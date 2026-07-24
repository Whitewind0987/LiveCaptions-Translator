#pragma once
#include "protocol.hpp"
#include <chrono>
#include <condition_variable>
#include <cstdint>
#include <deque>
#include <functional>
#include <memory>
#include <mutex>
#include <optional>
#include <span>
#include <string>
#include <thread>
#include <vector>

namespace lct {
enum class caption_kind : std::uint16_t { partial = 0, committed = 1, final = 2, reset = 3 };
enum class recognition_error_kind : std::uint16_t { invalid_configuration = 5, model_load_failed = 6, vad_inference_failed = 7, whisper_inference_failed = 8, drain_timeout = 9 };
struct recognition_exception : protocol_error {
    recognition_exception(recognition_error_kind value, std::string message) : protocol_error(std::move(message)), kind(value) {}
    recognition_error_kind kind;
};

struct recognition_config {
    float speech_threshold{0.50F};
    std::int64_t minimum_speech_samples{4000};
    std::int64_t endpoint_silence_samples{8000};
    std::int64_t pre_roll_samples{3200};
    std::int64_t post_roll_samples{1600};
    std::int64_t maximum_segment_samples{320000};
    std::int64_t minimum_partial_samples{12800};
    std::int64_t partial_cadence_samples{16000};
    std::chrono::seconds drain_timeout{120};
    void validate() const;
};

struct recognition_diagnostics {
    bool enabled{};
    std::uint64_t vad_windows{};
    std::uint64_t speech_regions_started{};
    std::uint64_t speech_regions_completed{};
    std::uint64_t speech_regions_discarded{};
    std::uint64_t partial_submitted{};
    std::uint64_t partial_replaced{};
    std::uint64_t partial_completed{};
    std::uint64_t partial_stale{};
    std::uint64_t final_completed{};
    std::uint64_t caption_resets{};
    std::uint64_t caption_partials{};
    std::uint64_t caption_committed{};
    std::uint64_t caption_finals{};
    std::uint64_t inference_count{};
    std::int64_t latest_inference_ms{};
    std::int64_t maximum_inference_ms{};
    std::int64_t last_caption_sequence{};
    bool inference_thread_joined{};
};

class IVadEngine {
public:
    virtual ~IVadEngine() = default;
    virtual float probability(std::span<const float> samples) = 0;
    virtual void reset() = 0;
};

class IWhisperEngine {
public:
    virtual ~IWhisperEngine() = default;
    virtual std::string transcribe(std::span<const float> samples) = 0;
    virtual void request_cancel() { }
};

using caption_sink = std::function<void(std::span<const std::uint8_t>)>;

class StreamingRecognizer {
public:
    StreamingRecognizer(std::unique_ptr<IVadEngine> vad, std::unique_ptr<IWhisperEngine> whisper,
                        caption_sink sink, recognition_config config = {});
    ~StreamingRecognizer();
    StreamingRecognizer(const StreamingRecognizer&) = delete;
    StreamingRecognizer& operator=(const StreamingRecognizer&) = delete;

    void start_stream(const guid& capture_session);
    void accept_frame(std::span<const std::int16_t> pcm, std::int64_t start_sample, bool source_gap);
    void end_stream();
    void abort_stream();
    recognition_diagnostics diagnostics() const;

private:
    struct request {
        bool final{};
        guid capture{};
        std::uint64_t generation{};
        std::int64_t segment{};
        std::int64_t requested_revision{};
        std::int64_t start_sample{};
        std::int64_t end_sample{};
        std::vector<float> audio;
        std::string prior_text;
        std::int64_t prior_revision{};
    };

    void inference_loop();
    void process_window(std::span<const float> window, std::int64_t window_start);
    void submit_partial();
    void submit_final();
    void publish_result(const request& work, std::string text);
    void emit_reset_locked();
    void emit_text_locked(caption_kind kind, std::int64_t segment, std::int64_t revision,
                          std::string_view text, std::int64_t start_sample, std::int64_t end_sample);
    void reset_recognition_locked(bool emit_reset);
    void wait_for_final_locked(std::unique_lock<std::mutex>& lock);
    static std::string normalize_text(std::string text);

    std::unique_ptr<IVadEngine> vad_;
    std::unique_ptr<IWhisperEngine> whisper_;
    caption_sink sink_;
    recognition_config config_;
    mutable std::mutex mutex_;
    std::condition_variable work_changed_;
    std::condition_variable drain_changed_;
    std::thread inference_thread_;
    bool stopping_{};
    bool stream_active_{};
    bool inference_running_{};
    guid capture_{};
    std::uint64_t generation_{};
    std::int64_t caption_sequence_{};
    std::int64_t next_segment_{1};
    std::int64_t active_segment_{};
    std::int64_t active_revision_{};
    std::int64_t segment_start_{};
    std::int64_t last_partial_request_end_{};
    std::int64_t silence_samples_{};
    std::int64_t speech_samples_{};
    std::string last_text_;
    std::vector<float> vad_remainder_;
    std::int64_t vad_remainder_start_{};
    std::deque<float> pre_roll_;
    std::vector<float> segment_audio_;
    std::optional<request> pending_partial_;
    std::optional<request> pending_final_;
    recognition_diagnostics diagnostics_{true};
    std::optional<std::string> inference_failure_;
};

std::vector<std::uint8_t> encode_caption_event(const guid& session, std::int64_t sequence,
    std::int64_t segment, std::int64_t revision, caption_kind kind, std::string_view text,
    std::optional<std::int64_t> start_ms, std::optional<std::int64_t> end_ms);
}
