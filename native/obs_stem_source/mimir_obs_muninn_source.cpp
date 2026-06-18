#ifdef _WIN32
#include <winsock2.h>
#include <ws2tcpip.h>
#include <windows.h>
#endif

#include <obs-module.h>

#include "muninn_media_wire.h"

#include <algorithm>
#include <atomic>
#include <chrono>
#include <condition_variable>
#include <cstdint>
#include <cctype>
#include <cstring>
#include <deque>
#include <fstream>
#include <iterator>
#include <map>
#include <memory>
#include <mutex>
#include <optional>
#include <sstream>
#include <stdexcept>
#include <string>
#include <thread>
#include <vector>

namespace {

constexpr const char *MuninnStorePath = "C:\\Meta\\Odin\\state\\muninn.telemetry.cc";
constexpr const char *MuninnCommandExePath = "C:\\Meta\\Odin\\Muninn\\muninn.exe";
constexpr const char *MuninnCommandRudpTarget = "10.77.0.4:17873";
constexpr const char *MuninnDefaultHostId = "raven";
constexpr const char *MuninnDefaultMediaTargetHost = "10.77.0.2";
constexpr uint16_t MuninnDefaultMediaPort = 5204;
constexpr const char *MuninnObsCatalogType = "muninn.obs_stream_catalog";
constexpr const char *MuninnObsCatalogSchema = "muninn.obs_stream_catalog.v1";
constexpr const char *MuninnObsCatalogKey = "obs";
constexpr uint32_t MuninnMediaRudpConnectionId = 0x6d750001;
constexpr auto MuninnVideoAssemblyDeadline = std::chrono::milliseconds(75);

struct MuninnStreamOption {
    std::string stream_id;
    std::string label;
    std::string url;
    std::string state;
};

struct MuninnQueuedAudio {
    std::vector<uint8_t> planes[MAX_AV_PLANES];
    uint32_t frames = 0;
    uint64_t timestamp = 0;
    enum speaker_layout speakers = SPEAKERS_STEREO;
    uint32_t samples_per_sec = 48000;
};

struct MuninnSource {
    obs_source_t *source = nullptr;
    obs_source_t *media = nullptr;
    obs_source_t *audio_media = nullptr;
    std::unique_ptr<class RudpMediaBridge> bridge;
    std::mutex audio_mutex;
    std::condition_variable audio_cv;
    std::deque<MuninnQueuedAudio> audio_queue;
    std::thread audio_worker;
    bool audio_worker_running = false;
    std::string store_path = MuninnStorePath;
    std::string command_exe_path = MuninnCommandExePath;
    std::string command_rudp_target = MuninnCommandRudpTarget;
    std::string host_id = MuninnDefaultHostId;
    std::string media_target_host = MuninnDefaultMediaTargetHost;
    uint16_t media_port = MuninnDefaultMediaPort;
    std::string stream_id;
    std::string stream_url;
    std::string last_requested_stream_id;
};

static void muninn_child_audio(void *param, obs_source_t *source, const struct audio_data *audio_data, bool muted);
static void stop_audio_worker(MuninnSource *ctx);

struct RudpUrlParts {
    uint16_t port = 0;
    uint16_t video_local_port = 0;
    uint16_t audio_local_port = 0;
    uint32_t connection_id = MuninnMediaRudpConnectionId;
};

struct RudpBridgeOutputs {
    std::string video_url;
    std::string audio_url;
};

struct MuninnMediaPayload {
    enum class Kind { Unknown, Video, Audio };
    Kind kind = Kind::Unknown;
    std::string stream_id;
    std::string session_id;
    uint64_t frame_id = 0;
    uint16_t chunk_index = 0;
    uint16_t chunk_count = 1;
    std::vector<uint8_t> payload;
};

struct MuninnVideoFrameAssembly {
    std::string stream_id;
    std::string session_id;
    uint64_t frame_id = 0;
    uint16_t chunk_count = 0;
    uint16_t chunks_received = 0;
    std::chrono::steady_clock::time_point started_at;
    std::vector<std::vector<uint8_t>> chunks;
};

static bool starts_with(const std::string &value, const std::string &prefix)
{
    return value.rfind(prefix, 0) == 0;
}

static uint32_t read_be32(const uint8_t *data)
{
    return (static_cast<uint32_t>(data[0]) << 24) | (static_cast<uint32_t>(data[1]) << 16) |
           (static_cast<uint32_t>(data[2]) << 8) | static_cast<uint32_t>(data[3]);
}

static uint16_t read_be16(const uint8_t *data)
{
    return static_cast<uint16_t>((static_cast<uint16_t>(data[0]) << 8) | data[1]);
}

static void write_be32(std::vector<uint8_t> &data, size_t offset, uint32_t value)
{
    data[offset] = static_cast<uint8_t>((value >> 24) & 0xff);
    data[offset + 1] = static_cast<uint8_t>((value >> 16) & 0xff);
    data[offset + 2] = static_cast<uint8_t>((value >> 8) & 0xff);
    data[offset + 3] = static_cast<uint8_t>(value & 0xff);
}

static void write_be16(std::vector<uint8_t> &data, size_t offset, uint16_t value)
{
    data[offset] = static_cast<uint8_t>((value >> 8) & 0xff);
    data[offset + 1] = static_cast<uint8_t>(value & 0xff);
}

static std::optional<uint32_t> parse_u32_query_hex_or_decimal(const std::string &value)
{
    try {
        size_t consumed = 0;
        const int base = starts_with(value, "0x") || starts_with(value, "0X") ? 16 : 10;
        const auto parsed = std::stoul(value, &consumed, base);
        if (consumed == value.size()) {
            return static_cast<uint32_t>(parsed);
        }
    } catch (...) {
    }
    return std::nullopt;
}

static std::optional<RudpUrlParts> parse_rudp_url(const std::string &url)
{
    if (!starts_with(url, "rudp://")) {
        return std::nullopt;
    }

    const auto authority_start = std::string("rudp://").size();
    const auto path_start = url.find('/', authority_start);
    const auto query_start = url.find('?', authority_start);
    const auto authority_end = std::min(path_start == std::string::npos ? url.size() : path_start,
                                        query_start == std::string::npos ? url.size() : query_start);
    const auto authority = url.substr(authority_start, authority_end - authority_start);
    const auto colon = authority.rfind(':');
    if (colon == std::string::npos || colon + 1 >= authority.size()) {
        return std::nullopt;
    }

    RudpUrlParts parts;
    parts.port = static_cast<uint16_t>(std::stoul(authority.substr(colon + 1)));
    parts.video_local_port = static_cast<uint16_t>(parts.port + 10000);
    parts.audio_local_port = static_cast<uint16_t>(parts.port + 10001);

    if (query_start != std::string::npos) {
        std::stringstream query(url.substr(query_start + 1));
        std::string item;
        while (std::getline(query, item, '&')) {
            const auto equals = item.find('=');
            if (equals == std::string::npos) {
                continue;
            }
            const auto key = item.substr(0, equals);
            const auto value = item.substr(equals + 1);
            if (key == "connection") {
                if (const auto parsed = parse_u32_query_hex_or_decimal(value)) {
                    parts.connection_id = *parsed;
                }
            } else if (key == "local_port" || key == "video_local_port") {
                parts.video_local_port = static_cast<uint16_t>(std::stoul(value));
            } else if (key == "audio_local_port") {
                parts.audio_local_port = static_cast<uint16_t>(std::stoul(value));
            }
        }
    }

    return parts;
}

class MediaMsgpackReader {
public:
    MediaMsgpackReader(const uint8_t *data, size_t size) : data_(data), size_(size) {}
    explicit MediaMsgpackReader(const std::vector<uint8_t> &bytes) : data_(bytes.data()), size_(bytes.size()) {}

    uint32_t read_map_len()
    {
        const uint8_t tag = read_u8();
        if ((tag & 0xf0) == 0x80) {
            return tag & 0x0f;
        }
        if (tag == 0xde) {
            return read_u16();
        }
        if (tag == 0xdf) {
            return read_u32();
        }
        throw std::runtime_error("expected MessagePack map");
    }

    uint32_t read_array_len()
    {
        const uint8_t tag = read_u8();
        if ((tag & 0xf0) == 0x90) {
            return tag & 0x0f;
        }
        if (tag == 0xdc) {
            return read_u16();
        }
        if (tag == 0xdd) {
            return read_u32();
        }
        throw std::runtime_error("expected MessagePack array");
    }

    std::string read_string()
    {
        const uint8_t tag = read_u8();
        uint32_t length = 0;
        if ((tag & 0xe0) == 0xa0) {
            length = tag & 0x1f;
        } else if (tag == 0xd9) {
            length = read_u8();
        } else if (tag == 0xda) {
            length = read_u16();
        } else if (tag == 0xdb) {
            length = read_u32();
        } else {
            throw std::runtime_error("expected MessagePack string");
        }
        require(length);
        std::string value(reinterpret_cast<const char *>(data_ + pos_), length);
        pos_ += length;
        return value;
    }

    std::vector<uint8_t> read_bytes()
    {
        const uint8_t tag = read_u8();
        uint32_t length = 0;
        if (tag == 0xc4) {
            length = read_u8();
        } else if (tag == 0xc5) {
            length = read_u16();
        } else if (tag == 0xc6) {
            length = read_u32();
        } else if ((tag & 0xf0) == 0x90 || tag == 0xdc || tag == 0xdd) {
            --pos_;
            length = read_array_len();
            std::vector<uint8_t> bytes;
            bytes.reserve(length);
            for (uint32_t index = 0; index < length; ++index) {
                bytes.push_back(static_cast<uint8_t>(read_unsigned()));
            }
            return bytes;
        } else {
            throw std::runtime_error("expected MessagePack bytes");
        }
        require(length);
        std::vector<uint8_t> bytes(data_ + pos_, data_ + pos_ + length);
        pos_ += length;
        return bytes;
    }

    uint64_t read_unsigned()
    {
        const uint8_t tag = read_u8();
        if (tag <= 0x7f) {
            return tag;
        }
        if (tag == 0xcc) {
            return read_u8();
        }
        if (tag == 0xcd) {
            return read_u16();
        }
        if (tag == 0xce) {
            return read_u32();
        }
        if (tag == 0xcf) {
            return read_u64();
        }
        throw std::runtime_error("expected MessagePack unsigned integer");
    }

    void skip()
    {
        const uint8_t tag = read_u8();
        if (tag <= 0x7f || tag >= 0xe0 || tag == 0xc0 || tag == 0xc2 || tag == 0xc3) {
            return;
        }
        if ((tag & 0xe0) == 0xa0) {
            skip_bytes(tag & 0x1f);
            return;
        }
        if ((tag & 0xf0) == 0x90) {
            skip_items(tag & 0x0f);
            return;
        }
        if ((tag & 0xf0) == 0x80) {
            skip_items((tag & 0x0f) * 2);
            return;
        }
        switch (tag) {
        case 0xc4:
        case 0xd9:
            skip_bytes(read_u8());
            return;
        case 0xc5:
        case 0xda:
            skip_bytes(read_u16());
            return;
        case 0xc6:
        case 0xdb:
            skip_bytes(read_u32());
            return;
        case 0xcc:
        case 0xd0:
            skip_bytes(1);
            return;
        case 0xcd:
        case 0xd1:
            skip_bytes(2);
            return;
        case 0xce:
        case 0xd2:
        case 0xca:
            skip_bytes(4);
            return;
        case 0xcf:
        case 0xd3:
        case 0xcb:
            skip_bytes(8);
            return;
        case 0xdc:
            skip_items(read_u16());
            return;
        case 0xdd:
            skip_items(read_u32());
            return;
        case 0xde:
            skip_items(read_u16() * 2);
            return;
        case 0xdf:
            skip_items(read_u32() * 2);
            return;
        default:
            throw std::runtime_error("unsupported MessagePack item");
        }
    }

private:
    const uint8_t *data_;
    size_t size_;
    size_t pos_ = 0;

    void require(size_t count) const
    {
        if (count > size_ - pos_) {
            throw std::runtime_error("truncated MessagePack input");
        }
    }

    uint8_t read_u8()
    {
        require(1);
        return data_[pos_++];
    }

    uint16_t read_u16()
    {
        require(2);
        const uint16_t value = (static_cast<uint16_t>(data_[pos_]) << 8) | static_cast<uint16_t>(data_[pos_ + 1]);
        pos_ += 2;
        return value;
    }

    uint32_t read_u32()
    {
        require(4);
        const uint32_t value = (static_cast<uint32_t>(data_[pos_]) << 24) | (static_cast<uint32_t>(data_[pos_ + 1]) << 16) |
                               (static_cast<uint32_t>(data_[pos_ + 2]) << 8) | static_cast<uint32_t>(data_[pos_ + 3]);
        pos_ += 4;
        return value;
    }

    uint64_t read_u64()
    {
        const uint64_t high = read_u32();
        const uint64_t low = read_u32();
        return (high << 32) | low;
    }

    void skip_bytes(size_t count)
    {
        require(count);
        pos_ += count;
    }

    void skip_items(uint32_t count)
    {
        for (uint32_t index = 0; index < count; ++index) {
            skip();
        }
    }
};

static std::optional<MuninnMediaPayload> decode_muninn_media_payload(const uint8_t *payload, uint32_t payload_len)
{
    try {
        MediaMsgpackReader wire(payload, payload_len);
        const uint32_t message_len = wire.read_map_len();
        std::string schema_version;
        std::string schema_id;
        std::vector<uint8_t> document_payload;

        for (uint32_t index = 0; index < message_len; ++index) {
            const auto key = wire.read_string();
            if (key == "schemaVersion") {
                schema_version = wire.read_string();
            } else if (key == "document") {
                const uint32_t document_len = wire.read_map_len();
                for (uint32_t field = 0; field < document_len; ++field) {
                    const auto document_key = wire.read_string();
                    if (document_key == "schemaId") {
                        schema_id = wire.read_string();
                    } else if (document_key == "payload") {
                        document_payload = wire.read_bytes();
                    } else {
                        wire.skip();
                    }
                }
            } else {
                wire.skip();
            }
        }

        if (schema_version != "cultnet.document_put_raw.v0" || document_payload.empty()) {
            return std::nullopt;
        }

        MediaMsgpackReader record(document_payload);
        const uint32_t record_len = record.read_array_len();
        if (schema_id == "muninn.media_video_access_unit.v1") {
            if (record_len < 14) {
                return std::nullopt;
            }
            MuninnMediaPayload media;
            media.kind = MuninnMediaPayload::Kind::Video;
            media.stream_id = record.read_string();
            media.session_id = record.read_string();
            media.frame_id = record.read_unsigned();
            record.skip(); // codec
            record.skip(); // pts_ticks
            record.skip(); // duration_ticks
            record.skip(); // timebase_num
            record.skip(); // timebase_den
            record.skip(); // keyframe
            record.skip(); // dependency_frame_id
            record.skip(); // deadline_ticks
            media.chunk_index = static_cast<uint16_t>(record.read_unsigned());
            media.chunk_count = static_cast<uint16_t>(record.read_unsigned());
            media.payload = record.read_bytes();
            return media;
        }
        if (schema_id == "muninn.media_audio_packet.v1") {
            if (record_len < 10) {
                return std::nullopt;
            }
            for (uint32_t index = 0; index < 9; ++index) {
                record.skip();
            }
            MuninnMediaPayload media;
            media.kind = MuninnMediaPayload::Kind::Audio;
            media.payload = record.read_bytes();
            return media;
        }
    } catch (const std::exception &error) {
        blog(LOG_WARNING, "Muninn RUDP bridge could not decode typed media payload: %s", error.what());
    }

    return std::nullopt;
}

class RudpMediaBridge {
public:
    ~RudpMediaBridge() { stop(); }

    RudpBridgeOutputs start(const std::string &url)
    {
        if (running_ && url == source_url_) {
            return outputs_;
        }

        stop();
        const auto parsed = parse_rudp_url(url);
        if (!parsed) {
            return RudpBridgeOutputs{url, ""};
        }

        source_url_ = url;
        parts_ = *parsed;
        outputs_.video_url = "udp://127.0.0.1:" + std::to_string(parts_.video_local_port) +
                             "?fifo_size=131072&overrun_nonfatal=1&buffer_size=1048576";
        outputs_.audio_url = "udp://127.0.0.1:" + std::to_string(parts_.audio_local_port) +
                             "?fifo_size=131072&overrun_nonfatal=1&buffer_size=1048576";
        running_ = true;
        worker_ = std::thread([this] { run(); });
        blog(LOG_INFO, "Muninn RUDP bridge starting udp/%u -> video udp/127.0.0.1:%u audio udp/127.0.0.1:%u",
             parts_.port, parts_.video_local_port, parts_.audio_local_port);
        return outputs_;
    }

    void stop()
    {
        running_ = false;
#ifdef _WIN32
        if (socket_ != INVALID_SOCKET) {
            closesocket(socket_);
            socket_ = INVALID_SOCKET;
        }
#endif
        if (worker_.joinable()) {
            worker_.join();
        }
        source_url_.clear();
        outputs_ = {};
    }

private:
    void run()
    {
#ifdef _WIN32
        WSADATA data;
        WSAStartup(MAKEWORD(2, 2), &data);

        socket_ = ::socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
        if (socket_ == INVALID_SOCKET) {
            blog(LOG_WARNING, "Muninn RUDP bridge could not create socket");
            WSACleanup();
            return;
        }
        const int socket_buffer_bytes = 4 * 1024 * 1024;
        setsockopt(socket_, SOL_SOCKET, SO_RCVBUF, reinterpret_cast<const char *>(&socket_buffer_bytes),
                   sizeof(socket_buffer_bytes));
        const DWORD receive_timeout_ms = 2;
        setsockopt(socket_, SOL_SOCKET, SO_RCVTIMEO, reinterpret_cast<const char *>(&receive_timeout_ms),
                   sizeof(receive_timeout_ms));

        sockaddr_in bind_addr = {};
        bind_addr.sin_family = AF_INET;
        bind_addr.sin_addr.s_addr = htonl(INADDR_ANY);
        bind_addr.sin_port = htons(parts_.port);
        if (bind(socket_, reinterpret_cast<sockaddr *>(&bind_addr), sizeof(bind_addr)) != 0) {
            blog(LOG_WARNING, "Muninn RUDP bridge could not bind udp/%u", parts_.port);
            closesocket(socket_);
            socket_ = INVALID_SOCKET;
            WSACleanup();
            return;
        }

        const SOCKET video_socket = ::socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
        const SOCKET audio_socket = ::socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
        setsockopt(video_socket, SOL_SOCKET, SO_SNDBUF, reinterpret_cast<const char *>(&socket_buffer_bytes),
                   sizeof(socket_buffer_bytes));
        setsockopt(audio_socket, SOL_SOCKET, SO_SNDBUF, reinterpret_cast<const char *>(&socket_buffer_bytes),
                   sizeof(socket_buffer_bytes));
        sockaddr_in video_addr = {};
        video_addr.sin_family = AF_INET;
        inet_pton(AF_INET, "127.0.0.1", &video_addr.sin_addr);
        video_addr.sin_port = htons(parts_.video_local_port);
        sockaddr_in audio_addr = {};
        audio_addr.sin_family = AF_INET;
        inet_pton(AF_INET, "127.0.0.1", &audio_addr.sin_addr);
        audio_addr.sin_port = htons(parts_.audio_local_port);

        std::vector<uint8_t> buffer(65535);
        uint32_t bridge_sequence = 1;
        while (running_) {
            sockaddr_in remote = {};
            int remote_len = sizeof(remote);
            const int received = recvfrom(socket_, reinterpret_cast<char *>(buffer.data()), static_cast<int>(buffer.size()), 0,
                                          reinterpret_cast<sockaddr *>(&remote), &remote_len);
            if (received <= 0) {
                flush_forward_payloads(video_socket, video_addr, audio_socket, audio_addr, bridge_sequence);
                expire_video_assemblies(std::chrono::steady_clock::now(), bridge_sequence);
                continue;
            }
            handle_packet(buffer.data(), static_cast<size_t>(received), remote, bridge_sequence, video_socket, video_addr, audio_socket, audio_addr);
        }

        if (video_socket != INVALID_SOCKET) {
            closesocket(video_socket);
        }
        if (audio_socket != INVALID_SOCKET) {
            closesocket(audio_socket);
        }
        if (socket_ != INVALID_SOCKET) {
            closesocket(socket_);
            socket_ = INVALID_SOCKET;
        }
        WSACleanup();
#endif
    }

#ifdef _WIN32
    void handle_packet(const uint8_t *packet,
                       size_t size,
                       const sockaddr_in &remote,
                       uint32_t &bridge_sequence,
                       SOCKET video_socket,
                       const sockaddr_in &video_addr,
                       SOCKET audio_socket,
                       const sockaddr_in &audio_addr)
    {
        if (size < 36 || packet[0] != 'C' || packet[1] != 'N' || packet[2] != 'R' || packet[3] != '0') {
            return;
        }
        const uint8_t version = packet[4];
        const uint8_t packet_type = packet[5];
        const uint8_t header_bytes = packet[7];
        const uint32_t connection_id = read_be32(packet + 8);
        const uint32_t sequence = read_be32(packet + 12);
        const uint16_t fragment_count = read_be16(packet + 28);
        const uint32_t payload_len = read_be32(packet + 30);
        const uint8_t channel_len = packet[34];
        if (version != 0 || connection_id != parts_.connection_id || header_bytes < 36 || size < header_bytes) {
            return;
        }
        last_remote_ = remote;
        const size_t payload_offset = static_cast<size_t>(header_bytes);
        if (payload_offset + payload_len > size || 36 + channel_len > header_bytes) {
            return;
        }

        if (packet_type == 1) {
            pending_payloads_.clear();
            video_assemblies_.clear();
            gap_wait_started_.reset();
            next_data_sequence_ = sequence + 1;
            packets_forwarded_ = 0;
            bytes_forwarded_ = 0;
            payloads_decoded_dropped_ = 0;
            video_assemblies_expired_ = 0;
            video_forward_failures_ = 0;
            audio_forward_failures_ = 0;
            send_control_packet(2, 0x03, bridge_sequence++, sequence, remote);
            blog(LOG_INFO, "Muninn RUDP bridge accepted media connection sequence %u", sequence);
            return;
        }

        if (packet_type == 3 && fragment_count == 0) {
            forward_payload(packet + payload_offset, payload_len, video_socket, video_addr, audio_socket, audio_addr, bridge_sequence);
            send_control_packet(4, 0x00, bridge_sequence++, sequence, remote);
        }
    }

    void forward_payload(const uint8_t *payload,
                         uint32_t payload_len,
                         SOCKET video_socket,
                         const sockaddr_in &video_addr,
                         SOCKET audio_socket,
                         const sockaddr_in &audio_addr,
                         uint32_t &bridge_sequence)
    {
        const auto media = decode_muninn_media_payload(payload, payload_len);
        if (!media || media->payload.empty()) {
            ++payloads_decoded_dropped_;
            log_bridge_pressure("dropped undecodable media payload");
            return;
        }
        size_t forwarded_bytes = 0;
        if (media->kind == MuninnMediaPayload::Kind::Video) {
            const auto assembled = assemble_video_payload(*media, bridge_sequence);
            if (!assembled) {
                return;
            }
            forwarded_bytes = assembled->size();
            const int sent = sendto(video_socket, reinterpret_cast<const char *>(assembled->data()), static_cast<int>(assembled->size()), 0,
                                    reinterpret_cast<const sockaddr *>(&video_addr), sizeof(video_addr));
            if (sent < 0) {
                ++video_forward_failures_;
                log_bridge_pressure("failed forwarding video payload to local decoder socket");
                return;
            }
        } else if (media->kind == MuninnMediaPayload::Kind::Audio) {
            forwarded_bytes = media->payload.size();
            const int sent = sendto(audio_socket, reinterpret_cast<const char *>(media->payload.data()), static_cast<int>(media->payload.size()), 0,
                                    reinterpret_cast<const sockaddr *>(&audio_addr), sizeof(audio_addr));
            if (sent < 0) {
                ++audio_forward_failures_;
                log_bridge_pressure("failed forwarding audio payload to local decoder socket");
                return;
            }
        } else {
            ++payloads_decoded_dropped_;
            log_bridge_pressure("dropped unknown media payload");
            return;
        }
        ++packets_forwarded_;
        bytes_forwarded_ += forwarded_bytes;
        if (packets_forwarded_ == 1 || packets_forwarded_ % 900 == 0) {
            blog(LOG_INFO,
                 "Muninn RUDP bridge forwarded=%llu bytes=%llu decode_dropped=%llu assemblies_expired=%llu video_failures=%llu audio_failures=%llu",
                 static_cast<unsigned long long>(packets_forwarded_),
                 static_cast<unsigned long long>(bytes_forwarded_),
                 static_cast<unsigned long long>(payloads_decoded_dropped_),
                 static_cast<unsigned long long>(video_assemblies_expired_),
                 static_cast<unsigned long long>(video_forward_failures_),
                 static_cast<unsigned long long>(audio_forward_failures_));
        }
    }

    std::optional<std::vector<uint8_t>> assemble_video_payload(const MuninnMediaPayload &media, uint32_t &bridge_sequence)
    {
        expire_video_assemblies(std::chrono::steady_clock::now(), bridge_sequence);
        if (media.chunk_count == 0 || media.chunk_index >= media.chunk_count || media.payload.empty()) {
            ++payloads_decoded_dropped_;
            log_bridge_pressure("dropped invalid video chunk metadata");
            return std::nullopt;
        }
        if (media.chunk_count == 1) {
            return media.payload;
        }

        const std::string key =
            media.stream_id + ":" + media.session_id + ":" + std::to_string(media.frame_id);
        auto &assembly = video_assemblies_[key];
        if (assembly.chunks.empty()) {
            assembly.stream_id = media.stream_id;
            assembly.session_id = media.session_id;
            assembly.frame_id = media.frame_id;
            assembly.chunk_count = media.chunk_count;
            assembly.chunks.resize(media.chunk_count);
            assembly.started_at = std::chrono::steady_clock::now();
        }
        if (assembly.chunk_count != media.chunk_count) {
            video_assemblies_.erase(key);
            ++payloads_decoded_dropped_;
            log_bridge_pressure("dropped video frame with mixed chunk count");
            return std::nullopt;
        }
        auto &slot = assembly.chunks[media.chunk_index];
        if (!slot.empty()) {
            return std::nullopt;
        }
        slot = media.payload;
        ++assembly.chunks_received;
        if (assembly.chunks_received != assembly.chunk_count) {
            return std::nullopt;
        }

        std::vector<uint8_t> assembled;
        size_t byte_count = 0;
        for (const auto &chunk : assembly.chunks) {
            byte_count += chunk.size();
        }
        assembled.reserve(byte_count);
        for (const auto &chunk : assembly.chunks) {
            assembled.insert(assembled.end(), chunk.begin(), chunk.end());
        }
        video_assemblies_.erase(key);
        return assembled;
    }

    void expire_video_assemblies(std::chrono::steady_clock::time_point now, uint32_t &bridge_sequence)
    {
        for (auto iterator = video_assemblies_.begin(); iterator != video_assemblies_.end();) {
            if (now - iterator->second.started_at < MuninnVideoAssemblyDeadline) {
                ++iterator;
                continue;
            }
            const auto missing_chunk_keys = missing_video_chunk_keys(iterator->second);
            send_receiver_feedback(iterator->second, missing_chunk_keys, bridge_sequence);
            iterator = video_assemblies_.erase(iterator);
            ++video_assemblies_expired_;
            log_bridge_pressure("expired incomplete video frame assembly");
        }
    }

    std::vector<std::string> missing_video_chunk_keys(const MuninnVideoFrameAssembly &assembly) const
    {
        std::vector<std::string> keys;
        for (uint16_t index = 0; index < assembly.chunk_count; ++index) {
            if (index >= assembly.chunks.size() || assembly.chunks[index].empty()) {
                keys.push_back(std::to_string(assembly.frame_id) + ":" + std::to_string(index));
            }
        }
        return keys;
    }

    void send_receiver_feedback(const MuninnVideoFrameAssembly &assembly,
                                const std::vector<std::string> &missing_chunk_keys,
                                uint32_t &bridge_sequence)
    {
        if (!last_remote_ || assembly.stream_id.empty() || assembly.session_id.empty()) {
            return;
        }
        const auto payload = muninn_media_wire::encode_receiver_feedback_payload(
            assembly.stream_id, assembly.session_id, assembly.frame_id, missing_chunk_keys);
        send_media_packet(payload, bridge_sequence++, *last_remote_);
    }

    void log_bridge_pressure(const char *reason)
    {
        const uint64_t pressure =
            payloads_decoded_dropped_ + video_assemblies_expired_ + video_forward_failures_ + audio_forward_failures_;
        if (pressure == 1 || pressure % 300 == 0) {
            blog(LOG_WARNING,
                 "Muninn RUDP bridge %s: forwarded=%llu bytes=%llu decode_dropped=%llu assemblies_expired=%llu video_failures=%llu audio_failures=%llu",
                 reason,
                 static_cast<unsigned long long>(packets_forwarded_),
                 static_cast<unsigned long long>(bytes_forwarded_),
                 static_cast<unsigned long long>(payloads_decoded_dropped_),
                 static_cast<unsigned long long>(video_assemblies_expired_),
                 static_cast<unsigned long long>(video_forward_failures_),
                 static_cast<unsigned long long>(audio_forward_failures_));
        }
    }

    void flush_forward_payloads(SOCKET video_socket,
                                const sockaddr_in &video_addr,
                                SOCKET audio_socket,
                                const sockaddr_in &audio_addr,
                                uint32_t &bridge_sequence)
    {
        if (!next_data_sequence_) {
            return;
        }
        while (true) {
            const auto found = pending_payloads_.find(*next_data_sequence_);
            if (found == pending_payloads_.end()) {
                if (pending_payloads_.empty()) {
                    gap_wait_started_.reset();
                    return;
                }
                const auto now = std::chrono::steady_clock::now();
                if (!gap_wait_started_) {
                    gap_wait_started_ = now;
                    return;
                }
                const auto waited = std::chrono::duration_cast<std::chrono::milliseconds>(now - *gap_wait_started_);
                if (waited >= std::chrono::milliseconds(8) || pending_payloads_.size() > 16) {
                    *next_data_sequence_ = pending_payloads_.begin()->first;
                    gap_wait_started_.reset();
                    continue;
                }
                return;
            }
            gap_wait_started_.reset();
            const auto &payload = found->second;
            forward_payload(payload.data(), static_cast<uint32_t>(payload.size()), video_socket, video_addr, audio_socket, audio_addr, bridge_sequence);
            pending_payloads_.erase(found);
            ++(*next_data_sequence_);
        }
    }

    void send_control_packet(uint8_t packet_type, uint8_t flags, uint32_t sequence, uint32_t ack, const sockaddr_in &remote)
    {
        const std::string channel = "control";
        std::vector<uint8_t> response(36 + channel.size());
        response[0] = 'C';
        response[1] = 'N';
        response[2] = 'R';
        response[3] = '0';
        response[4] = 0;
        response[5] = packet_type;
        response[6] = flags;
        response[7] = static_cast<uint8_t>(36 + channel.size());
        write_be32(response, 8, parts_.connection_id);
        write_be32(response, 12, sequence);
        write_be32(response, 16, ack);
        write_be32(response, 20, 0);
        write_be16(response, 24, 0);
        write_be16(response, 26, 0);
        write_be16(response, 28, 0);
        write_be32(response, 30, 0);
        response[34] = static_cast<uint8_t>(channel.size());
        response[35] = 0;
        std::copy(channel.begin(), channel.end(), response.begin() + 36);
        sendto(socket_, reinterpret_cast<const char *>(response.data()), static_cast<int>(response.size()), 0,
               reinterpret_cast<const sockaddr *>(&remote), sizeof(remote));
    }

    void send_media_packet(const std::vector<uint8_t> &payload, uint32_t sequence, const sockaddr_in &remote)
    {
        const std::string channel = "media";
        std::vector<uint8_t> response(36 + channel.size() + payload.size());
        response[0] = 'C';
        response[1] = 'N';
        response[2] = 'R';
        response[3] = '0';
        response[4] = 0;
        response[5] = 3;
        response[6] = 0x01;
        response[7] = static_cast<uint8_t>(36 + channel.size());
        write_be32(response, 8, parts_.connection_id);
        write_be32(response, 12, sequence);
        write_be32(response, 16, 0);
        write_be32(response, 20, 0);
        write_be16(response, 24, 0);
        write_be16(response, 26, 0);
        write_be16(response, 28, 0);
        write_be32(response, 30, static_cast<uint32_t>(payload.size()));
        response[34] = static_cast<uint8_t>(channel.size());
        response[35] = 0;
        std::copy(channel.begin(), channel.end(), response.begin() + 36);
        std::copy(payload.begin(), payload.end(), response.begin() + 36 + channel.size());
        sendto(socket_, reinterpret_cast<const char *>(response.data()), static_cast<int>(response.size()), 0,
               reinterpret_cast<const sockaddr *>(&remote), sizeof(remote));
    }

    SOCKET socket_ = INVALID_SOCKET;
    std::optional<sockaddr_in> last_remote_;
#endif
    std::atomic<bool> running_{false};
    std::thread worker_;
    RudpUrlParts parts_;
    std::map<uint32_t, std::vector<uint8_t>> pending_payloads_;
    std::map<std::string, MuninnVideoFrameAssembly> video_assemblies_;
    std::optional<uint32_t> next_data_sequence_;
    std::optional<std::chrono::steady_clock::time_point> gap_wait_started_;
    uint64_t packets_forwarded_ = 0;
    uint64_t bytes_forwarded_ = 0;
    uint64_t payloads_decoded_dropped_ = 0;
    uint64_t video_assemblies_expired_ = 0;
    uint64_t video_forward_failures_ = 0;
    uint64_t audio_forward_failures_ = 0;
    std::string source_url_;
    RudpBridgeOutputs outputs_;
};

static std::string obs_string(obs_data_t *settings, const char *key, const char *fallback)
{
    const char *value = obs_data_get_string(settings, key);
    return value && *value ? std::string(value) : std::string(fallback);
}

static const MuninnStreamOption *find_selected(const std::vector<MuninnStreamOption> &options, const std::string &stream_id)
{
    for (const auto &option : options) {
        if (option.stream_id == stream_id) {
            return &option;
        }
    }
    return options.empty() ? nullptr : &options.front();
}

static std::string quote_windows_argument(const std::string &value)
{
    std::string result = "\"";
    size_t backslashes = 0;
    for (const char ch : value) {
        if (ch == '\\') {
            ++backslashes;
            continue;
        }
        if (ch == '"') {
            result.append(backslashes * 2 + 1, '\\');
            result.push_back('"');
            backslashes = 0;
            continue;
        }
        result.append(backslashes, '\\');
        backslashes = 0;
        result.push_back(ch);
    }
    result.append(backslashes * 2, '\\');
    result.push_back('"');
    return result;
}

static bool run_detached_command(const std::vector<std::string> &args)
{
    if (args.empty()) {
        return false;
    }

    std::string command_line;
    for (const auto &arg : args) {
        if (!command_line.empty()) {
            command_line.push_back(' ');
        }
        command_line += quote_windows_argument(arg);
    }

#ifdef _WIN32
    STARTUPINFOA startup = {};
    startup.cb = sizeof(startup);
    PROCESS_INFORMATION process = {};
    std::vector<char> mutable_command(command_line.begin(), command_line.end());
    mutable_command.push_back('\0');
    const BOOL ok = CreateProcessA(
        nullptr,
        mutable_command.data(),
        nullptr,
        nullptr,
        FALSE,
        CREATE_NO_WINDOW,
        nullptr,
        nullptr,
        &startup,
        &process);
    if (!ok) {
        blog(LOG_WARNING, "Muninn Stream could not launch command %s", command_line.c_str());
        return false;
    }
    CloseHandle(process.hThread);
    CloseHandle(process.hProcess);
    return true;
#else
    return false;
#endif
}

static std::string expected_rudp_stream_url(const MuninnSource *ctx, const std::string &stream_id)
{
    return "rudp://" + ctx->media_target_host + ":" + std::to_string(ctx->media_port) + "/" + stream_id +
           "?channel=media&format=muninn-typed-media&connection=0x6d750001";
}

static std::string canonical_muninn_stream_id(const std::string &stream_id)
{
    const auto colon = stream_id.rfind(':');
    if (colon == std::string::npos || colon + 1 >= stream_id.size()) {
        return stream_id;
    }
    const auto suffix = stream_id.substr(colon + 1);
    if (std::all_of(suffix.begin(), suffix.end(), [](unsigned char ch) { return std::isdigit(ch) != 0; })) {
        return stream_id.substr(0, colon);
    }
    return stream_id;
}

static void request_stream_activation(MuninnSource *ctx, const std::string &stream_id)
{
    const auto canonical_stream_id = canonical_muninn_stream_id(stream_id);
    if (!ctx || canonical_stream_id.empty() || ctx->command_exe_path.empty() || ctx->command_rudp_target.empty()) {
        return;
    }
    if (ctx->last_requested_stream_id == canonical_stream_id) {
        return;
    }
    ctx->last_requested_stream_id = canonical_stream_id;

    const std::vector<std::string> args = {
        ctx->command_exe_path,
        "request-stream",
        "--host",
        ctx->host_id,
        "--stream",
        canonical_stream_id,
        "--stream-action",
        "start",
        "--target-host",
        ctx->media_target_host,
        "--port",
        std::to_string(ctx->media_port),
        "--media-transport",
        "rudp",
        "--no-obs-target",
        "--capture-command-rudp-target",
        ctx->command_rudp_target,
    };
    std::thread([args] {
        if (!run_detached_command(args)) {
            blog(LOG_WARNING, "Muninn Stream failed to publish activation request");
        }
    }).detach();
    blog(LOG_INFO, "Muninn Stream requested activation for %s over RUDP %s", canonical_stream_id.c_str(), ctx->command_rudp_target.c_str());
}

class MsgpackReader {
public:
    explicit MsgpackReader(const std::vector<uint8_t> &bytes) : data_(bytes.data()), size_(bytes.size()) {}
    MsgpackReader(const uint8_t *data, size_t size) : data_(data), size_(size) {}

    uint32_t read_array_len()
    {
        const uint8_t tag = read_u8();
        if ((tag & 0xf0) == 0x90) {
            return tag & 0x0f;
        }
        if (tag == 0xdc) {
            return read_u16();
        }
        if (tag == 0xdd) {
            return read_u32();
        }
        throw std::runtime_error("expected MessagePack array");
    }

    std::string read_string()
    {
        const uint8_t tag = read_u8();
        uint32_t length = 0;
        if ((tag & 0xe0) == 0xa0) {
            length = tag & 0x1f;
        } else if (tag == 0xd9) {
            length = read_u8();
        } else if (tag == 0xda) {
            length = read_u16();
        } else if (tag == 0xdb) {
            length = read_u32();
        } else {
            throw std::runtime_error("expected MessagePack string");
        }
        require(length);
        std::string value(reinterpret_cast<const char *>(data_ + pos_), length);
        pos_ += length;
        return value;
    }

    std::vector<std::string> read_string_array()
    {
        const uint32_t length = read_array_len();
        std::vector<std::string> values;
        values.reserve(length);
        for (uint32_t index = 0; index < length; ++index) {
            values.push_back(read_string());
        }
        return values;
    }

    std::vector<uint8_t> read_bytes()
    {
        const uint8_t tag = read_u8();
        uint32_t length = 0;
        if (tag == 0xc4) {
            length = read_u8();
        } else if (tag == 0xc5) {
            length = read_u16();
        } else if (tag == 0xc6) {
            length = read_u32();
        } else if ((tag & 0xf0) == 0x90 || tag == 0xdc || tag == 0xdd) {
            pos_--;
            length = read_array_len();
            std::vector<uint8_t> bytes;
            bytes.reserve(length);
            for (uint32_t index = 0; index < length; ++index) {
                bytes.push_back(static_cast<uint8_t>(read_unsigned()));
            }
            return bytes;
        } else {
            throw std::runtime_error("expected MessagePack bytes");
        }
        require(length);
        std::vector<uint8_t> bytes(data_ + pos_, data_ + pos_ + length);
        pos_ += length;
        return bytes;
    }

    uint64_t read_unsigned()
    {
        const uint8_t tag = read_u8();
        if (tag <= 0x7f) {
            return tag;
        }
        if (tag == 0xcc) {
            return read_u8();
        }
        if (tag == 0xcd) {
            return read_u16();
        }
        if (tag == 0xce) {
            return read_u32();
        }
        if (tag == 0xcf) {
            return read_u64();
        }
        throw std::runtime_error("expected MessagePack unsigned integer");
    }

    void skip()
    {
        const uint8_t tag = read_u8();
        if (tag <= 0x7f || tag >= 0xe0 || tag == 0xc0 || tag == 0xc2 || tag == 0xc3) {
            return;
        }
        if ((tag & 0xe0) == 0xa0) {
            skip_bytes(tag & 0x1f);
            return;
        }
        if ((tag & 0xf0) == 0x90) {
            skip_items(tag & 0x0f);
            return;
        }
        if ((tag & 0xf0) == 0x80) {
            skip_items((tag & 0x0f) * 2);
            return;
        }
        switch (tag) {
        case 0xc4:
        case 0xd9:
            skip_bytes(read_u8());
            return;
        case 0xc5:
        case 0xda:
            skip_bytes(read_u16());
            return;
        case 0xc6:
        case 0xdb:
            skip_bytes(read_u32());
            return;
        case 0xcc:
        case 0xd0:
            skip_bytes(1);
            return;
        case 0xcd:
        case 0xd1:
            skip_bytes(2);
            return;
        case 0xce:
        case 0xd2:
        case 0xca:
            skip_bytes(4);
            return;
        case 0xcf:
        case 0xd3:
        case 0xcb:
            skip_bytes(8);
            return;
        case 0xdc:
            skip_items(read_u16());
            return;
        case 0xdd:
            skip_items(read_u32());
            return;
        case 0xde:
            skip_items(read_u16() * 2);
            return;
        case 0xdf:
            skip_items(read_u32() * 2);
            return;
        default:
            throw std::runtime_error("unsupported MessagePack tag");
        }
    }

private:
    const uint8_t *data_;
    size_t size_;
    size_t pos_ = 0;

    void require(size_t count) const
    {
        if (count > size_ - pos_) {
            throw std::runtime_error("truncated MessagePack input");
        }
    }

    uint8_t read_u8()
    {
        require(1);
        return data_[pos_++];
    }

    uint16_t read_u16()
    {
        require(2);
        const uint16_t value = (static_cast<uint16_t>(data_[pos_]) << 8) | static_cast<uint16_t>(data_[pos_ + 1]);
        pos_ += 2;
        return value;
    }

    uint32_t read_u32()
    {
        require(4);
        const uint32_t value = (static_cast<uint32_t>(data_[pos_]) << 24) | (static_cast<uint32_t>(data_[pos_ + 1]) << 16) |
                               (static_cast<uint32_t>(data_[pos_ + 2]) << 8) | static_cast<uint32_t>(data_[pos_ + 3]);
        pos_ += 4;
        return value;
    }

    uint64_t read_u64()
    {
        const uint64_t high = read_u32();
        const uint64_t low = read_u32();
        return (high << 32) | low;
    }

    void skip_bytes(size_t count)
    {
        require(count);
        pos_ += count;
    }

    void skip_items(uint32_t count)
    {
        for (uint32_t index = 0; index < count; ++index) {
            skip();
        }
    }
};

static std::vector<MuninnStreamOption> decode_obs_catalog_payload(const std::vector<uint8_t> &payload)
{
    MsgpackReader reader(payload);
    const uint32_t length = reader.read_array_len();
    if (length < 7) {
        return {};
    }

    reader.skip(); // catalog_id
    reader.skip(); // host_id
    auto stream_ids = reader.read_string_array();
    auto labels = reader.read_string_array();
    auto urls = reader.read_string_array();
    auto states = reader.read_string_array();
    reader.skip(); // updated_at

    std::vector<MuninnStreamOption> options;
    const size_t count = std::min({stream_ids.size(), labels.size(), urls.size(), states.size()});
    options.reserve(count);
    for (size_t index = 0; index < count; ++index) {
        if (stream_ids[index].empty()) {
            continue;
        }
        options.push_back(MuninnStreamOption{
            stream_ids[index],
            labels[index].empty() ? stream_ids[index] : labels[index],
            urls[index],
            states[index],
        });
    }

    std::sort(options.begin(), options.end(), [](const auto &left, const auto &right) {
        return left.label < right.label;
    });
    return options;
}

static std::vector<MuninnStreamOption> read_catalog(const std::string &path)
{
    std::ifstream file(path, std::ios::binary);
    if (!file) {
        return {};
    }

    std::vector<uint8_t> bytes((std::istreambuf_iterator<char>(file)), std::istreambuf_iterator<char>());
    if (bytes.empty()) {
        return {};
    }

    try {
        MsgpackReader reader(bytes);
        const uint32_t snapshot_len = reader.read_array_len();
        if (snapshot_len < 3 || reader.read_string() != "cultcache.store.v1") {
            return {};
        }

        const uint32_t catalog_count = reader.read_array_len();
        for (uint32_t index = 0; index < catalog_count; ++index) {
            reader.skip();
        }

        const uint32_t record_count = reader.read_array_len();
        for (uint32_t index = 0; index < record_count; ++index) {
            const uint32_t record_len = reader.read_array_len();
            if (record_len < 4) {
                for (uint32_t item = 0; item < record_len; ++item) {
                    reader.skip();
                }
                continue;
            }

            const std::string key = reader.read_string();
            const std::string schema = reader.read_string();
            reader.skip(); // stored_at
            const auto payload = reader.read_bytes();
            for (uint32_t item = 4; item < record_len; ++item) {
                reader.skip();
            }

            if (key == MuninnObsCatalogKey && (schema == MuninnObsCatalogType || schema == MuninnObsCatalogSchema)) {
                return decode_obs_catalog_payload(payload);
            }
        }
    } catch (const std::exception &error) {
        blog(LOG_WARNING, "Muninn Stream could not read CultCache store %s: %s", path.c_str(), error.what());
    }

    return {};
}

static void release_media(MuninnSource *ctx)
{
    if (ctx->bridge) {
        ctx->bridge->stop();
    }
    if (ctx->audio_media) {
        obs_source_remove_audio_capture_callback(ctx->audio_media, muninn_child_audio, ctx);
        obs_source_remove_active_child(ctx->source, ctx->audio_media);
        obs_source_release(ctx->audio_media);
        ctx->audio_media = nullptr;
    }
    if (ctx->media) {
        obs_source_remove_audio_capture_callback(ctx->media, muninn_child_audio, ctx);
        obs_source_remove_active_child(ctx->source, ctx->media);
        obs_source_release(ctx->media);
        ctx->media = nullptr;
    }
}

static obs_source_t *create_ffmpeg_child_source(MuninnSource *ctx,
                                                const char *name,
                                                const std::string &media_url,
                                                const char *input_format,
                                                bool capture_audio)
{
    obs_data_t *settings = obs_data_create();
    obs_data_set_bool(settings, "is_local_file", false);
    obs_data_set_string(settings, "input", media_url.c_str());
    obs_data_set_string(settings, "input_format", input_format);
    obs_data_set_string(settings, "ffmpeg_options", "fflags=nobuffer flags=low_delay probesize=32768 analyzeduration=0");
    obs_data_set_bool(settings, "restart_on_activate", true);
    obs_data_set_bool(settings, "clear_on_media_end", false);
    obs_data_set_bool(settings, "close_when_inactive", false);

    obs_source_t *source = obs_source_create_private("ffmpeg_source", name, settings);
    obs_data_release(settings);
    if (!source) {
        blog(LOG_WARNING, "Muninn Stream could not create OBS ffmpeg_source for %s", media_url.c_str());
        return nullptr;
    }
    if (capture_audio) {
        obs_source_add_audio_capture_callback(source, muninn_child_audio, ctx);
    }
    obs_source_add_active_child(ctx->source, source);
    return source;
}

static void create_or_update_media(MuninnSource *ctx, const std::string &url)
{
    if (url.empty()) {
        release_media(ctx);
        ctx->stream_url.clear();
        return;
    }

    if (ctx->media && ctx->stream_url == url) {
        return;
    }

    release_media(ctx);

    if (starts_with(url, "rudp://")) {
        if (!ctx->bridge) {
            ctx->bridge = std::make_unique<RudpMediaBridge>();
        }
        const auto outputs = ctx->bridge->start(url);
        ctx->media = create_ffmpeg_child_source(ctx, "Muninn Stream Video", outputs.video_url, "h264", false);
        ctx->audio_media = create_ffmpeg_child_source(ctx, "Muninn Stream Audio", outputs.audio_url, "adts", true);
        if (ctx->media || ctx->audio_media) {
            ctx->stream_url = url;
        } else {
            ctx->stream_url.clear();
        }
        return;
    } else if (ctx->bridge) {
        ctx->bridge->stop();
    }

    ctx->media = create_ffmpeg_child_source(ctx, "Muninn Stream Media", url, "mpegts", true);
    if (ctx->media) {
        ctx->stream_url = url;
    } else {
        ctx->stream_url.clear();
    }
}

static void populate_stream_list(obs_property_t *property, const std::string &store_path)
{
    obs_property_list_clear(property);
    const auto options = read_catalog(store_path);
    if (options.empty()) {
        obs_property_list_add_string(property, "No active Muninn streams", "");
        return;
    }

    for (const auto &option : options) {
        std::string label = option.label;
        if (!option.state.empty()) {
            label += " [" + option.state + "]";
        }
        if (option.url.empty()) {
            label += " (activation required)";
        }
        obs_property_list_add_string(property, label.c_str(), option.stream_id.c_str());
    }
}

static bool muninn_refresh_streams(obs_properties_t *props, obs_property_t *, void *data)
{
    std::string store_path = MuninnStorePath;
    if (data) {
        auto *ctx = static_cast<MuninnSource *>(data);
        store_path = ctx->store_path;
    }

    obs_property_t *stream_list = obs_properties_get(props, "stream_id");
    if (stream_list) {
        populate_stream_list(stream_list, store_path);
    }
    return true;
}

static const char *muninn_get_name(void *)
{
    return "Muninn Stream";
}

static void audio_worker_loop(MuninnSource *ctx)
{
    while (true) {
        MuninnQueuedAudio packet;
        {
            std::unique_lock<std::mutex> lock(ctx->audio_mutex);
            ctx->audio_cv.wait(lock, [&] { return !ctx->audio_worker_running || !ctx->audio_queue.empty(); });
            if (!ctx->audio_worker_running && ctx->audio_queue.empty()) {
                return;
            }
            packet = std::move(ctx->audio_queue.front());
            ctx->audio_queue.pop_front();
        }

        if (!ctx->source || packet.frames == 0) {
            continue;
        }

        struct obs_source_audio audio = {};
        for (size_t plane = 0; plane < MAX_AV_PLANES; ++plane) {
            if (!packet.planes[plane].empty()) {
                audio.data[plane] = packet.planes[plane].data();
            }
        }
        audio.frames = packet.frames;
        audio.timestamp = packet.timestamp;
        audio.format = AUDIO_FORMAT_FLOAT_PLANAR;
        audio.speakers = packet.speakers;
        audio.samples_per_sec = packet.samples_per_sec;
        obs_source_output_audio(ctx->source, &audio);
    }
}

static void start_audio_worker(MuninnSource *ctx)
{
    std::lock_guard<std::mutex> lock(ctx->audio_mutex);
    if (ctx->audio_worker_running) {
        return;
    }
    ctx->audio_worker_running = true;
    ctx->audio_worker = std::thread([ctx] { audio_worker_loop(ctx); });
}

static void stop_audio_worker(MuninnSource *ctx)
{
    {
        std::lock_guard<std::mutex> lock(ctx->audio_mutex);
        ctx->audio_worker_running = false;
        ctx->audio_queue.clear();
    }
    ctx->audio_cv.notify_all();
    if (ctx->audio_worker.joinable()) {
        ctx->audio_worker.join();
    }
}

static void muninn_update(void *data, obs_data_t *settings)
{
    auto *ctx = static_cast<MuninnSource *>(data);
    ctx->store_path = obs_string(settings, "store_path", MuninnStorePath);
    ctx->command_exe_path = obs_string(settings, "command_exe_path", MuninnCommandExePath);
    ctx->command_rudp_target = obs_string(settings, "command_rudp_target", MuninnCommandRudpTarget);
    ctx->host_id = obs_string(settings, "host_id", MuninnDefaultHostId);
    ctx->media_target_host = obs_string(settings, "media_target_host", MuninnDefaultMediaTargetHost);
    ctx->media_port = static_cast<uint16_t>(obs_data_get_int(settings, "media_port") > 0
                                                ? obs_data_get_int(settings, "media_port")
                                                : MuninnDefaultMediaPort);
    ctx->stream_id = obs_string(settings, "stream_id", "");

    const auto options = read_catalog(ctx->store_path);
    const auto *selected = find_selected(options, ctx->stream_id);
    if (!selected) {
        create_or_update_media(ctx, "");
        return;
    }

    ctx->stream_id = selected->stream_id;
    request_stream_activation(ctx, selected->stream_id);
    if (selected->url.empty()) {
        create_or_update_media(ctx, expected_rudp_stream_url(ctx, selected->stream_id));
    } else {
        create_or_update_media(ctx, selected->url);
    }
}

static void *muninn_create(obs_data_t *settings, obs_source_t *source)
{
    auto *ctx = new MuninnSource();
    ctx->source = source;
    ctx->bridge = std::make_unique<RudpMediaBridge>();
    start_audio_worker(ctx);
    muninn_update(ctx, settings);
    return ctx;
}

static void muninn_destroy(void *data)
{
    auto *ctx = static_cast<MuninnSource *>(data);
    release_media(ctx);
    stop_audio_worker(ctx);
    delete ctx;
}

static uint32_t muninn_width(void *data)
{
    auto *ctx = static_cast<MuninnSource *>(data);
    return ctx->media ? obs_source_get_width(ctx->media) : 0;
}

static uint32_t muninn_height(void *data)
{
    auto *ctx = static_cast<MuninnSource *>(data);
    return ctx->media ? obs_source_get_height(ctx->media) : 0;
}

static void muninn_defaults(obs_data_t *settings)
{
    obs_data_set_default_string(settings, "store_path", MuninnStorePath);
    obs_data_set_default_string(settings, "command_exe_path", MuninnCommandExePath);
    obs_data_set_default_string(settings, "command_rudp_target", MuninnCommandRudpTarget);
    obs_data_set_default_string(settings, "host_id", MuninnDefaultHostId);
    obs_data_set_default_string(settings, "media_target_host", MuninnDefaultMediaTargetHost);
    obs_data_set_default_int(settings, "media_port", MuninnDefaultMediaPort);
    obs_data_set_default_string(settings, "stream_id", "");
}

static obs_properties_t *muninn_properties(void *data)
{
    auto *ctx = static_cast<MuninnSource *>(data);
    const auto store_path = ctx ? ctx->store_path : std::string(MuninnStorePath);

    obs_properties_t *props = obs_properties_create();
    obs_properties_add_text(props, "store_path", "Muninn CultCache store", OBS_TEXT_DEFAULT);
    obs_properties_add_text(props, "command_exe_path", "Muninn command executable", OBS_TEXT_DEFAULT);
    obs_properties_add_text(props, "command_rudp_target", "Muninn command RUDP target", OBS_TEXT_DEFAULT);
    obs_properties_add_text(props, "host_id", "Muninn host", OBS_TEXT_DEFAULT);
    obs_properties_add_text(props, "media_target_host", "Muninn media target host", OBS_TEXT_DEFAULT);
    obs_properties_add_int(props, "media_port", "Muninn media UDP port", 1, 65535, 1);
    obs_property_t *streams = obs_properties_add_list(props, "stream_id", "Muninn stream", OBS_COMBO_TYPE_LIST, OBS_COMBO_FORMAT_STRING);
    populate_stream_list(streams, store_path);
    obs_properties_add_button(props, "refresh_streams", "Refresh Muninn streams", muninn_refresh_streams);
    return props;
}

static void muninn_render(void *data, gs_effect_t *)
{
    auto *ctx = static_cast<MuninnSource *>(data);
    if (ctx->media) {
        obs_source_video_render(ctx->media);
    }
}

static void muninn_child_audio(void *param, obs_source_t *, const struct audio_data *audio_data, bool muted)
{
    auto *ctx = static_cast<MuninnSource *>(param);
    if (!ctx || !ctx->source || !audio_data || muted || audio_data->frames == 0) {
        return;
    }

    struct obs_audio_info2 audio_info = {};
    if (!obs_get_audio_info2(&audio_info)) {
        return;
    }

    MuninnQueuedAudio packet;
    packet.frames = audio_data->frames;
    packet.timestamp = audio_data->timestamp;
    packet.speakers = audio_info.speakers;
    packet.samples_per_sec = audio_info.samples_per_sec;
    const size_t bytes_per_plane = static_cast<size_t>(audio_data->frames) * sizeof(float);
    for (size_t plane = 0; plane < MAX_AV_PLANES; ++plane) {
        if (!audio_data->data[plane]) {
            continue;
        }
        packet.planes[plane].resize(bytes_per_plane);
        std::memcpy(packet.planes[plane].data(), audio_data->data[plane], bytes_per_plane);
    }

    {
        std::lock_guard<std::mutex> lock(ctx->audio_mutex);
        if (!ctx->audio_worker_running) {
            return;
        }
        while (ctx->audio_queue.size() >= 4) {
            ctx->audio_queue.pop_front();
        }
        ctx->audio_queue.push_back(std::move(packet));
    }
    ctx->audio_cv.notify_one();
}

static void muninn_enum_active_sources(void *data, obs_source_enum_proc_t enum_callback, void *param)
{
    auto *ctx = static_cast<MuninnSource *>(data);
    if (ctx->media) {
        enum_callback(ctx->source, ctx->media, param);
    }
    if (ctx->audio_media) {
        enum_callback(ctx->source, ctx->audio_media, param);
    }
}

static obs_source_info muninn_source_info = {
    .id = "muninn_stream_source",
    .type = OBS_SOURCE_TYPE_INPUT,
    .output_flags = OBS_SOURCE_VIDEO | OBS_SOURCE_AUDIO,
    .get_name = muninn_get_name,
    .create = muninn_create,
    .destroy = muninn_destroy,
    .get_width = muninn_width,
    .get_height = muninn_height,
    .get_defaults = muninn_defaults,
    .get_properties = muninn_properties,
    .update = muninn_update,
    .video_render = muninn_render,
    .enum_active_sources = muninn_enum_active_sources,
};

} // namespace

void mimir_register_muninn_source()
{
    obs_register_source(&muninn_source_info);
}
