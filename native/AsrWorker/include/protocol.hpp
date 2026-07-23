#pragma once
#include <array>
#include <cstdint>
#include <span>
#include <stdexcept>
#include <string>
#include <vector>

namespace lct {
constexpr std::uint32_t magic = 0x3150434c;
constexpr std::uint16_t major = 1;
constexpr std::uint16_t minor = 0;
constexpr std::size_t envelope_size = 40;
constexpr std::uint32_t maximum_control_payload = 65536;
constexpr std::uint32_t audio_payload_size = 700;
constexpr std::uint32_t pcm_bytes = 640;
enum class message_type : std::uint16_t { worker_hello=1, host_accept=2, host_reject=3, worker_ready=4, start_audio=5, audio_started=6, stop_audio=7, audio_stopped=8, ping=9, pong=10, worker_status=11, audio_progress=12, error=13, shutdown=14, shutdown_ack=15, caption_event=16, audio_frame=100 };
struct protocol_error : std::runtime_error { using std::runtime_error::runtime_error; };
struct guid { std::array<std::uint8_t,16> bytes{}; bool operator==(const guid&) const = default; };
struct envelope { std::uint16_t protocol_major{}, protocol_minor{}, type{}, flags{}; std::uint32_t payload_length{}; std::uint64_t sequence{}; guid correlation{}; };
struct audio_metadata { guid worker_session{}, capture_session{}; std::int64_t sequence{}, sample_index{}, timestamp_ms{}; std::uint32_t payload_length{}; };
struct stream_statistics { guid capture_session{}; std::int64_t frames{}, bytes{}, first_sequence{}, last_sequence{}, gaps{}, invalid{}, first_timestamp{}, last_timestamp{}, expected_sample_index{}; };
void put_u16(std::vector<std::uint8_t>&, std::uint16_t); void put_u32(std::vector<std::uint8_t>&, std::uint32_t); void put_i32(std::vector<std::uint8_t>&, std::int32_t); void put_u64(std::vector<std::uint8_t>&, std::uint64_t); void put_i64(std::vector<std::uint8_t>&, std::int64_t); void put_guid(std::vector<std::uint8_t>&, const guid&); void put_string(std::vector<std::uint8_t>&, std::string_view, std::size_t=4096);
std::uint16_t get_u16(std::span<const std::uint8_t>, std::size_t&); std::uint32_t get_u32(std::span<const std::uint8_t>, std::size_t&); std::int32_t get_i32(std::span<const std::uint8_t>, std::size_t&); std::uint64_t get_u64(std::span<const std::uint8_t>, std::size_t&); std::int64_t get_i64(std::span<const std::uint8_t>, std::size_t&); guid get_guid(std::span<const std::uint8_t>, std::size_t&); std::string get_string(std::span<const std::uint8_t>, std::size_t&, std::size_t=4096);
std::array<std::uint8_t,envelope_size> encode_envelope(const envelope&); envelope decode_envelope(std::span<const std::uint8_t>, std::uint32_t maximum_payload);
guid parse_guid(std::wstring_view); std::vector<std::uint8_t> parse_hex(std::wstring_view); audio_metadata decode_audio_metadata(std::span<const std::uint8_t>); bool validate_audio(stream_statistics&, const audio_metadata&); std::vector<std::uint8_t> encode_summary(const stream_statistics&);
}
