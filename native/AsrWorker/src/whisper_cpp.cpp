#include "recognition_adapters.hpp"
#include <whisper.h>
#include <algorithm>
#include <cctype>
#include <atomic>

namespace lct {
namespace {
class whisper_engine final : public IWhisperEngine {
public:
    whisper_engine(const std::filesystem::path& path, std::string language, int threads)
        : language_(std::move(language)), threads_(threads) {
        auto parameters = whisper_context_default_params();
        parameters.use_gpu = false;
        context_ = whisper_init_from_file_with_params(path.string().c_str(), parameters);
        if (!context_) throw protocol_error("whisper.cpp model load failed");
    }
    ~whisper_engine() override { if (context_) whisper_free(context_); }

    std::string transcribe(std::span<const float> samples) override {
        cancel_ = false;
        auto parameters = whisper_full_default_params(WHISPER_SAMPLING_GREEDY);
        parameters.n_threads = threads_;
        parameters.translate = false;
        parameters.no_context = true;
        parameters.no_timestamps = false;
        parameters.single_segment = false;
        parameters.print_special = false;
        parameters.print_progress = false;
        parameters.print_realtime = false;
        parameters.print_timestamps = false;
        parameters.suppress_blank = true;
        parameters.suppress_nst = true;
        parameters.language = language_ == "auto" ? nullptr : language_.c_str();
        parameters.detect_language = language_ == "auto";
        parameters.abort_callback = [](void* context) { return static_cast<whisper_engine*>(context)->cancel_.load(); };
        parameters.abort_callback_user_data = this;
        if (whisper_full(context_, parameters, samples.data(), static_cast<int>(samples.size())) != 0)
            throw protocol_error("whisper.cpp inference returned failure");
        std::string text;
        const auto count = whisper_full_n_segments(context_);
        for (int index = 0; index < count; ++index) {
            const auto* value = whisper_full_get_segment_text(context_, index);
            if (value) text += value;
        }
        return text;
    }
    void request_cancel() override { cancel_ = true; }
private:
    whisper_context* context_{};
    std::string language_;
    int threads_{};
    std::atomic_bool cancel_{};
};
}

std::unique_ptr<IWhisperEngine> create_whisper_engine(const std::filesystem::path& model_path,
    std::string language, int threads) {
    return std::make_unique<whisper_engine>(model_path, std::move(language), threads);
}
}
