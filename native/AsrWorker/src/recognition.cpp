#include "recognition.hpp"
#include <algorithm>
#include <cctype>
#include <cmath>

namespace lct {
namespace {
constexpr std::size_t vad_window_samples = 512;
std::int64_t to_ms(std::int64_t sample) { return sample * 1000 / 16000; }
}

void recognition_config::validate() const {
    if (!(speech_threshold > 0.0F && speech_threshold < 1.0F) || minimum_speech_samples <= 0 ||
        endpoint_silence_samples <= 0 || pre_roll_samples < 0 || post_roll_samples < 0 ||
        maximum_segment_samples < minimum_speech_samples || minimum_partial_samples <= 0 ||
        partial_cadence_samples <= 0 || drain_timeout <= std::chrono::seconds::zero())
        throw protocol_error("invalid recognition configuration");
}

StreamingRecognizer::StreamingRecognizer(std::unique_ptr<IVadEngine> vad, std::unique_ptr<IWhisperEngine> whisper,
    caption_sink sink, recognition_config config)
    : vad_(std::move(vad)), whisper_(std::move(whisper)), sink_(std::move(sink)), config_(config) {
    if (!vad_ || !whisper_ || !sink_) throw protocol_error("recognition dependency is missing");
    config_.validate();
    inference_thread_ = std::thread([this] { inference_loop(); });
}

StreamingRecognizer::~StreamingRecognizer() {
    { std::lock_guard lock(mutex_); stopping_ = true; pending_partial_.reset(); pending_final_.reset(); whisper_->request_cancel(); }
    work_changed_.notify_all();
    if (inference_thread_.joinable()) inference_thread_.join();
    diagnostics_.inference_thread_joined = true;
}

void StreamingRecognizer::start_stream(const guid& capture_session) {
    std::lock_guard lock(mutex_);
    if (stream_active_) throw protocol_error("recognition stream is already active");
    capture_ = capture_session; stream_active_ = true; caption_sequence_ = 0; next_segment_ = 1;
    reset_recognition_locked(false); emit_reset_locked();
}

void StreamingRecognizer::accept_frame(std::span<const std::int16_t> pcm, std::int64_t start_sample, bool source_gap) {
    if (pcm.size() != 320) throw protocol_error("recognition frame must contain 320 samples");
    std::unique_lock lock(mutex_);
    if (!stream_active_) throw protocol_error("recognition frame received outside active stream");
    if (source_gap) { reset_recognition_locked(true); }
    std::vector<float> converted;
    converted.reserve(pcm.size());
    for (const auto sample : pcm) converted.push_back(static_cast<float>(sample) / 32768.0F);
    if (vad_remainder_.empty()) vad_remainder_start_ = start_sample;
    vad_remainder_.insert(vad_remainder_.end(), converted.begin(), converted.end());
    while (vad_remainder_.size() >= vad_window_samples) {
        std::array<float, vad_window_samples> window{};
        std::copy_n(vad_remainder_.begin(), vad_window_samples, window.begin());
        const auto window_start = vad_remainder_start_;
        vad_remainder_.erase(vad_remainder_.begin(), vad_remainder_.begin() + static_cast<std::ptrdiff_t>(vad_window_samples));
        vad_remainder_start_ += static_cast<std::int64_t>(vad_window_samples);
        lock.unlock();
        process_window(window, window_start);
        lock.lock();
    }
}

void StreamingRecognizer::process_window(std::span<const float> window, std::int64_t window_start) {
    float probability{};
    try { probability = vad_->probability(window); }
    catch (const std::exception& exception) { throw recognition_exception(recognition_error_kind::vad_inference_failed, std::string("VAD inference failed: ") + exception.what()); }

    std::lock_guard lock(mutex_);
    if (!stream_active_) return;
    diagnostics_.vad_windows++;
    for (const auto sample : window) {
        pre_roll_.push_back(sample);
        if (pre_roll_.size() > static_cast<std::size_t>(config_.pre_roll_samples)) pre_roll_.pop_front();
    }
    if (probability >= config_.speech_threshold) {
        bool just_started = false;
        if (active_segment_ == 0) {
            active_segment_ = next_segment_;
            segment_start_ = std::max<std::int64_t>(0, window_start + static_cast<std::int64_t>(window.size()) - static_cast<std::int64_t>(pre_roll_.size()));
            segment_audio_.assign(pre_roll_.begin(), pre_roll_.end());
            diagnostics_.speech_regions_started++;
            last_partial_request_end_ = segment_start_;
            just_started = true;
        }
        if (!just_started) segment_audio_.insert(segment_audio_.end(), window.begin(), window.end());
        speech_samples_ += static_cast<std::int64_t>(window.size());
        silence_samples_ = 0;
    } else if (active_segment_ != 0) {
        segment_audio_.insert(segment_audio_.end(), window.begin(), window.end());
        silence_samples_ += static_cast<std::int64_t>(window.size());
    }

    if (active_segment_ != 0) {
        const auto end = segment_start_ + static_cast<std::int64_t>(segment_audio_.size());
        if (static_cast<std::int64_t>(segment_audio_.size()) >= config_.minimum_partial_samples &&
            end - last_partial_request_end_ >= config_.partial_cadence_samples) submit_partial();
        if (silence_samples_ >= config_.endpoint_silence_samples) {
            const auto excess = silence_samples_ - config_.post_roll_samples;
            if (excess > 0 && excess <= static_cast<std::int64_t>(segment_audio_.size()))
                segment_audio_.resize(segment_audio_.size() - static_cast<std::size_t>(excess));
            submit_final();
        } else if (static_cast<std::int64_t>(segment_audio_.size()) >= config_.maximum_segment_samples) submit_final();
    }
}

void StreamingRecognizer::submit_partial() {
    request work{false, capture_, generation_, active_segment_, active_revision_ + 1, segment_start_,
        segment_start_ + static_cast<std::int64_t>(segment_audio_.size()), segment_audio_, last_text_, active_revision_};
    if (pending_partial_) diagnostics_.partial_replaced++;
    pending_partial_ = std::move(work); diagnostics_.partial_submitted++;
    last_partial_request_end_ = pending_partial_->end_sample;
    work_changed_.notify_one();
}

void StreamingRecognizer::submit_final() {
    if (active_segment_ == 0) return;
    if (speech_samples_ < config_.minimum_speech_samples) {
        diagnostics_.speech_regions_discarded++; active_segment_ = 0; segment_audio_.clear(); silence_samples_ = 0; speech_samples_ = 0; return;
    }
    if (pending_final_) throw recognition_exception(recognition_error_kind::drain_timeout,
        "bounded recognition scheduler already has a mandatory Final request");
    pending_partial_.reset();
    const auto finalizing_segment = active_segment_;
    pending_final_ = request{true, capture_, generation_, finalizing_segment, active_revision_ + 1, segment_start_,
        segment_start_ + static_cast<std::int64_t>(segment_audio_.size()), std::move(segment_audio_), last_text_, active_revision_};
    next_segment_ = finalizing_segment + 1;
    active_segment_ = 0; active_revision_ = 0; last_text_.clear(); silence_samples_ = 0; speech_samples_ = 0; last_partial_request_end_ = 0;
    work_changed_.notify_one();
}

void StreamingRecognizer::inference_loop() {
    while (true) {
        request work;
        { std::unique_lock lock(mutex_); work_changed_.wait(lock, [&] { return stopping_ || pending_final_ || pending_partial_; });
            if (stopping_ && !pending_final_ && !pending_partial_) break;
            if (pending_final_) { work = std::move(*pending_final_); pending_final_.reset(); }
            else { work = std::move(*pending_partial_); pending_partial_.reset(); }
            inference_running_ = true;
        }
        const auto started = std::chrono::steady_clock::now();
        std::string text;
        try { text = normalize_text(whisper_->transcribe(work.audio)); }
        catch (const std::exception& exception) {
            std::lock_guard lock(mutex_); inference_failure_ = exception.what(); inference_running_ = false;
            pending_partial_.reset(); pending_final_.reset(); drain_changed_.notify_all(); continue;
        }
        const auto elapsed = std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::steady_clock::now() - started).count();
        { std::lock_guard lock(mutex_); diagnostics_.inference_count++; diagnostics_.latest_inference_ms = elapsed;
            diagnostics_.maximum_inference_ms = std::max(diagnostics_.maximum_inference_ms, elapsed); }
        publish_result(work, std::move(text));
        { std::lock_guard lock(mutex_); inference_running_ = false; drain_changed_.notify_all(); }
    }
    { std::lock_guard lock(mutex_); diagnostics_.inference_thread_joined = true; drain_changed_.notify_all(); }
}

void StreamingRecognizer::publish_result(const request& work, std::string text) {
    std::lock_guard lock(mutex_);
    if (!stream_active_ || work.capture != capture_ || work.generation != generation_) { diagnostics_.partial_stale++; return; }
    if (!work.final) {
        if (active_segment_ != work.segment || text.empty() || text == last_text_) { if (active_segment_ != work.segment) diagnostics_.partial_stale++; return; }
        active_revision_++; last_text_ = text; diagnostics_.partial_completed++;
        emit_text_locked(caption_kind::partial, work.segment, active_revision_, text, work.start_sample, work.end_sample);
        return;
    }
    diagnostics_.final_completed++;
    if (text.empty()) {
        if (!work.prior_text.empty()) emit_reset_locked();
        diagnostics_.speech_regions_discarded++;
        return;
    }
    const auto revision = text == work.prior_text && work.prior_revision > 0
        ? work.prior_revision : work.prior_revision + 1;
    emit_text_locked(caption_kind::committed, work.segment, revision, text, work.start_sample, work.end_sample);
    emit_text_locked(caption_kind::final, work.segment, revision, text, work.start_sample, work.end_sample);
    diagnostics_.speech_regions_completed++;
}

void StreamingRecognizer::end_stream() {
    std::unique_lock lock(mutex_);
    if (!stream_active_) throw protocol_error("recognition stream is not active");
    if (!vad_remainder_.empty()) {
        std::array<float, vad_window_samples> window{};
        std::copy(vad_remainder_.begin(), vad_remainder_.end(), window.begin());
        const auto start = vad_remainder_start_; vad_remainder_.clear();
        lock.unlock(); process_window(window, start); lock.lock();
    }
    if (active_segment_ != 0) submit_final();
    wait_for_final_locked(lock);
    stream_active_ = false; generation_++; vad_->reset(); vad_remainder_.clear(); pre_roll_.clear();
}

void StreamingRecognizer::wait_for_final_locked(std::unique_lock<std::mutex>& lock) {
    const auto completed = drain_changed_.wait_for(lock, config_.drain_timeout,
        [&] { return !pending_final_ && !inference_running_; });
    if (!completed) { stopping_ = true; whisper_->request_cancel(); work_changed_.notify_all(); throw recognition_exception(recognition_error_kind::drain_timeout, "recognition drain timed out"); }
    if (inference_failure_) throw recognition_exception(recognition_error_kind::whisper_inference_failed,
        "Whisper inference failed: " + *inference_failure_);
}

void StreamingRecognizer::abort_stream() {
    std::lock_guard lock(mutex_); stream_active_ = false; reset_recognition_locked(false);
}

void StreamingRecognizer::reset_recognition_locked(bool emit_reset) {
    generation_++; vad_->reset(); vad_remainder_.clear(); pre_roll_.clear(); segment_audio_.clear();
    pending_partial_.reset(); active_segment_ = 0; active_revision_ = 0; silence_samples_ = 0; speech_samples_ = 0;
    pending_final_.reset(); inference_failure_.reset(); last_partial_request_end_ = 0; last_text_.clear(); next_segment_ = 1;
    if (emit_reset) emit_reset_locked();
}

void StreamingRecognizer::emit_reset_locked() {
    sink_(encode_caption_event(capture_, ++caption_sequence_, 0, 0, caption_kind::reset, {}, {}, {}));
    diagnostics_.caption_resets++; diagnostics_.last_caption_sequence = caption_sequence_; next_segment_ = 1;
}

void StreamingRecognizer::emit_text_locked(caption_kind kind, std::int64_t segment, std::int64_t revision,
    std::string_view text, std::int64_t start_sample, std::int64_t end_sample) {
    sink_(encode_caption_event(capture_, ++caption_sequence_, segment, revision, kind, text, to_ms(start_sample), to_ms(end_sample)));
    if (kind == caption_kind::partial) diagnostics_.caption_partials++;
    else if (kind == caption_kind::committed) diagnostics_.caption_committed++;
    else if (kind == caption_kind::final) diagnostics_.caption_finals++;
    diagnostics_.last_caption_sequence = caption_sequence_;
}

std::string StreamingRecognizer::normalize_text(std::string text) {
    std::string result; bool whitespace = true;
    for (const unsigned char character : text) {
        if (std::isspace(character)) { whitespace = !result.empty(); continue; }
        if (whitespace && !result.empty()) result.push_back(' ');
        result.push_back(static_cast<char>(character)); whitespace = false;
    }
    return result;
}

recognition_diagnostics StreamingRecognizer::diagnostics() const { std::lock_guard lock(mutex_); return diagnostics_; }

std::vector<std::uint8_t> encode_caption_event(const guid& session, std::int64_t sequence,
    std::int64_t segment, std::int64_t revision, caption_kind kind, std::string_view text,
    std::optional<std::int64_t> start_ms, std::optional<std::int64_t> end_ms) {
    std::vector<std::uint8_t> payload;
    put_i32(payload, 1); put_guid(payload, session); put_i64(payload, sequence); put_i64(payload, segment);
    put_i64(payload, revision); put_u16(payload, static_cast<std::uint16_t>(kind)); put_string(payload, text, 32768);
    payload.push_back(start_ms ? 1 : 0); if (start_ms) put_i64(payload, *start_ms);
    payload.push_back(end_ms ? 1 : 0); if (end_ms) put_i64(payload, *end_ms);
    const auto now = std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::system_clock::now().time_since_epoch()).count();
    put_i64(payload, now); return payload;
}
}
