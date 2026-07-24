#include "recognition.hpp"
#include <atomic>
#include <iostream>
#include <mutex>
#include <stdexcept>

namespace {
using namespace lct;
void require(bool value, const char* message) { if (!value) throw std::runtime_error(message); }

class fake_vad final : public IVadEngine {
public:
    explicit fake_vad(float probability_value) : value(probability_value) {}
    float probability(std::span<const float> samples) override {
        require(samples.size() == 512, "VAD window is not 512 samples");
        windows.emplace_back(samples.begin(), samples.end()); return value;
    }
    void reset() override { resets++; }
    float value; int resets{}; std::vector<std::vector<float>> windows;
};

class fake_whisper final : public IWhisperEngine {
public:
    explicit fake_whisper(std::string result) : result_(std::move(result)) {}
    std::string transcribe(std::span<const float> samples) override {
        const auto active = ++active_; maximum_ = std::max(maximum_.load(), active);
        sizes.push_back(samples.size()); --active_; return result_;
    }
    int maximum() const { return maximum_; }
    std::vector<std::size_t> sizes;
private:
    std::string result_; std::atomic_int active_{}; std::atomic_int maximum_{};
};

struct decoded { std::int64_t sequence{}, segment{}, revision{}; caption_kind kind{}; std::string text; };
decoded decode(std::span<const std::uint8_t> payload) {
    std::size_t offset = 0; require(get_i32(payload, offset) == 1, "schema"); (void)get_guid(payload, offset);
    decoded value; value.sequence = get_i64(payload, offset); value.segment = get_i64(payload, offset);
    value.revision = get_i64(payload, offset); value.kind = static_cast<caption_kind>(get_u16(payload, offset));
    value.text = get_string(payload, offset, 32768); return value;
}

guid session() { guid value{}; value.bytes[0] = 1; return value; }
std::array<std::int16_t, 320> frame(std::int16_t value) { std::array<std::int16_t, 320> result{}; result.fill(value); return result; }

void silence_and_window_test() {
    auto vad = std::make_unique<fake_vad>(0.0F); auto* vad_ptr = vad.get();
    auto whisper = std::make_unique<fake_whisper>("must not run"); auto* whisper_ptr = whisper.get();
    std::vector<decoded> events;
    { StreamingRecognizer recognizer(std::move(vad), std::move(whisper), [&](auto payload) { events.push_back(decode(payload)); });
      recognizer.start_stream(session());
      for (int index = 0; index < 4; ++index) recognizer.accept_frame(frame(0), index * 320, false);
      recognizer.end_stream();
      const auto diagnostics = recognizer.diagnostics(); require(diagnostics.vad_windows == 3, "incomplete VAD carry or flush failed");
      require(whisper_ptr->sizes.empty(), "silence invoked Whisper"); require(vad_ptr->resets >= 2, "VAD state was not reset"); }
    require(events.size() == 1 && events[0].kind == caption_kind::reset && events[0].sequence == 1, "silence emitted text");
}

void speech_lifecycle_test() {
    auto vad = std::make_unique<fake_vad>(0.9F);
    auto whisper = std::make_unique<fake_whisper>("  structured   caption events  "); auto* whisper_ptr = whisper.get();
    std::vector<decoded> events;
    { StreamingRecognizer recognizer(std::move(vad), std::move(whisper), [&](auto payload) { events.push_back(decode(payload)); });
      recognizer.start_stream(session());
      for (int index = 0; index < 50; ++index) recognizer.accept_frame(frame(1000), index * 320, false);
      recognizer.end_stream(); require(whisper_ptr->maximum() == 1, "concurrent Whisper calls occurred"); }
    require(events.front().kind == caption_kind::reset, "Reset was not first");
    require(events.size() >= 3, "final lifecycle missing");
    const auto committed = events[events.size() - 2]; const auto final = events.back();
    require(committed.kind == caption_kind::committed && final.kind == caption_kind::final, "Committed/Final order invalid");
    require(committed.segment == 1 && committed.revision == final.revision && committed.text == "structured caption events", "final identity or normalization invalid");
    for (std::size_t index = 0; index < events.size(); ++index) require(events[index].sequence == static_cast<std::int64_t>(index + 1), "caption sequence invalid");
}

void minimum_and_gap_test() {
    auto vad = std::make_unique<fake_vad>(0.9F); auto* vad_ptr = vad.get();
    auto whisper = std::make_unique<fake_whisper>("text");
    std::vector<decoded> events;
    { StreamingRecognizer recognizer(std::move(vad), std::move(whisper), [&](auto payload) { events.push_back(decode(payload)); });
      recognizer.start_stream(session()); recognizer.accept_frame(frame(1000), 0, false);
      recognizer.accept_frame(frame(1000), 640, true); recognizer.end_stream(); require(vad_ptr->resets >= 3, "source gap did not reset VAD state"); }
    require(events.size() == 2 && events[1].kind == caption_kind::reset && events[1].sequence == 2, "source gap Reset invalid");
}
}

int main() {
    try { silence_and_window_test(); speech_lifecycle_test(); minimum_and_gap_test(); std::cout << "Stage 5 recognition tests passed\n"; return 0; }
    catch (const std::exception& exception) { std::cerr << exception.what() << '\n'; return 1; }
}
