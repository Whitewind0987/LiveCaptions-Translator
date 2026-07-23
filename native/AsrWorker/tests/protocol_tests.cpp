#include "protocol.hpp"
#include <fstream>
#include <iostream>
#include <map>
#include <string>

using namespace lct;

namespace {
int failures = 0;
const auto worker = parse_guid(L"00112233-4455-6677-8899-aabbccddeeff");
const auto capture = parse_guid(L"11111111-2222-3333-4444-555555555555");

void check(bool value, const char* message) { if (!value) { std::cerr << message << '\n'; ++failures; } }
template<class Function> void rejects(Function function, const char* message) { try { function(); check(false, message); } catch (const protocol_error&) { } }

std::vector<std::uint8_t> from_hex(const std::string& text) { return parse_hex(std::wstring(text.begin(), text.end())); }

std::map<std::string, std::vector<std::uint8_t>> load_vectors() {
    std::ifstream input(GOLDEN_VECTOR_PATH);
    check(input.good(), "golden-vector fixture opens");
    std::map<std::string, std::vector<std::uint8_t>> result;
    std::string line;
    while (std::getline(input, line)) {
        if (line.empty() || line[0] == '#') continue;
        const auto split = line.find('=');
        if (split == std::string::npos) { check(false, "golden-vector syntax"); continue; }
        result.emplace(line.substr(0, split), from_hex(line.substr(split + 1)));
    }
    return result;
}

void exact_worker_hello(const std::vector<std::uint8_t>& bytes) {
    std::size_t offset = 0;
    const auto session = get_guid(bytes, offset);
    std::vector<std::uint8_t> nonce(bytes.begin() + static_cast<std::ptrdiff_t>(offset), bytes.begin() + static_cast<std::ptrdiff_t>(offset + 32)); offset += 32;
    const auto pid = get_i32(bytes, offset); const auto minimum = get_u16(bytes, offset); const auto maximum = get_u16(bytes, offset); const auto build = get_string(bytes, offset, 256); const auto capabilities = get_u64(bytes, offset);
    check(session == worker && pid == 1234 && minimum == 0 && maximum == 0 && build == "stage4-vector" && capabilities == 3 && offset == bytes.size(), "WorkerHello decoded fields");
    check(encode_worker_hello(session, nonce, pid, build, capabilities) == bytes, "WorkerHello exact encoding");
}

void exact_audio_hello(const std::vector<std::uint8_t>& bytes) {
    std::size_t offset = 0; const auto session = get_guid(bytes, offset);
    std::vector<std::uint8_t> nonce(bytes.begin() + static_cast<std::ptrdiff_t>(offset), bytes.begin() + static_cast<std::ptrdiff_t>(offset + 32)); offset += 32;
    const auto pid = get_i32(bytes, offset); check(session == worker && pid == 1234 && offset == bytes.size(), "AudioPipeHello decoded fields");
    check(encode_audio_pipe_hello(session, nonce, pid) == bytes, "AudioPipeHello exact encoding");
}

void exact_fixed_payloads(const std::map<std::string, std::vector<std::uint8_t>>& vectors) {
    {
        const auto& bytes = vectors.at("HostAccept"); std::size_t offset = 0; std::vector<std::uint8_t> encoded;
        const auto session = get_guid(bytes, offset); const auto negotiated = get_u16(bytes, offset); const auto rate = get_i32(bytes, offset); const auto channels = get_u16(bytes, offset); const auto bits = get_u16(bytes, offset); const auto milliseconds = get_u16(bytes, offset); const auto samples = get_u32(bytes, offset); const auto pcm = get_u32(bytes, offset);
        put_guid(encoded, session); put_u16(encoded, negotiated); put_i32(encoded, rate); put_u16(encoded, channels); put_u16(encoded, bits); put_u16(encoded, milliseconds); put_u32(encoded, samples); put_u32(encoded, pcm);
        check(session == worker && rate == 16000 && channels == 1 && bits == 16 && milliseconds == 20 && samples == 320 && pcm == 640 && offset == bytes.size() && encoded == bytes, "HostAccept exact decode/encode");
    }
    for (const auto* name : { "WorkerReady", "AudioPipeAccepted" }) {
        const auto& bytes = vectors.at(name); std::size_t offset = 0; std::vector<std::uint8_t> encoded; const auto session = get_guid(bytes, offset); const auto pid = get_i32(bytes, offset); put_guid(encoded, session); put_i32(encoded, pid);
        check(session == worker && pid == 1234 && offset == bytes.size() && encoded == bytes, "ready/accepted exact decode/encode");
    }
    {
        const auto& bytes = vectors.at("StartAudioStream"); std::size_t offset = 0; std::vector<std::uint8_t> encoded;
        const auto ws = get_guid(bytes, offset); const auto cs = get_guid(bytes, offset); const auto sequence = get_i64(bytes, offset); const auto timestamp = get_i64(bytes, offset); const auto rate = get_i32(bytes, offset); const auto channels = get_u16(bytes, offset); const auto bits = get_u16(bytes, offset); const auto milliseconds = get_u16(bytes, offset); const auto samples = get_u32(bytes, offset); const auto pcm = get_u32(bytes, offset);
        put_guid(encoded, ws); put_guid(encoded, cs); put_i64(encoded, sequence); put_i64(encoded, timestamp); put_i32(encoded, rate); put_u16(encoded, channels); put_u16(encoded, bits); put_u16(encoded, milliseconds); put_u32(encoded, samples); put_u32(encoded, pcm);
        check(ws == worker && cs == capture && sequence == 1 && timestamp == 1700000000000 && offset == bytes.size() && encoded == bytes, "StartAudioStream exact decode/encode");
    }
    {
        const auto& bytes = vectors.at("AudioFrame"); const auto metadata = decode_audio_metadata(bytes); std::vector<std::uint8_t> encoded;
        put_guid(encoded, metadata.worker_session); put_guid(encoded, metadata.capture_session); put_i64(encoded, metadata.sequence); put_i64(encoded, metadata.sample_index); put_i64(encoded, metadata.timestamp_ms); put_u32(encoded, metadata.payload_length); encoded.insert(encoded.end(), bytes.begin() + 60, bytes.end());
        check(metadata.worker_session == worker && metadata.capture_session == capture && metadata.sequence == 1 && metadata.sample_index == 0 && metadata.timestamp_ms == 1700000000020 && encoded == bytes, "AudioFrame exact decode/encode");
    }
    {
        const auto& bytes = vectors.at("AudioProgress"); std::size_t offset = 0; stream_statistics stats{}; stats.capture_session = get_guid(bytes, offset); stats.frames = get_i64(bytes, offset); stats.bytes = get_i64(bytes, offset); stats.first_sequence = get_i64(bytes, offset); stats.last_sequence = get_i64(bytes, offset); stats.gaps = get_i64(bytes, offset); stats.invalid = get_i64(bytes, offset); stats.first_timestamp = get_i64(bytes, offset); stats.last_timestamp = get_i64(bytes, offset);
        check(stats.capture_session == capture && stats.frames == 50 && stats.bytes == 32000 && offset == bytes.size() && encode_summary(stats) == bytes, "AudioProgress exact decode/encode");
    }
    {
        const auto& bytes = vectors.at("Error"); std::size_t offset = 0; std::vector<std::uint8_t> encoded; const auto kind = get_u16(bytes, offset); const auto text = get_string(bytes, offset); put_u16(encoded, kind); put_string(encoded, text);
        check(kind == 2 && text == "vector error" && offset == bytes.size() && encoded == bytes, "Error exact decode/encode");
    }
    {
        const auto& bytes = vectors.at("CaptionEvent"); std::size_t offset = 0; std::vector<std::uint8_t> encoded;
        const auto schema = get_i32(bytes, offset); const auto session = get_guid(bytes, offset); const auto sequence = get_i64(bytes, offset); const auto segment = get_i64(bytes, offset); const auto revision = get_i64(bytes, offset); const auto kind = get_u16(bytes, offset); const auto text = get_string(bytes, offset, 32768); const auto has_start = bytes.at(offset++); const auto start = has_start ? get_i64(bytes, offset) : 0; const auto has_end = bytes.at(offset++); const auto end = has_end ? get_i64(bytes, offset) : 0; const auto emitted = get_i64(bytes, offset);
        put_i32(encoded, schema); put_guid(encoded, session); put_i64(encoded, sequence); put_i64(encoded, segment); put_i64(encoded, revision); put_u16(encoded, kind); put_string(encoded, text, 32768); encoded.push_back(has_start); if (has_start) put_i64(encoded, start); encoded.push_back(has_end); if (has_end) put_i64(encoded, end); put_i64(encoded, emitted);
        check(schema == 1 && session == capture && sequence == 2 && text == "hello" && start == 0 && end == 20 && emitted == 1700000001000 && offset == bytes.size() && encoded == bytes, "CaptionEvent exact decode/encode");
    }
    for (const auto* name : { "Shutdown", "ShutdownAcknowledged" }) {
        const auto& bytes = vectors.at(name); std::size_t offset = 0; std::vector<std::uint8_t> encoded; const auto session = get_guid(bytes, offset); put_guid(encoded, session);
        check(session == worker && offset == bytes.size() && encoded == bytes, "shutdown exact decode/encode");
    }
}

void audio_validation_tests() {
    stream_statistics stats{}; stats.worker_session = worker; stats.capture_session = capture;
    audio_metadata frame{ worker, capture, 1, 0, 1000, 640 };
    check(validate_audio(stats, frame), "first frame accepted");
    frame = { worker, capture, 3, 640, 1040, 640 }; check(validate_audio(stats, frame) && stats.gaps == 1, "one-frame gap uses sequence-scaled sample index");
    frame = { worker, capture, 6, 1600, 1100, 640 }; check(validate_audio(stats, frame) && stats.gaps == 3, "multi-frame gap accepted");
    const auto before = stats;
    frame = { worker, capture, 7, 2241, 1120, 640 }; check(!validate_audio(stats, frame) && stats.frames == before.frames && stats.gaps == before.gaps && stats.last_sequence == before.last_sequence, "invalid sample delta leaves state unchanged");
    frame = { worker, capture, 6, 1600, 1120, 640 }; check(!validate_audio(stats, frame), "duplicate rejected");
    frame = { worker, capture, 7, 1920, 1099, 640 }; check(!validate_audio(stats, frame), "decreasing timestamp rejected");
    frame = { worker, capture, 7, 1920, 1120, 640 }; check(validate_audio(stats, frame), "valid recovery after rejected frame");
}
}

int wmain() {
    envelope envelope_value{ 1, 0, 1, 0, 3, 7, worker };
    const auto envelope_bytes = encode_envelope(envelope_value);
    const auto decoded = decode_envelope(envelope_bytes, 65536);
    check(decoded.sequence == 7 && decoded.correlation == worker, "envelope round-trip");
    check(envelope_bytes[24] == 0 && envelope_bytes[25] == 0x11, "RFC Guid order");
    auto bad = envelope_bytes; bad[0] = 0; rejects([&] { decode_envelope(bad, 65536); }, "invalid magic rejected");
    bad = envelope_bytes; bad[4] = 2; rejects([&] { decode_envelope(bad, 65536); }, "major rejected");
    bad = envelope_bytes; bad[6] = 1; rejects([&] { decode_envelope(bad, 65536); }, "minor rejected");
    bad = envelope_bytes; bad[10] = 2; rejects([&] { decode_envelope(bad, 65536); }, "flags rejected");
    rejects([&] { decode_envelope(std::span<const std::uint8_t>(envelope_bytes.data(), 20), 65536); }, "fragmented envelope rejected until complete");
    std::uint64_t sequence = 0; accept_sequence(sequence, 1); rejects([&] { accept_sequence(sequence, 1); }, "duplicate sequence rejected"); rejects([&] { accept_sequence(sequence, 0); }, "zero sequence rejected");
    rejects([&] { is_optional_unknown(500, 0); }, "unknown required type rejected"); check(is_optional_unknown(500, 1), "optional unknown type ignored");
    guid other{}; rejects([&] { require_correlation(worker, other); }, "correlation mismatch rejected");
    std::vector<std::uint8_t> invalid_utf8; put_u32(invalid_utf8, 1); invalid_utf8.push_back(0xff); std::size_t invalid_offset = 0; rejects([&] { get_string(invalid_utf8, invalid_offset); }, "invalid UTF-8 rejected");
    std::vector<std::uint8_t> fragmented; put_string(fragmented, "payload"); fragmented.pop_back(); std::size_t fragmented_offset = 0; rejects([&] { get_string(fragmented, fragmented_offset); }, "fragmented payload rejected until complete");
    stream_lifecycle lifecycle; lifecycle.start(); rejects([&] { lifecycle.start(); }, "duplicate stream start rejected"); lifecycle.stop(); rejects([&] { lifecycle.stop(); }, "stop while idle rejected");

    const auto vectors = load_vectors();
    check(vectors.size() == 12, "all shared vectors loaded");
    exact_worker_hello(vectors.at("WorkerHello"));
    exact_audio_hello(vectors.at("AudioPipeHello"));
    exact_fixed_payloads(vectors);
    auto accepted_with_trailing = vectors.at("AudioPipeAccepted"); accepted_with_trailing.push_back(0); rejects([&] { decode_audio_pipe_accepted(accepted_with_trailing, worker, 1234); }, "trailing payload bytes rejected");
    audio_validation_tests();
    if (failures != 0) return 1;
    std::cout << "native protocol tests passed\n";
    return 0;
}
