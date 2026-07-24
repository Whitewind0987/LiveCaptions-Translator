#include "protocol.hpp"
#ifdef LCT_ENABLE_RECOGNITION
#include "recognition.hpp"
#include "recognition_adapters.hpp"
#endif
#include <windows.h>
#include <atomic>
#include <chrono>
#include <condition_variable>
#include <iostream>
#include <mutex>
#include <thread>
#include <filesystem>
#include <set>
#include <algorithm>
#include <cstring>

namespace {
using namespace lct;

class handle {
    HANDLE value_{INVALID_HANDLE_VALUE};
public:
    handle() = default;
    explicit handle(HANDLE value) : value_(value) {}
    ~handle() { if (valid()) CloseHandle(value_); }
    handle(const handle&) = delete;
    handle& operator=(const handle&) = delete;
    HANDLE get() const { return value_; }
    bool valid() const { return value_ != INVALID_HANDLE_VALUE && value_ != nullptr; }
};

struct args {
    std::wstring control, audio, session, vad_model, whisper_model;
    std::string language{"auto"};
    DWORD parent{};
    int threads{};
    bool recognition() const { return !vad_model.empty() || !whisper_model.empty(); }
};

std::string narrow_ascii(std::wstring_view value) {
    std::string result;
    for (const auto character : value) {
        if (character > 127) throw protocol_error("language must be an ASCII language code");
        result.push_back(static_cast<char>(character));
    }
    return result;
}

args parse(int argc, wchar_t** argv) {
    args result;
    std::set<std::wstring> seen;
    if ((argc - 1) % 2 != 0) throw protocol_error("worker argument has no value");
    for (int index = 1; index < argc; index += 2) {
        std::wstring key = argv[index], value = argv[index + 1];
        if (!seen.insert(key).second) throw protocol_error("duplicate worker argument");
        if (key == L"--control-pipe") result.control = value;
        else if (key == L"--audio-pipe") result.audio = value;
        else if (key == L"--session") result.session = value;
        else if (key == L"--parent-pid") { try { result.parent = std::stoul(value); } catch (...) { throw protocol_error("invalid parent PID"); } }
        else if (key == L"--vad-model") result.vad_model = value;
        else if (key == L"--whisper-model") result.whisper_model = value;
        else if (key == L"--language") result.language = narrow_ascii(value);
        else if (key == L"--threads") { try { result.threads = std::stoi(value); } catch (...) { throw protocol_error("invalid recognition thread count"); } }
        else throw protocol_error("unknown argument");
    }
    if (result.control.empty() || result.audio.empty() || result.session.empty() || !result.parent) throw protocol_error("missing argument");
    if (result.recognition()) {
        if (result.vad_model.empty() || result.whisper_model.empty()) throw protocol_error("both recognition model paths are required");
        if (!std::filesystem::path(result.vad_model).is_absolute() || !std::filesystem::is_regular_file(result.vad_model) ||
            !std::filesystem::path(result.whisper_model).is_absolute() || !std::filesystem::is_regular_file(result.whisper_model))
            throw protocol_error("recognition model paths must be absolute existing files");
        if (result.threads < 1 || result.threads > 32) throw protocol_error("recognition thread count must be between 1 and 32");
        if (result.language.empty() || result.language.size() > 12 || !std::all_of(result.language.begin(), result.language.end(), [](char value) { return (value >= 'a' && value <= 'z') || value == '-'; }))
            throw protocol_error("invalid recognition language");
#ifndef LCT_ENABLE_RECOGNITION
        throw protocol_error("this worker was built without recognition support");
#endif
    } else if (seen.contains(L"--language") || seen.contains(L"--threads")) throw protocol_error("recognition options require both model paths");
    return result;
}

handle connect_pipe(const std::wstring& name) {
    const std::wstring path = L"\\\\.\\pipe\\" + name;
    const auto deadline = GetTickCount64() + 5000;
    do {
        HANDLE pipe = CreateFileW(path.c_str(), GENERIC_READ | GENERIC_WRITE, 0, nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (pipe != INVALID_HANDLE_VALUE) return handle(pipe);
        if (GetLastError() == ERROR_PIPE_BUSY) WaitNamedPipeW(path.c_str(), 100); else Sleep(25);
    } while (GetTickCount64() < deadline);
    throw protocol_error("pipe connection timeout");
}

void read_exact(HANDLE pipe, std::span<std::uint8_t> bytes) {
    std::size_t offset = 0;
    while (offset < bytes.size()) {
        DWORD read = 0;
        if (!ReadFile(pipe, bytes.data() + offset, static_cast<DWORD>(bytes.size() - offset), &read, nullptr) || read == 0) throw protocol_error("pipe read failed");
        offset += read;
    }
}

void write_exact(HANDLE pipe, std::span<const std::uint8_t> bytes) {
    std::size_t offset = 0;
    while (offset < bytes.size()) {
        DWORD written = 0;
        if (!WriteFile(pipe, bytes.data() + offset, static_cast<DWORD>(bytes.size() - offset), &written, nullptr) || written == 0) throw protocol_error("pipe write failed");
        offset += written;
    }
}

struct message { envelope env; std::vector<std::uint8_t> payload; };

message read_message(HANDLE pipe, std::uint32_t maximum, std::uint64_t& last_sequence) {
    std::array<std::uint8_t, envelope_size> header{};
    read_exact(pipe, header);
    auto decoded = decode_envelope(header, maximum);
    accept_sequence(last_sequence, decoded.sequence);
    std::vector<std::uint8_t> payload(decoded.payload_length);
    read_exact(pipe, payload);
    return { decoded, std::move(payload) };
}

bool empty_guid(const guid& value) {
    for (const auto byte : value.bytes) if (byte != 0) return false;
    return true;
}

class serialized_writer {
    HANDLE pipe_;
    std::mutex mutex_;
    std::uint64_t sequence_{};
public:
    explicit serialized_writer(HANDLE pipe) : pipe_(pipe) {}
    void send(message_type type, std::span<const std::uint8_t> payload, const guid& correlation = {}) {
        std::lock_guard lock(mutex_);
        envelope header{ major, minor, static_cast<std::uint16_t>(type), 0, static_cast<std::uint32_t>(payload.size()), ++sequence_, correlation };
        const auto encoded = encode_envelope(header);
        write_exact(pipe_, encoded);
        write_exact(pipe_, payload);
    }
};

std::vector<std::uint8_t> ready(const guid& session) {
    std::vector<std::uint8_t> payload;
    put_guid(payload, session);
    put_i32(payload, static_cast<std::int32_t>(GetCurrentProcessId()));
    return payload;
}

std::vector<std::uint8_t> identity_payload(const guid& worker, const guid& capture) {
    std::vector<std::uint8_t> payload;
    put_guid(payload, worker);
    put_guid(payload, capture);
    return payload;
}

std::vector<std::uint8_t> error_payload(std::uint16_t kind, std::string_view diagnostic) {
    std::vector<std::uint8_t> error; put_u16(error, kind); put_string(error, diagnostic); return error;
}

void cancel_thread(HANDLE thread) {
    if (thread != nullptr && thread != INVALID_HANDLE_VALUE) CancelSynchronousIo(thread);
}

void join_bounded(std::thread& thread) {
    if (!thread.joinable()) return;
    if (WaitForSingleObject(thread.native_handle(), 5000) != WAIT_OBJECT_0) TerminateProcess(GetCurrentProcess(), 22);
    thread.join();
}
}

int wmain(int argc, wchar_t** argv) {
    try {
        const auto arguments = parse(argc, argv);
        const auto session = parse_guid(arguments.session);
        wchar_t nonce_text[129]{};
        const auto nonce_length = GetEnvironmentVariableW(L"LIVE_CAPTIONS_ASR_NONCE", nonce_text, 129);
        if (nonce_length != 64) throw protocol_error("missing nonce");
        const auto nonce = parse_hex(std::wstring_view(nonce_text, nonce_length));
        SecureZeroMemory(nonce_text, sizeof(nonce_text));

        auto control = connect_pipe(arguments.control);
        auto audio = connect_pipe(arguments.audio);
        serialized_writer control_writer(control.get());
        serialized_writer audio_writer(audio.get());
        std::atomic_bool stopping = false;
        std::atomic<HANDLE> audio_thread_handle = nullptr;
        std::mutex statistics_mutex;
        std::condition_variable audio_end_changed;
        bool audio_end_received = false;
        stream_lifecycle audio_lifecycle;
        stream_statistics statistics{};
        std::uint64_t control_sequence = 0;
        std::uint64_t audio_sequence = 0;
        DWORD test_audio_delay_ms = 0;
        wchar_t test_delay[16]{};
        if (const auto length = GetEnvironmentVariableW(L"LIVE_CAPTIONS_ASR_TEST_AUDIO_DELAY_MS", test_delay, 16); length > 0 && length < 16) test_audio_delay_ms = std::stoul(test_delay);

        handle parent(OpenProcess(SYNCHRONIZE, FALSE, arguments.parent));
        if (!parent.valid()) throw protocol_error("parent monitoring unavailable");
        HANDLE current_thread = nullptr;
        if (!DuplicateHandle(GetCurrentProcess(), GetCurrentThread(), GetCurrentProcess(), &current_thread, THREAD_TERMINATE, FALSE, 0)) throw protocol_error("control thread monitoring unavailable");
        handle control_thread(current_thread);

        std::thread parent_monitor([&] {
            while (!stopping) {
                const auto wait = WaitForSingleObject(parent.get(), 250);
                if (wait == WAIT_OBJECT_0 || wait == WAIT_FAILED) {
                    stopping = true;
                    cancel_thread(control_thread.get());
                    cancel_thread(audio_thread_handle.load());
                    return;
                }
            }
        });

        guid control_correlation{};
        control_correlation.bytes[15] = 1;
        guid audio_correlation{};
        audio_correlation.bytes[15] = 2;
        const auto pid = static_cast<std::int32_t>(GetCurrentProcessId());
        const auto capabilities = arguments.recognition() ? 47ULL : 3ULL;
        const auto control_hello = encode_worker_hello(session, nonce, pid, "stage5-1.0.0", capabilities);
        const auto audio_hello = encode_audio_pipe_hello(session, nonce, pid);
        control_writer.send(message_type::worker_hello, control_hello, control_correlation);
        audio_writer.send(message_type::audio_pipe_hello, audio_hello, audio_correlation);

        const auto audio_accept = read_message(audio.get(), maximum_control_payload, audio_sequence);
        if (audio_accept.env.type != static_cast<std::uint16_t>(message_type::audio_pipe_accepted) || audio_accept.env.correlation != audio_correlation) throw protocol_error("invalid audio pipe acceptance response");
        decode_audio_pipe_accepted(audio_accept.payload, session, pid);

        const auto accept = read_message(control.get(), maximum_control_payload, control_sequence);
        if (accept.env.type == static_cast<std::uint16_t>(message_type::host_reject)) throw protocol_error("host rejected handshake");
        if (accept.env.type != static_cast<std::uint16_t>(message_type::host_accept) || accept.env.correlation != control_correlation) throw protocol_error("expected correlated host accept");
        std::size_t accept_offset = 0;
        if (get_guid(accept.payload, accept_offset) != session || get_u16(accept.payload, accept_offset) != 0 || get_i32(accept.payload, accept_offset) != 16000 || get_u16(accept.payload, accept_offset) != 1 || get_u16(accept.payload, accept_offset) != 16 || get_u16(accept.payload, accept_offset) != 20 || get_u32(accept.payload, accept_offset) != 320 || get_u32(accept.payload, accept_offset) != 640 || accept_offset != accept.payload.size()) throw protocol_error("invalid host accept");
        #ifdef LCT_ENABLE_RECOGNITION
        std::unique_ptr<StreamingRecognizer> recognizer;
        if (arguments.recognition()) {
            try {
                auto vad = create_silero_vad(arguments.vad_model);
                auto whisper = create_whisper_engine(arguments.whisper_model, arguments.language, arguments.threads);
                recognizer = std::make_unique<StreamingRecognizer>(std::move(vad), std::move(whisper),
                    [&](std::span<const std::uint8_t> payload) { control_writer.send(message_type::caption_event, payload); });
            }
            catch (const std::exception& exception) {
                control_writer.send(message_type::error, error_payload(static_cast<std::uint16_t>(recognition_error_kind::model_load_failed), exception.what()));
                stopping = true; join_bounded(parent_monitor); return 23;
            }
        }
        #endif
        control_writer.send(message_type::worker_ready, ready(session), control_correlation);

        std::thread audio_reader([&] {
            try {
                while (!stopping) {
                    const auto incoming = read_message(audio.get(), audio_payload_size, audio_sequence);
                    if (is_optional_unknown(incoming.env.type, incoming.env.flags)) continue;
                    if (!empty_guid(incoming.env.correlation)) throw protocol_error("audio message correlation is not empty");
                    if (incoming.env.type == static_cast<std::uint16_t>(message_type::audio_frame)) {
                        const auto metadata = decode_audio_metadata(incoming.payload);
                        bool gap = false;
                        { std::lock_guard lock(statistics_mutex);
                            audio_lifecycle.frame();
                            const auto previous_gaps = statistics.gaps;
                            if (!validate_audio(statistics, metadata)) { statistics.invalid++; continue; }
                            gap = statistics.gaps != previous_gaps;
                            if (statistics.frames % 50 == 0) control_writer.send(message_type::audio_progress, encode_summary(statistics));
                        }
                        #ifdef LCT_ENABLE_RECOGNITION
                        if (recognizer) {
                            std::array<std::int16_t, 320> pcm{};
                            std::memcpy(pcm.data(), incoming.payload.data() + 60, pcm_bytes);
                            recognizer->accept_frame(pcm, metadata.sample_index, gap);
                        }
                        #endif
                        if (test_audio_delay_ms != 0) Sleep(test_audio_delay_ms);
                    }
                    else if (incoming.env.type == static_cast<std::uint16_t>(message_type::audio_stream_end)) {
                        const auto end = decode_audio_stream_end(incoming.payload);
                        { std::lock_guard lock(statistics_mutex);
                            audio_lifecycle.frame();
                            if (!validate_audio_stream_end(statistics, end)) throw protocol_error("audio stream end totals or identity do not match");
                            audio_lifecycle.end();
                        }
                        #ifdef LCT_ENABLE_RECOGNITION
                        if (recognizer) recognizer->end_stream();
                        #endif
                        { std::lock_guard lock(statistics_mutex); audio_end_received = true; }
                        audio_end_changed.notify_all();
                    }
                    else throw protocol_error("invalid audio message");
                }
            }
            catch (const std::exception& exception) {
                if (!stopping) {
                    auto kind = static_cast<std::uint16_t>(0);
                    #ifdef LCT_ENABLE_RECOGNITION
                    if (const auto* recognition_error = dynamic_cast<const recognition_exception*>(&exception)) kind = static_cast<std::uint16_t>(recognition_error->kind);
                    #endif
                    try { control_writer.send(message_type::error, error_payload(kind, exception.what())); } catch (const std::exception&) { }
                    stopping = true;
                    cancel_thread(control_thread.get());
                }
            }
        });
        audio_thread_handle = audio_reader.native_handle();

        try {
            while (!stopping) {
                const auto incoming = read_message(control.get(), maximum_control_payload, control_sequence);
                if (is_optional_unknown(incoming.env.type, incoming.env.flags)) continue;
                const auto type = static_cast<message_type>(incoming.env.type);
                if (empty_guid(incoming.env.correlation)) throw protocol_error("control request correlation is empty");
                if (type == message_type::ping) {
                    std::size_t offset = 0;
                    if (get_i64(incoming.payload, offset) <= 0 || offset != incoming.payload.size()) throw protocol_error("invalid ping payload");
                    control_writer.send(message_type::pong, incoming.payload, incoming.env.correlation);
                }
                else if (type == message_type::start_audio) {
                    std::size_t offset = 0;
                    if (get_guid(incoming.payload, offset) != session) throw protocol_error("worker session mismatch");
                    stream_statistics fresh{};
                    fresh.worker_session = session;
                    fresh.capture_session = get_guid(incoming.payload, offset);
                    fresh.initial_sequence = get_i64(incoming.payload, offset);
                    const auto started_at = get_i64(incoming.payload, offset);
                    if (get_i32(incoming.payload, offset) != 16000 || get_u16(incoming.payload, offset) != 1 || get_u16(incoming.payload, offset) != 16 || get_u16(incoming.payload, offset) != 20 || get_u32(incoming.payload, offset) != 320 || get_u32(incoming.payload, offset) != 640 || fresh.initial_sequence < 1 || started_at <= 0 || offset != incoming.payload.size()) throw protocol_error("invalid audio format");
                    { std::lock_guard lock(statistics_mutex); audio_lifecycle.start(); statistics = fresh; audio_end_received = false; }
                    #ifdef LCT_ENABLE_RECOGNITION
                    if (recognizer) recognizer->start_stream(fresh.capture_session);
                    #endif
                    control_writer.send(message_type::audio_started, identity_payload(session, fresh.capture_session), incoming.env.correlation);
                }
                else if (type == message_type::stop_audio) {
                    std::size_t offset = 0;
                    if (get_guid(incoming.payload, offset) != session) throw protocol_error("worker session mismatch");
                    const auto capture = get_guid(incoming.payload, offset);
                    if (offset != incoming.payload.size()) throw protocol_error("stop payload has trailing bytes");
                    std::vector<std::uint8_t> summary;
                    { std::unique_lock lock(statistics_mutex);
                        if (capture != statistics.capture_session) throw protocol_error("capture session mismatch");
                        const auto timeout = arguments.recognition() ? std::chrono::seconds(125) : std::chrono::seconds(2);
                        if (!audio_end_changed.wait_for(lock, timeout, [&]{ return audio_end_received || stopping.load(); }) || !audio_end_received) throw protocol_error("timed out waiting for audio stream end");
                        audio_lifecycle.stop();
                        summary = encode_summary(statistics);
                    }
                    control_writer.send(message_type::audio_stopped, summary, incoming.env.correlation);
                }
                else if (type == message_type::shutdown) {
                    std::size_t offset = 0;
                    if (get_guid(incoming.payload, offset) != session || offset != incoming.payload.size()) throw protocol_error("invalid shutdown session");
                    control_writer.send(message_type::shutdown_ack, incoming.payload, incoming.env.correlation);
                    stopping = true;
                    cancel_thread(audio_reader.native_handle());
                    break;
                }
                else throw protocol_error("control message is invalid in the current state");
            }
        }
        catch (const std::exception& exception) {
            std::vector<std::uint8_t> error;
            put_u16(error, 0);
            put_string(error, exception.what());
            try { control_writer.send(message_type::error, error); } catch (const std::exception&) { }
            stopping = true;
            cancel_thread(audio_reader.native_handle());
            join_bounded(audio_reader);
            join_bounded(parent_monitor);
            return 21;
        }

        stopping = true;
        cancel_thread(audio_reader.native_handle());
        join_bounded(audio_reader);
        join_bounded(parent_monitor);
        #ifdef LCT_ENABLE_RECOGNITION
        if (recognizer) {
            const auto diagnostics = recognizer->diagnostics();
            recognizer.reset();
            std::cout << "recognition enabled=1 vad_windows=" << diagnostics.vad_windows
                      << " regions_started=" << diagnostics.speech_regions_started
                      << " regions_completed=" << diagnostics.speech_regions_completed
                      << " regions_discarded=" << diagnostics.speech_regions_discarded
                      << " partial_submitted=" << diagnostics.partial_submitted
                      << " partial_replaced=" << diagnostics.partial_replaced
                      << " partial_completed=" << diagnostics.partial_completed
                      << " partial_stale=" << diagnostics.partial_stale
                      << " final_completed=" << diagnostics.final_completed
                      << " caption_sequence=" << diagnostics.last_caption_sequence
                      << " inference_count=" << diagnostics.inference_count
                      << " latest_inference_ms=" << diagnostics.latest_inference_ms
                      << " maximum_inference_ms=" << diagnostics.maximum_inference_ms
                      << " inference_thread_joined=1\n";
        }
        #endif
        std::cout << "LiveCaptionsAsrWorker clean shutdown\n";
        return 0;
    }
    catch (const std::exception& exception) {
        std::cerr << "worker failure: " << exception.what() << '\n';
        return 20;
    }
}
