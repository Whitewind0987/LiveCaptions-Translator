#include "recognition_adapters.hpp"
#include <onnxruntime_cxx_api.h>
#include <algorithm>
#include <array>

namespace lct {
namespace {
class silero_vad final : public IVadEngine {
public:
    explicit silero_vad(const std::filesystem::path& path)
        : environment_(ORT_LOGGING_LEVEL_WARNING, "LiveCaptionsAsrWorker"),
          options_(make_options()),
          session_(environment_, path.c_str(), options_) {
        validate_metadata();
        reset();
    }

    float probability(std::span<const float> samples) override {
        if (samples.size() != 512) throw protocol_error("Silero requires exactly 512 new 16 kHz samples");
        std::array<float, 576> input{};
        std::copy(context_.begin(), context_.end(), input.begin());
        std::copy(samples.begin(), samples.end(), input.begin() + 64);
        const std::array<std::int64_t, 2> input_shape{1, 576};
        const std::array<std::int64_t, 3> state_shape{2, 1, 128};
        std::int64_t sample_rate = 16000;
        auto memory = Ort::MemoryInfo::CreateCpu(OrtArenaAllocator, OrtMemTypeDefault);
        std::array<Ort::Value, 3> values{
            Ort::Value::CreateTensor<float>(memory, input.data(), input.size(), input_shape.data(), input_shape.size()),
            Ort::Value::CreateTensor<float>(memory, state_.data(), state_.size(), state_shape.data(), state_shape.size()),
            Ort::Value::CreateTensor<std::int64_t>(memory, &sample_rate, 1, nullptr, 0)};
        constexpr std::array<const char*, 3> inputs{"input", "state", "sr"};
        constexpr std::array<const char*, 2> outputs{"output", "stateN"};
        auto result = session_.Run(Ort::RunOptions{nullptr}, inputs.data(), values.data(), values.size(), outputs.data(), outputs.size());
        const auto probability_value = result[0].GetTensorData<float>()[0];
        const auto state_info = result[1].GetTensorTypeAndShapeInfo();
        if (state_info.GetElementCount() != state_.size()) throw protocol_error("Silero returned an invalid recurrent state");
        std::copy_n(result[1].GetTensorData<float>(), state_.size(), state_.begin());
        std::copy(samples.end() - 64, samples.end(), context_.begin());
        return probability_value;
    }

    void reset() override { state_.fill(0.0F); context_.fill(0.0F); }

private:
    static Ort::SessionOptions make_options() { Ort::SessionOptions value; value.SetIntraOpNumThreads(1); return value; }
    void validate_metadata() {
        if (session_.GetInputCount() != 3 || session_.GetOutputCount() != 2)
            throw protocol_error("Silero model must expose 3 inputs and 2 outputs");
        Ort::AllocatorWithDefaultOptions allocator;
        constexpr std::array<const char*, 3> input_names{"input", "state", "sr"};
        constexpr std::array<const char*, 2> output_names{"output", "stateN"};
        for (std::size_t index = 0; index < input_names.size(); ++index) {
            const auto name = session_.GetInputNameAllocated(index, allocator);
            if (std::string_view(name.get()) != input_names[index]) throw protocol_error("Silero input metadata does not match pinned v6.2.1 interface");
        }
        for (std::size_t index = 0; index < output_names.size(); ++index) {
            const auto name = session_.GetOutputNameAllocated(index, allocator);
            if (std::string_view(name.get()) != output_names[index]) throw protocol_error("Silero output metadata does not match pinned v6.2.1 interface");
        }
        const auto input = session_.GetInputTypeInfo(0).GetTensorTypeAndShapeInfo();
        const auto state = session_.GetInputTypeInfo(1).GetTensorTypeAndShapeInfo();
        const auto rate = session_.GetInputTypeInfo(2).GetTensorTypeAndShapeInfo();
        const auto input_shape = input.GetShape(); const auto state_shape = state.GetShape();
        const auto valid_input_shape = input_shape.size() == 2 && (input_shape[0] == -1 || input_shape[0] == 1) && (input_shape[1] == -1 || input_shape[1] == 576);
        const auto valid_state_shape = state_shape.size() == 3 && state_shape[0] == 2 && (state_shape[1] == -1 || state_shape[1] == 1) && state_shape[2] == 128;
        if (input.GetElementType() != ONNX_TENSOR_ELEMENT_DATA_TYPE_FLOAT || !valid_input_shape ||
            state.GetElementType() != ONNX_TENSOR_ELEMENT_DATA_TYPE_FLOAT || !valid_state_shape ||
            rate.GetElementType() != ONNX_TENSOR_ELEMENT_DATA_TYPE_INT64)
            throw protocol_error("Silero tensor metadata does not match pinned v6.2.1 interface");
    }

    Ort::Env environment_;
    Ort::SessionOptions options_;
    Ort::Session session_;
    std::array<float, 256> state_{};
    std::array<float, 64> context_{};
};
}

std::unique_ptr<IVadEngine> create_silero_vad(const std::filesystem::path& model_path) {
    try { return std::make_unique<silero_vad>(model_path); }
    catch (const Ort::Exception& exception) { throw protocol_error(std::string("Silero model load failed: ") + exception.what()); }
}
}
