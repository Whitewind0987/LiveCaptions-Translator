#pragma once
#include "recognition.hpp"
#include <filesystem>

namespace lct {
std::unique_ptr<IVadEngine> create_silero_vad(const std::filesystem::path& model_path);
std::unique_ptr<IWhisperEngine> create_whisper_engine(const std::filesystem::path& model_path,
    std::string language, int threads);
}
