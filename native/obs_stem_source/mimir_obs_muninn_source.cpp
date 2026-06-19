#ifdef _WIN32
#include <winsock2.h>
#include <ws2tcpip.h>
#include <windows.h>
#endif

#include <obs-module.h>
#include <util/platform.h>

#include "muninn_media_wire.h"
#include "muninn_rudp_ack.h"
#include "muninn_rudp_fragments.h"
#include "muninn_rudp_url.h"

#include <algorithm>
#include <array>
#include <atomic>
#include <chrono>
#include <condition_variable>
#include <cstdio>
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
#include <set>
#include <sstream>
#include <stdexcept>
#include <string>
#include <thread>
#include <utility>
#include <vector>

namespace {

constexpr const char *MuninnStorePath = "C:\\Meta\\Odin\\state\\muninn.telemetry.cc";
constexpr const char *MuninnCommandExePath = "C:\\Meta\\Odin\\Muninn\\muninn.exe";
constexpr const char *MuninnCommandRudpTarget = "192.168.1.84:17873";
constexpr const char *MuninnDeprecatedWireGuardCommandRudpTarget = "10.77.0.4:17873";
constexpr const char *MuninnDefaultHostId = "raven";
constexpr const char *MuninnDefaultMediaTargetHost = "192.168.1.66";
constexpr const char *MuninnDeprecatedWireGuardMediaTargetHost = "10.77.0.2";
constexpr uint16_t MuninnDefaultMediaPort = 5204;
constexpr const char *MuninnObsCatalogType = "muninn.obs_stream_catalog";
constexpr const char *MuninnObsCatalogSchema = "muninn.obs_stream_catalog.v1";
constexpr const char *MuninnObsCatalogKey = "obs";
constexpr uint64_t MuninnCatalogRefreshMs = 2000;
constexpr uint64_t MuninnActivationRetryMs = 5000;
constexpr uint64_t MuninnMediaStartupGraceMs = 3000;
constexpr uint64_t MuninnMediaStaleMs = 1500;
constexpr uint64_t MuninnMediaRestartCooldownMs = 5000;
constexpr uint64_t MuninnReceiverKeyframeRequestCooldownMs = 500;
constexpr uint64_t MuninnReceiverChunkRepairFirstWaitMs = 64;
constexpr uint64_t MuninnVideoAssemblyExpiryScanMs = 10;
constexpr size_t MuninnExpiredVideoFrameRememberCount = 4096;
constexpr size_t MuninnVideoParityCacheMaxEntries = 4096;
constexpr uint16_t MuninnCatalogDiscoveryPort = 17874;
constexpr uint32_t MuninnCatalogDiscoveryConnectionId = 0x6d750003;
constexpr uint32_t MuninnCommandRudpConnectionId = 0x6d750002;
constexpr uint32_t MuninnDefaultMediaPacketBytes = 848;
constexpr uint32_t MuninnDefaultRudpVideoBitrateKbps = 12000;
constexpr uint32_t MuninnDefaultRudpLatencyBudgetMs = 2000;
constexpr uint32_t MuninnDefaultRudpAudioReorderMs = muninn_rudp_url::DefaultAudioReorderMs;
constexpr uint32_t MuninnMaxRudpVideoBitrateKbps = 100000;
constexpr uint32_t MuninnMaxRudpLatencyBudgetMs = 2000;
constexpr uint32_t MuninnMaxRudpAudioReorderMs = 2000;
constexpr size_t MuninnAudioReorderMaxPackets = 128;
constexpr size_t MuninnAudioQueueMaxPackets = 32;

struct MuninnStreamOption {
    std::string stream_id;
    std::string label;
    std::string url;
    std::string state;
    std::string command_rudp_target;
    std::string media_target_host;
    uint16_t media_port = MuninnDefaultMediaPort;
    uint32_t media_packet_bytes = MuninnDefaultMediaPacketBytes;
    uint32_t rudp_video_bitrate_kbps = MuninnDefaultRudpVideoBitrateKbps;
    uint32_t rudp_latency_budget_ms = MuninnDefaultRudpLatencyBudgetMs;
    std::vector<std::pair<std::string, std::string>> video_sources;
    std::vector<std::pair<std::string, std::string>> audio_sources;
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
    std::unique_ptr<class FfmpegAudioDecoder> audio_decoder;
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
    uint32_t media_packet_bytes = MuninnDefaultMediaPacketBytes;
    uint32_t rudp_video_bitrate_kbps = MuninnDefaultRudpVideoBitrateKbps;
    uint32_t rudp_latency_budget_ms = MuninnDefaultRudpLatencyBudgetMs;
    std::vector<std::pair<std::string, std::string>> video_sources;
    std::vector<std::pair<std::string, std::string>> audio_sources;
    uint32_t rudp_audio_reorder_ms = MuninnDefaultRudpAudioReorderMs;
    std::string stream_id;
    std::string video_source_id;
    std::string audio_source_id;
    std::string stream_url;
    std::string last_requested_stream_id;
    std::string last_requested_video_source_id;
    std::string last_requested_audio_source_id;
    std::vector<MuninnStreamOption> catalog_options;
    std::unique_ptr<class CatalogDiscoveryReceiver> catalog_discovery;
    std::chrono::steady_clock::time_point next_catalog_refresh = std::chrono::steady_clock::now();
    std::chrono::steady_clock::time_point last_activation_request = std::chrono::steady_clock::time_point::min();
    std::chrono::steady_clock::time_point last_media_restart = std::chrono::steady_clock::time_point::min();
};

static void muninn_child_audio(void *param, obs_source_t *source, const struct audio_data *audio_data, bool muted);
static void stop_audio_worker(MuninnSource *ctx);
static std::string canonical_muninn_stream_id(const std::string &stream_id);

struct RudpBridgeOutputs {
    std::string video_url;
    std::string audio_url;
};

struct MuninnMediaPayload {
    enum class Kind { Unknown, Video, VideoParity, Audio };
    Kind kind = Kind::Unknown;
    std::string stream_id;
    std::string session_id;
    uint64_t frame_id = 0;
    uint64_t audio_packet_id = 0;
    uint64_t audio_pts_ticks = 0;
    uint64_t audio_duration_ticks = 0;
    uint32_t audio_timebase_num = 0;
    uint32_t audio_timebase_den = 0;
    uint64_t audio_deadline_ticks = 0;
    bool keyframe = false;
    std::optional<uint64_t> dependency_frame_id;
    uint16_t chunk_index = 0;
    uint16_t chunk_count = 1;
    uint16_t parity_index = 0;
    uint16_t parity_count = 0;
    std::vector<uint32_t> chunk_payload_lengths;
    std::vector<uint8_t> payload;
};

struct MuninnVideoFrameAssembly {
    std::string stream_id;
    std::string session_id;
    uint64_t frame_id = 0;
    bool keyframe = false;
    std::optional<uint64_t> dependency_frame_id;
    uint16_t chunk_count = 0;
    uint16_t chunks_received = 0;
    std::chrono::steady_clock::time_point started_at;
    bool repair_requested = false;
    std::vector<uint32_t> chunk_payload_lengths;
    uint16_t parity_count = 0;
    std::map<uint16_t, std::vector<uint8_t>> parity_payloads;
    std::vector<std::vector<uint8_t>> chunks;
};

struct MuninnVideoParityCacheEntry {
    std::string stream_id;
    std::string session_id;
    uint64_t frame_id = 0;
    bool keyframe = false;
    std::optional<uint64_t> dependency_frame_id;
    uint16_t chunk_count = 0;
    uint16_t parity_index = 0;
    uint16_t parity_count = 0;
    std::chrono::steady_clock::time_point received_at;
    std::vector<uint32_t> chunk_payload_lengths;
    std::vector<uint8_t> payload;
};

struct MuninnPendingAudioPacket {
    std::vector<uint8_t> payload;
    std::chrono::steady_clock::time_point received_at;
};

static bool starts_with(const std::string &value, const std::string &prefix)
{
    return value.rfind(prefix, 0) == 0;
}

static uint64_t steady_millis()
{
    return static_cast<uint64_t>(
        std::chrono::duration_cast<std::chrono::milliseconds>(
            std::chrono::steady_clock::now().time_since_epoch())
            .count());
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

    std::optional<uint64_t> read_optional_unsigned()
    {
        const uint8_t tag = read_u8();
        if (tag == 0xc0) {
            return std::nullopt;
        }
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
        throw std::runtime_error("expected optional MessagePack unsigned integer");
    }

    bool read_bool()
    {
        const uint8_t tag = read_u8();
        if (tag == 0xc2) {
            return false;
        }
        if (tag == 0xc3) {
            return true;
        }
        throw std::runtime_error("expected MessagePack boolean");
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
            media.keyframe = record.read_bool();
            media.dependency_frame_id = record.read_optional_unsigned();
            record.skip(); // deadline_ticks
            media.chunk_index = static_cast<uint16_t>(record.read_unsigned());
            media.chunk_count = static_cast<uint16_t>(record.read_unsigned());
            media.payload = record.read_bytes();
            return media;
        }
        if (schema_id == "muninn.media_video_parity_shard.v1") {
            if (record_len < 16) {
                return std::nullopt;
            }
            MuninnMediaPayload media;
            media.kind = MuninnMediaPayload::Kind::VideoParity;
            media.stream_id = record.read_string();
            media.session_id = record.read_string();
            media.frame_id = record.read_unsigned();
            record.skip(); // codec
            record.skip(); // pts_ticks
            record.skip(); // duration_ticks
            record.skip(); // timebase_num
            record.skip(); // timebase_den
            media.keyframe = record.read_bool();
            media.dependency_frame_id = record.read_optional_unsigned();
            record.skip(); // deadline_ticks
            media.chunk_count = static_cast<uint16_t>(record.read_unsigned());
            media.parity_index = static_cast<uint16_t>(record.read_unsigned());
            media.parity_count = static_cast<uint16_t>(record.read_unsigned());
            const uint32_t lengths = record.read_array_len();
            media.chunk_payload_lengths.reserve(lengths);
            for (uint32_t index = 0; index < lengths; ++index) {
                media.chunk_payload_lengths.push_back(static_cast<uint32_t>(record.read_unsigned()));
            }
            media.payload = record.read_bytes();
            return media;
        }
        if (schema_id == "muninn.media_video_parity_shard.v2") {
            if (record_len < 17) {
                return std::nullopt;
            }
            MuninnMediaPayload media;
            media.kind = MuninnMediaPayload::Kind::VideoParity;
            media.stream_id = record.read_string();
            media.session_id = record.read_string();
            media.frame_id = record.read_unsigned();
            record.skip(); // codec
            record.skip(); // pts_ticks
            record.skip(); // duration_ticks
            record.skip(); // timebase_num
            record.skip(); // timebase_den
            media.keyframe = record.read_bool();
            media.dependency_frame_id = record.read_optional_unsigned();
            record.skip(); // deadline_ticks
            media.chunk_count = static_cast<uint16_t>(record.read_unsigned());
            media.parity_index = static_cast<uint16_t>(record.read_unsigned());
            media.parity_count = static_cast<uint16_t>(record.read_unsigned());
            const uint32_t chunk_payload_bytes = static_cast<uint32_t>(record.read_unsigned());
            const uint32_t last_chunk_payload_bytes = static_cast<uint32_t>(record.read_unsigned());
            if (media.chunk_count == 0 ||
                chunk_payload_bytes == 0 ||
                last_chunk_payload_bytes == 0 ||
                last_chunk_payload_bytes > chunk_payload_bytes) {
                return std::nullopt;
            }
            media.chunk_payload_lengths.assign(media.chunk_count, chunk_payload_bytes);
            media.chunk_payload_lengths.back() = last_chunk_payload_bytes;
            media.payload = record.read_bytes();
            return media;
        }
        if (schema_id == "muninn.media_audio_packet.v1") {
            if (record_len < 10) {
                return std::nullopt;
            }
            MuninnMediaPayload media;
            media.kind = MuninnMediaPayload::Kind::Audio;
            media.stream_id = record.read_string();
            media.session_id = record.read_string();
            media.audio_packet_id = record.read_unsigned();
            record.skip(); // codec
            media.audio_pts_ticks = record.read_unsigned();
            media.audio_duration_ticks = record.read_unsigned();
            media.audio_timebase_num = static_cast<uint32_t>(record.read_unsigned());
            media.audio_timebase_den = static_cast<uint32_t>(record.read_unsigned());
            media.audio_deadline_ticks = record.read_unsigned();
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
        const auto parsed = muninn_rudp_url::parse(url);
        if (!parsed) {
            return RudpBridgeOutputs{url, ""};
        }

        source_url_ = url;
        parts_ = *parsed;
        outputs_.video_url = "udp://127.0.0.1:" + std::to_string(parts_.video_local_port) +
                             "?fifo_size=8388608&overrun_nonfatal=1&buffer_size=67108864";
        outputs_.audio_url = "udp://127.0.0.1:" + std::to_string(parts_.audio_local_port) +
                             "?fifo_size=1048576&overrun_nonfatal=1&buffer_size=8388608";
        const uint64_t now = steady_millis();
        started_ms_.store(now);
        last_packet_ms_.store(0);
        last_video_forwarded_ms_.store(0);
        last_audio_forwarded_ms_.store(0);
        stale_reported_.store(false);
        media_connected_ = false;
        running_ = true;
        worker_ = std::thread([this] { run(); });
        blog(LOG_INFO,
             "Muninn RUDP bridge starting udp/%u -> video udp/127.0.0.1:%u audio udp/127.0.0.1:%u sender_resend_delay_ms=%u reliable_expire_after_ms=%u assembly_deadline_ms=%u gap_wait_ms=%u audio_reorder_ms=%u",
             parts_.port, parts_.video_local_port, parts_.audio_local_port,
             parts_.sender_resend_delay_ms, parts_.reliable_expire_after_ms,
             parts_.video_assembly_deadline_ms, parts_.gap_wait_ms, parts_.audio_reorder_ms);
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
        started_ms_.store(0);
        last_packet_ms_.store(0);
        last_video_forwarded_ms_.store(0);
        last_audio_forwarded_ms_.store(0);
        stale_reported_.store(false);
        media_connected_ = false;
    }

    bool has_forwarded_media() const
    {
        return last_video_forwarded_ms_.load() != 0 || last_audio_forwarded_ms_.load() != 0;
    }

    bool is_running() const
    {
        return running_.load();
    }

    bool consume_decoder_reset_request()
    {
        return decoder_reset_requested_.exchange(false);
    }

    bool needs_activation(uint64_t now_ms) const
    {
        const uint64_t started = started_ms_.load();
        if (started == 0) {
            return true;
        }
        if (now_ms >= started && now_ms - started < MuninnMediaStartupGraceMs) {
            return false;
        }
        if (!running_.load()) {
            return true;
        }
        const uint64_t last_packet = last_packet_ms_.load();
        if (last_packet == 0) {
            return true;
        }
        const uint64_t last_video = last_video_forwarded_ms_.load();
        const uint64_t last_audio = last_audio_forwarded_ms_.load();
        const uint64_t last_media = std::max(last_video, last_audio);
        return last_media == 0 || (now_ms > last_media && now_ms - last_media >= MuninnMediaStaleMs);
    }

    bool consume_stale_transition(uint64_t now_ms)
    {
        if (!needs_activation(now_ms)) {
            stale_reported_.store(false);
            return false;
        }
        if (!has_forwarded_media()) {
            stale_reported_.store(false);
            return false;
        }
        bool expected = false;
        return stale_reported_.compare_exchange_strong(expected, true);
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
            running_ = false;
            WSACleanup();
            return;
        }
        const int socket_buffer_bytes = 4 * 1024 * 1024;
        const BOOL reuse_addr = TRUE;
        setsockopt(socket_, SOL_SOCKET, SO_REUSEADDR, reinterpret_cast<const char *>(&reuse_addr),
                   sizeof(reuse_addr));
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
            running_ = false;
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
                expire_rudp_packet_fragments(std::chrono::steady_clock::now());
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
        const bool is_video_connection = connection_id == parts_.connection_id;
        const bool is_audio_connection = connection_id == parts_.audio_connection_id;
        if (version != 0 || (!is_video_connection && !is_audio_connection) || header_bytes < 36 || size < header_bytes) {
            return;
        }
        last_packet_ms_.store(steady_millis());
        if (is_video_connection) {
            last_remote_ = remote;
        } else {
            last_audio_remote_ = remote;
        }
        auto &connection_ack_tracker = is_audio_connection ? audio_ack_tracker_ : ack_tracker_;
        auto &connection_fragments = is_audio_connection ? audio_packet_fragments_ : packet_fragments_;
        const size_t payload_offset = static_cast<size_t>(header_bytes);
        if (payload_offset + payload_len > size || 36 + channel_len > header_bytes) {
            return;
        }

        if (packet_type == 1) {
            const bool was_connected = media_connected_;
            video_assemblies_.clear();
            video_parity_cache_.clear();
            reset_audio_reorder();
            packet_fragments_.reset();
            audio_packet_fragments_.reset();
            connection_ack_tracker.reset();
            connection_ack_tracker.remember(sequence);
            packets_forwarded_ = 0;
            bytes_forwarded_ = 0;
            payloads_decoded_dropped_ = 0;
            video_assemblies_expired_ = 0;
            video_recovery_dropped_ = 0;
            video_parity_received_ = 0;
            video_parity_cached_ = 0;
            video_parity_ignored_ = 0;
            video_parity_recovered_ = 0;
            video_parity_unusable_ = 0;
            waiting_for_video_keyframe_ = true;
            video_forward_failures_ = 0;
            audio_forward_failures_ = 0;
            audio_packets_reordered_ = 0;
            audio_packets_skipped_ = 0;
            audio_packets_stale_ = 0;
            media_sequence_initialized_ = false;
            last_media_sequence_ = 0;
            media_sequence_gap_events_ = 0;
            media_sequence_gap_packets_ = 0;
            last_sequence_gap_log_ms_ = 0;
            last_keyframe_request_ms_ = 0;
            last_video_expiry_scan_ms_ = 0;
            media_connected_ = true;
            if (was_connected) {
                decoder_reset_requested_.store(true);
            }
            const auto [ack, ack_mask] = connection_ack_tracker.state();
            send_control_packet(2, 0x03, connection_id, bridge_sequence++, ack, ack_mask, remote);
            blog(LOG_INFO,
                 "Muninn RUDP bridge accepted %s media connection sequence %u",
                 is_audio_connection ? "audio" : "video",
                 sequence);
            return;
        }

        if (packet_type == 3) {
            if (!media_connected_) {
                video_assemblies_.clear();
                video_parity_cache_.clear();
                reset_audio_reorder();
                packet_fragments_.reset();
                audio_packet_fragments_.reset();
                ack_tracker_.reset();
                audio_ack_tracker_.reset();
                packets_forwarded_ = 0;
                bytes_forwarded_ = 0;
                payloads_decoded_dropped_ = 0;
                video_assemblies_expired_ = 0;
                video_recovery_dropped_ = 0;
                video_parity_received_ = 0;
                video_parity_cached_ = 0;
                video_parity_ignored_ = 0;
                video_parity_recovered_ = 0;
                video_parity_unusable_ = 0;
                waiting_for_video_keyframe_ = true;
                video_forward_failures_ = 0;
                audio_forward_failures_ = 0;
                audio_packets_reordered_ = 0;
                audio_packets_skipped_ = 0;
                audio_packets_stale_ = 0;
                media_sequence_initialized_ = false;
                last_media_sequence_ = 0;
                media_sequence_gap_events_ = 0;
                media_sequence_gap_packets_ = 0;
                last_sequence_gap_log_ms_ = 0;
                last_keyframe_request_ms_ = 0;
                last_video_expiry_scan_ms_ = 0;
                media_connected_ = true;
                decoder_reset_requested_.store(true);
                blog(LOG_INFO, "Muninn RUDP bridge adopted existing media sender at sequence %u", sequence);
            }
            connection_ack_tracker.remember(sequence);
            if (is_video_connection) {
                observe_media_sequence(sequence);
            }
            const std::string channel_id(
                reinterpret_cast<const char *>(packet + 36),
                reinterpret_cast<const char *>(packet + 36 + channel_len));
            if (channel_id != "media") {
                const auto [ack, ack_mask] = connection_ack_tracker.state();
                send_control_packet(4, 0x00, connection_id, bridge_sequence++, ack, ack_mask, remote);
                return;
            }
            std::optional<std::pair<uint32_t, std::vector<uint8_t>>> delivered;
            if (fragment_count == 0) {
                delivered = std::make_pair(
                    sequence,
                    std::vector<uint8_t>(packet + payload_offset, packet + payload_offset + payload_len));
            } else {
                delivered = assemble_rudp_packet_fragment(
                    connection_fragments,
                    channel_id,
                    read_be16(packet + 24),
                    read_be16(packet + 26),
                    fragment_count,
                    sequence,
                    packet + payload_offset,
                    payload_len);
            }
            if (delivered) {
                forward_payload(delivered->second.data(),
                                static_cast<uint32_t>(delivered->second.size()),
                                video_socket,
                                video_addr,
                                audio_socket,
                                audio_addr,
                                bridge_sequence);
            }
            const auto [ack, ack_mask] = connection_ack_tracker.state();
            send_control_packet(4, 0x00, connection_id, bridge_sequence++, ack, ack_mask, remote);
        }
    }

    std::optional<std::pair<uint32_t, std::vector<uint8_t>>> assemble_rudp_packet_fragment(
        muninn_rudp_fragments::FragmentAssembler &packet_fragments,
        const std::string &channel_id,
        uint16_t fragment_id,
        uint16_t fragment_index,
        uint16_t fragment_count,
        uint32_t sequence,
        const uint8_t *payload,
        uint32_t payload_len)
    {
        expire_rudp_packet_fragments(std::chrono::steady_clock::now());
        if (fragment_id == 0 || fragment_index >= fragment_count) {
            ++payloads_decoded_dropped_;
            log_bridge_pressure("dropped invalid RUDP packet fragment metadata");
            return std::nullopt;
        }

        const auto result = packet_fragments.insert(
            channel_id,
            fragment_id,
            fragment_index,
            fragment_count,
            sequence,
            payload,
            payload_len,
            std::chrono::steady_clock::now());
        if (!result) {
            return std::nullopt;
        }
        return std::make_pair(result->first_sequence, result->payload);
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
        if (media->kind == MuninnMediaPayload::Kind::Video || media->kind == MuninnMediaPayload::Kind::VideoParity) {
            const auto assembled = assemble_video_payload(*media, bridge_sequence);
            if (!assembled) {
                return;
            }
            if (waiting_for_video_keyframe_ && !h264_access_unit_has_idr(*assembled)) {
                ++video_recovery_dropped_;
                log_bridge_pressure("dropped video payload while waiting for decoder recovery keyframe");
                return;
            }
            if (h264_access_unit_has_idr(*assembled)) {
                waiting_for_video_keyframe_ = false;
            }
            forwarded_bytes = assembled->size();
            if (!send_udp_byte_stream(video_socket, video_addr, assembled->data(), assembled->size())) {
                ++video_forward_failures_;
                log_bridge_pressure("failed forwarding video payload to local decoder socket");
                return;
            }
            last_video_forwarded_ms_.store(steady_millis());
        } else if (media->kind == MuninnMediaPayload::Kind::Audio) {
            const auto audio_forwarded_bytes = forward_audio_payload(*media, audio_socket, audio_addr);
            if (!audio_forwarded_bytes) {
                return;
            }
            forwarded_bytes = *audio_forwarded_bytes;
        } else {
            ++payloads_decoded_dropped_;
            log_bridge_pressure("dropped unknown media payload");
            return;
        }
        ++packets_forwarded_;
        bytes_forwarded_ += forwarded_bytes;
        if (packets_forwarded_ == 1 || packets_forwarded_ % 900 == 0) {
            blog(LOG_INFO,
                 "Muninn RUDP bridge forwarded=%llu bytes=%llu decode_dropped=%llu assemblies_expired=%llu parity_received=%llu parity_cached=%llu parity_recovered=%llu parity_ignored=%llu parity_unusable=%llu video_failures=%llu audio_failures=%llu audio_reordered=%llu audio_skipped=%llu audio_stale=%llu audio_pending=%zu",
                 static_cast<unsigned long long>(packets_forwarded_),
                 static_cast<unsigned long long>(bytes_forwarded_),
                 static_cast<unsigned long long>(payloads_decoded_dropped_),
                 static_cast<unsigned long long>(video_assemblies_expired_),
                 static_cast<unsigned long long>(video_parity_received_),
                 static_cast<unsigned long long>(video_parity_cached_),
                 static_cast<unsigned long long>(video_parity_recovered_),
                 static_cast<unsigned long long>(video_parity_ignored_),
                 static_cast<unsigned long long>(video_parity_unusable_),
                 static_cast<unsigned long long>(video_forward_failures_),
                 static_cast<unsigned long long>(audio_forward_failures_),
                 static_cast<unsigned long long>(audio_packets_reordered_),
                 static_cast<unsigned long long>(audio_packets_skipped_),
                 static_cast<unsigned long long>(audio_packets_stale_),
                 pending_audio_packets_.size());
        }
    }

    std::optional<size_t> forward_audio_payload(const MuninnMediaPayload &media,
                                                SOCKET audio_socket,
                                                const sockaddr_in &audio_addr)
    {
        if (media.stream_id.empty() || media.session_id.empty() || media.payload.empty()) {
            ++payloads_decoded_dropped_;
            log_bridge_pressure("dropped invalid audio packet metadata");
            return std::nullopt;
        }

        const auto now = std::chrono::steady_clock::now();
        if (audio_stream_id_ != media.stream_id || audio_session_id_ != media.session_id) {
            reset_audio_reorder();
            audio_stream_id_ = media.stream_id;
            audio_session_id_ = media.session_id;
        }

        if (!next_audio_packet_ready_) {
            next_audio_packet_ready_ = true;
            next_audio_packet_id_ = media.audio_packet_id;
        }

        if (media.audio_packet_id < next_audio_packet_id_) {
            ++audio_packets_stale_;
            return std::nullopt;
        }

        auto inserted = pending_audio_packets_.emplace(
            media.audio_packet_id,
            MuninnPendingAudioPacket{media.payload, now});
        if (!inserted.second) {
            return std::nullopt;
        }
        if (media.audio_packet_id != next_audio_packet_id_) {
            ++audio_packets_reordered_;
        }

        return flush_audio_packets(now, audio_socket, audio_addr);
    }

    std::optional<size_t> flush_audio_packets(std::chrono::steady_clock::time_point now,
                                              SOCKET audio_socket,
                                              const sockaddr_in &audio_addr)
    {
        size_t forwarded_bytes = 0;
        bool forwarded_any = false;
        while (true) {
            auto ready = pending_audio_packets_.find(next_audio_packet_id_);
            if (ready == pending_audio_packets_.end()) {
                break;
            }
            if (!send_audio_packet(audio_socket, audio_addr, ready->second.payload)) {
                return std::nullopt;
            }
            forwarded_bytes += ready->second.payload.size();
            forwarded_any = true;
            pending_audio_packets_.erase(ready);
            ++next_audio_packet_id_;
            audio_gap_started_at_ = std::chrono::steady_clock::time_point::min();
        }

        if (pending_audio_packets_.empty()) {
            audio_gap_started_at_ = std::chrono::steady_clock::time_point::min();
            return forwarded_any ? std::optional<size_t>(forwarded_bytes) : std::nullopt;
        }

        if (audio_gap_started_at_ == std::chrono::steady_clock::time_point::min()) {
            audio_gap_started_at_ = now;
        }

        const bool gap_expired =
            now - audio_gap_started_at_ >= std::chrono::milliseconds(parts_.audio_reorder_ms);
        const bool queue_overflow = pending_audio_packets_.size() > MuninnAudioReorderMaxPackets;
        if (gap_expired || queue_overflow) {
            const uint64_t first_available = pending_audio_packets_.begin()->first;
            if (first_available > next_audio_packet_id_) {
                audio_packets_skipped_ += first_available - next_audio_packet_id_;
                next_audio_packet_id_ = first_available;
            }
            audio_gap_started_at_ = std::chrono::steady_clock::time_point::min();
            const auto after_skip = flush_audio_packets(now, audio_socket, audio_addr);
            if (after_skip) {
                forwarded_bytes += *after_skip;
                forwarded_any = true;
            }
        }

        return forwarded_any ? std::optional<size_t>(forwarded_bytes) : std::nullopt;
    }

    bool send_audio_packet(SOCKET audio_socket, const sockaddr_in &audio_addr, const std::vector<uint8_t> &payload)
    {
        const int sent = sendto(audio_socket,
                                reinterpret_cast<const char *>(payload.data()),
                                static_cast<int>(payload.size()),
                                0,
                                reinterpret_cast<const sockaddr *>(&audio_addr),
                                sizeof(audio_addr));
        if (sent != static_cast<int>(payload.size())) {
            ++audio_forward_failures_;
            log_bridge_pressure("failed forwarding audio payload to local decoder socket");
            return false;
        }
        last_audio_forwarded_ms_.store(steady_millis());
        return true;
    }

    void reset_audio_reorder()
    {
        audio_stream_id_.clear();
        audio_session_id_.clear();
        pending_audio_packets_.clear();
        next_audio_packet_ready_ = false;
        next_audio_packet_id_ = 0;
        audio_gap_started_at_ = std::chrono::steady_clock::time_point::min();
    }

    void observe_media_sequence(uint32_t sequence)
    {
        if (!media_sequence_initialized_) {
            media_sequence_initialized_ = true;
            last_media_sequence_ = sequence;
            return;
        }
        if (sequence <= last_media_sequence_) {
            return;
        }
        if (sequence == last_media_sequence_ + 1) {
            last_media_sequence_ = sequence;
            return;
        }
        const uint32_t missing = sequence - last_media_sequence_ - 1;
        media_sequence_gap_events_ += 1;
        media_sequence_gap_packets_ += missing;
        last_media_sequence_ = sequence;
        const uint64_t now = steady_millis();
        if (media_sequence_gap_events_ == 1 ||
            now > last_sequence_gap_log_ms_ + 5'000 ||
            media_sequence_gap_events_ % 64 == 0) {
            last_sequence_gap_log_ms_ = now;
            blog(LOG_WARNING,
                 "Muninn RUDP bridge observed media sequence gap missing=%u gap_events=%llu gap_packets=%llu forwarded=%llu assemblies_expired=%llu",
                 missing,
                 static_cast<unsigned long long>(media_sequence_gap_events_),
                 static_cast<unsigned long long>(media_sequence_gap_packets_),
                 static_cast<unsigned long long>(packets_forwarded_),
                 static_cast<unsigned long long>(video_assemblies_expired_));
        }
    }

    bool send_udp_byte_stream(SOCKET socket,
                              const sockaddr_in &addr,
                              const uint8_t *data,
                              size_t size) const
    {
        constexpr size_t MaxLocalUdpPayload = 16 * 1024;
        size_t offset = 0;
        while (offset < size) {
            const size_t chunk_size = std::min(MaxLocalUdpPayload, size - offset);
            const int sent = sendto(socket,
                                    reinterpret_cast<const char *>(data + offset),
                                    static_cast<int>(chunk_size),
                                    0,
                                    reinterpret_cast<const sockaddr *>(&addr),
                                    sizeof(addr));
            if (sent != static_cast<int>(chunk_size)) {
                return false;
            }
            offset += chunk_size;
        }
        return true;
    }

    std::optional<std::vector<uint8_t>> assemble_video_payload(const MuninnMediaPayload &media, uint32_t &bridge_sequence)
    {
        expire_video_assemblies(std::chrono::steady_clock::now(), bridge_sequence);
        expire_video_parity_cache(std::chrono::steady_clock::now());
        if (media.kind == MuninnMediaPayload::Kind::VideoParity) {
            return assemble_video_parity_payload(media, bridge_sequence);
        }
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
        if (expired_video_frame_keys_.find(key) != expired_video_frame_keys_.end()) {
            return std::nullopt;
        }
        auto &assembly = video_assemblies_[key];
        if (assembly.chunks.empty()) {
            assembly.stream_id = media.stream_id;
            assembly.session_id = media.session_id;
            assembly.frame_id = media.frame_id;
            assembly.keyframe = media.keyframe;
            assembly.dependency_frame_id = media.dependency_frame_id;
            assembly.chunk_count = media.chunk_count;
            assembly.chunks.resize(media.chunk_count);
            assembly.started_at = std::chrono::steady_clock::now();
            assembly.repair_requested = false;
            apply_cached_video_parity(key, assembly);
        }
        if (assembly.chunk_count != media.chunk_count ||
            assembly.keyframe != media.keyframe ||
            assembly.dependency_frame_id != media.dependency_frame_id) {
            video_assemblies_.erase(key);
            ++payloads_decoded_dropped_;
            log_bridge_pressure("dropped video frame with mixed metadata");
            return std::nullopt;
        }
        auto &slot = assembly.chunks[media.chunk_index];
        if (!slot.empty()) {
            return std::nullopt;
        }
        slot = media.payload;
        ++assembly.chunks_received;
        if (assembly.chunks_received != assembly.chunk_count) {
            if (try_recover_video_assembly_from_parity(assembly)) {
                ++video_parity_recovered_;
                return finish_video_assembly(key, assembly);
            }
            request_video_chunk_repair_if_due(assembly, std::chrono::steady_clock::now(), bridge_sequence);
            return std::nullopt;
        }

        return finish_video_assembly(key, assembly);
    }

    std::optional<std::vector<uint8_t>> assemble_video_parity_payload(const MuninnMediaPayload &media, uint32_t &bridge_sequence)
    {
        ++video_parity_received_;
        if (media.chunk_count == 0 ||
            media.parity_count == 0 ||
            media.parity_index >= media.parity_count ||
            media.parity_count > media.chunk_count ||
            media.chunk_payload_lengths.size() != media.chunk_count ||
            media.payload.empty()) {
            ++payloads_decoded_dropped_;
            ++video_parity_unusable_;
            log_bridge_pressure("dropped invalid video parity metadata");
            return std::nullopt;
        }
        const std::string key =
            media.stream_id + ":" + media.session_id + ":" + std::to_string(media.frame_id);
        if (expired_video_frame_keys_.find(key) != expired_video_frame_keys_.end()) {
            ++video_parity_ignored_;
            return std::nullopt;
        }
        auto iterator = video_assemblies_.find(key);
        if (iterator == video_assemblies_.end()) {
            cache_video_parity_payload(key, media);
            return std::nullopt;
        }
        auto &assembly = iterator->second;
        if (assembly.chunk_count != media.chunk_count ||
            assembly.keyframe != media.keyframe ||
            assembly.dependency_frame_id != media.dependency_frame_id) {
            video_assemblies_.erase(key);
            ++payloads_decoded_dropped_;
            ++video_parity_unusable_;
            log_bridge_pressure("dropped video parity with mixed metadata");
            return std::nullopt;
        }
        assembly.chunk_payload_lengths = media.chunk_payload_lengths;
        if (assembly.parity_count != 0 && assembly.parity_count != media.parity_count) {
            video_assemblies_.erase(key);
            ++payloads_decoded_dropped_;
            ++video_parity_unusable_;
            log_bridge_pressure("dropped video parity with mixed stripe count");
            return std::nullopt;
        }
        assembly.parity_count = media.parity_count;
        assembly.parity_payloads[media.parity_index] = media.payload;
        if (try_recover_video_assembly_from_parity(assembly)) {
            ++video_parity_recovered_;
            return finish_video_assembly(key, assembly);
        }
        request_video_chunk_repair_if_due(assembly, std::chrono::steady_clock::now(), bridge_sequence);
        return std::nullopt;
    }

    void cache_video_parity_payload(const std::string &key, const MuninnMediaPayload &media)
    {
        MuninnVideoParityCacheEntry entry;
        entry.stream_id = media.stream_id;
        entry.session_id = media.session_id;
        entry.frame_id = media.frame_id;
        entry.keyframe = media.keyframe;
        entry.dependency_frame_id = media.dependency_frame_id;
        entry.chunk_count = media.chunk_count;
        entry.parity_index = media.parity_index;
        entry.parity_count = media.parity_count;
        entry.received_at = std::chrono::steady_clock::now();
        entry.chunk_payload_lengths = media.chunk_payload_lengths;
        entry.payload = media.payload;
        video_parity_cache_[key].push_back(std::move(entry));
        ++video_parity_cached_;
        trim_video_parity_cache();
    }

    void apply_cached_video_parity(const std::string &key, MuninnVideoFrameAssembly &assembly)
    {
        auto iterator = video_parity_cache_.find(key);
        if (iterator == video_parity_cache_.end()) {
            return;
        }
        for (const auto &entry : iterator->second) {
            if (entry.chunk_count == assembly.chunk_count &&
                entry.keyframe == assembly.keyframe &&
                entry.dependency_frame_id == assembly.dependency_frame_id &&
                entry.parity_count != 0 &&
                entry.parity_index < entry.parity_count &&
                (assembly.parity_count == 0 || assembly.parity_count == entry.parity_count)) {
                assembly.chunk_payload_lengths = entry.chunk_payload_lengths;
                assembly.parity_count = entry.parity_count;
                assembly.parity_payloads[entry.parity_index] = entry.payload;
            } else {
                ++video_parity_unusable_;
            }
        }
        video_parity_cache_.erase(iterator);
    }

    std::optional<std::vector<uint8_t>> finish_video_assembly(const std::string &key, const MuninnVideoFrameAssembly &assembly)
    {
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
        video_parity_cache_.erase(key);
        return assembled;
    }

    bool try_recover_video_assembly_from_parity(MuninnVideoFrameAssembly &assembly)
    {
        if (assembly.parity_count == 0 ||
            assembly.parity_payloads.empty() ||
            assembly.chunk_payload_lengths.size() != assembly.chunk_count ||
            assembly.chunks.size() != assembly.chunk_count) {
            return false;
        }
        bool recovered_any = true;
        while (recovered_any && assembly.chunks_received < assembly.chunk_count) {
            recovered_any = false;
            for (const auto &parity : assembly.parity_payloads) {
                const uint16_t parity_index = parity.first;
                if (parity_index >= assembly.parity_count || parity.second.empty()) {
                    continue;
                }
                uint16_t missing_index = 0;
                uint16_t missing_count = 0;
                for (uint16_t index = parity_index; index < assembly.chunk_count; index += assembly.parity_count) {
                    if (assembly.chunks[index].empty()) {
                        missing_index = index;
                        ++missing_count;
                    }
                }
                if (missing_count != 1) {
                    continue;
                }
                const uint32_t missing_len = assembly.chunk_payload_lengths[missing_index];
                if (missing_len == 0 || missing_len > parity.second.size()) {
                    continue;
                }
                std::vector<uint8_t> recovered = parity.second;
                bool stripe_complete = true;
                for (uint16_t index = parity_index; index < assembly.chunk_count; index += assembly.parity_count) {
                    if (index == missing_index) {
                        continue;
                    }
                    const auto &chunk = assembly.chunks[index];
                    if (chunk.empty()) {
                        stripe_complete = false;
                        break;
                    }
                    for (size_t offset = 0; offset < chunk.size() && offset < recovered.size(); ++offset) {
                        recovered[offset] ^= chunk[offset];
                    }
                }
                if (!stripe_complete) {
                    continue;
                }
                recovered.resize(missing_len);
                assembly.chunks[missing_index] = std::move(recovered);
                ++assembly.chunks_received;
                recovered_any = true;
            }
        }
        return assembly.chunks_received == assembly.chunk_count;
    }

    void expire_video_parity_cache(std::chrono::steady_clock::time_point now)
    {
        for (auto iterator = video_parity_cache_.begin(); iterator != video_parity_cache_.end();) {
            const auto newest = std::max_element(
                iterator->second.begin(),
                iterator->second.end(),
                [](const MuninnVideoParityCacheEntry &left, const MuninnVideoParityCacheEntry &right) {
                    return left.received_at < right.received_at;
                });
            if (newest != iterator->second.end() &&
                now - newest->received_at < std::chrono::milliseconds(parts_.video_assembly_deadline_ms)) {
                ++iterator;
                continue;
            }
            ++video_parity_ignored_;
            iterator = video_parity_cache_.erase(iterator);
        }
    }

    void trim_video_parity_cache()
    {
        while (video_parity_cache_.size() > MuninnVideoParityCacheMaxEntries) {
            auto oldest = std::min_element(
                video_parity_cache_.begin(),
                video_parity_cache_.end(),
                [](const auto &left, const auto &right) {
                    const auto left_oldest = std::min_element(
                        left.second.begin(),
                        left.second.end(),
                        [](const MuninnVideoParityCacheEntry &a, const MuninnVideoParityCacheEntry &b) {
                            return a.received_at < b.received_at;
                        });
                    const auto right_oldest = std::min_element(
                        right.second.begin(),
                        right.second.end(),
                        [](const MuninnVideoParityCacheEntry &a, const MuninnVideoParityCacheEntry &b) {
                            return a.received_at < b.received_at;
                        });
                    if (left_oldest == left.second.end()) {
                        return true;
                    }
                    if (right_oldest == right.second.end()) {
                        return false;
                    }
                    return left_oldest->received_at < right_oldest->received_at;
                });
            if (oldest == video_parity_cache_.end()) {
                return;
            }
            ++video_parity_ignored_;
            video_parity_cache_.erase(oldest);
        }
    }

    void expire_video_assemblies(std::chrono::steady_clock::time_point now, uint32_t &bridge_sequence)
    {
        const uint64_t now_ms = steady_millis();
        if (last_video_expiry_scan_ms_ != 0 &&
            now_ms > last_video_expiry_scan_ms_ &&
            now_ms - last_video_expiry_scan_ms_ < MuninnVideoAssemblyExpiryScanMs) {
            return;
        }
        last_video_expiry_scan_ms_ = now_ms;
        for (auto iterator = video_assemblies_.begin(); iterator != video_assemblies_.end();) {
            if (now - iterator->second.started_at < std::chrono::milliseconds(parts_.video_assembly_deadline_ms)) {
                request_video_chunk_repair_if_due(iterator->second, now, bridge_sequence);
                ++iterator;
                continue;
            }
            const auto missing_chunk_keys = missing_video_chunk_keys(iterator->second);
            const bool needs_decoder_recovery =
                iterator->second.keyframe || iterator->second.dependency_frame_id.has_value();
            send_receiver_feedback_if_due(
                iterator->second,
                missing_chunk_keys,
                needs_decoder_recovery,
                bridge_sequence);
            if (needs_decoder_recovery) {
                waiting_for_video_keyframe_ = true;
            }
            log_video_assembly_expiry(iterator->second, missing_chunk_keys, now);
            remember_expired_video_frame(iterator->first);
            iterator = video_assemblies_.erase(iterator);
            ++video_assemblies_expired_;
            log_bridge_pressure("expired incomplete video frame assembly");
        }
    }

    void remember_expired_video_frame(const std::string &key)
    {
        if (expired_video_frame_keys_.insert(key).second) {
            expired_video_frame_order_.push_back(key);
        }
        while (expired_video_frame_order_.size() > MuninnExpiredVideoFrameRememberCount) {
            expired_video_frame_keys_.erase(expired_video_frame_order_.front());
            expired_video_frame_order_.pop_front();
        }
    }

    void log_video_assembly_expiry(const MuninnVideoFrameAssembly &assembly,
                                   const std::vector<std::string> &missing_chunk_keys,
                                   std::chrono::steady_clock::time_point now) const
    {
        const auto age_ms = std::chrono::duration_cast<std::chrono::milliseconds>(now - assembly.started_at).count();
        blog(LOG_WARNING,
             "Muninn RUDP bridge expired video frame id=%llu keyframe=%s dependency=%s chunks=%u/%u missing=%zu age_ms=%lld repair_requested=%s parity_present=%s",
             static_cast<unsigned long long>(assembly.frame_id),
             assembly.keyframe ? "true" : "false",
             assembly.dependency_frame_id.has_value() ? "true" : "false",
             static_cast<unsigned int>(assembly.chunks_received),
             static_cast<unsigned int>(assembly.chunk_count),
             missing_chunk_keys.size(),
             static_cast<long long>(age_ms),
             assembly.repair_requested ? "true" : "false",
             assembly.parity_payloads.empty() ? "false" : "true");
    }

    void request_video_chunk_repair_if_due(MuninnVideoFrameAssembly &assembly,
                                           std::chrono::steady_clock::time_point now,
                                           uint32_t &bridge_sequence)
    {
        if (assembly.chunks_received >= assembly.chunk_count) {
            return;
        }
        if (assembly.repair_requested) {
            return;
        }
        const auto repair_wait_ms = std::max<uint32_t>(parts_.gap_wait_ms, MuninnReceiverChunkRepairFirstWaitMs);
        if (now - assembly.started_at < std::chrono::milliseconds(repair_wait_ms)) {
            return;
        }
        const auto missing_chunk_keys = missing_video_chunk_keys(assembly);
        if (missing_chunk_keys.empty()) {
            return;
        }
        send_receiver_feedback_if_due(assembly, missing_chunk_keys, false, bridge_sequence);
        assembly.repair_requested = true;
    }

    void expire_rudp_packet_fragments(std::chrono::steady_clock::time_point now)
    {
        const auto expired = packet_fragments_.expire(now, parts_.reliable_expire_after_ms);
        const auto audio_expired = audio_packet_fragments_.expire(now, parts_.reliable_expire_after_ms);
        for (uint64_t index = 0; index < expired + audio_expired; ++index) {
            log_bridge_pressure("expired incomplete RUDP packet fragment assembly");
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

    void send_receiver_feedback_if_due(const MuninnVideoFrameAssembly &assembly,
                                       const std::vector<std::string> &missing_chunk_keys,
                                       bool requested_keyframe,
                                       uint32_t &bridge_sequence)
    {
        if (!last_remote_ || assembly.stream_id.empty() || assembly.session_id.empty()) {
            return;
        }
        if (requested_keyframe) {
            const uint64_t now = steady_millis();
            const uint64_t last = last_keyframe_request_ms_.load();
            if (last != 0 && now >= last && now - last < MuninnReceiverKeyframeRequestCooldownMs) {
                return;
            }
            last_keyframe_request_ms_.store(now);
        }
        const std::string observed_at = "unix:" + std::to_string(steady_millis());
        const auto payload = muninn_media_wire::encode_receiver_feedback_payload(
            assembly.stream_id, assembly.session_id, assembly.frame_id, missing_chunk_keys, requested_keyframe, observed_at);
        send_media_packet(payload, bridge_sequence++, *last_remote_);
    }

    void log_bridge_pressure(const char *reason)
    {
        const uint64_t pressure =
            payloads_decoded_dropped_ + video_assemblies_expired_ + packet_fragments_.expired_count() +
            audio_packet_fragments_.expired_count() +
            video_recovery_dropped_ + video_parity_unusable_ + video_forward_failures_ + audio_forward_failures_;
        if (pressure == 1 || pressure % 300 == 0) {
            blog(LOG_WARNING,
                 "Muninn RUDP bridge %s: forwarded=%llu bytes=%llu decode_dropped=%llu assemblies_expired=%llu packet_fragments_expired=%llu recovery_dropped=%llu parity_received=%llu parity_cached=%llu parity_recovered=%llu parity_ignored=%llu parity_unusable=%llu video_failures=%llu audio_failures=%llu audio_reordered=%llu audio_skipped=%llu audio_stale=%llu audio_pending=%zu",
                 reason,
                 static_cast<unsigned long long>(packets_forwarded_),
                 static_cast<unsigned long long>(bytes_forwarded_),
                 static_cast<unsigned long long>(payloads_decoded_dropped_),
                 static_cast<unsigned long long>(video_assemblies_expired_),
                 static_cast<unsigned long long>(
                     packet_fragments_.expired_count() + audio_packet_fragments_.expired_count()),
                 static_cast<unsigned long long>(video_recovery_dropped_),
                 static_cast<unsigned long long>(video_parity_received_),
                 static_cast<unsigned long long>(video_parity_cached_),
                 static_cast<unsigned long long>(video_parity_recovered_),
                 static_cast<unsigned long long>(video_parity_ignored_),
                 static_cast<unsigned long long>(video_parity_unusable_),
                 static_cast<unsigned long long>(video_forward_failures_),
                 static_cast<unsigned long long>(audio_forward_failures_),
                 static_cast<unsigned long long>(audio_packets_reordered_),
                 static_cast<unsigned long long>(audio_packets_skipped_),
                 static_cast<unsigned long long>(audio_packets_stale_),
                 pending_audio_packets_.size());
        }
    }

    static bool h264_access_unit_has_idr(const std::vector<uint8_t> &access_unit)
    {
        for (size_t index = 0; index + 4 < access_unit.size();) {
            size_t start_code = std::string::npos;
            size_t nal_offset = 0;
            for (size_t cursor = index; cursor + 3 < access_unit.size(); ++cursor) {
                if (access_unit[cursor] == 0x00 && access_unit[cursor + 1] == 0x00) {
                    if (access_unit[cursor + 2] == 0x01) {
                        start_code = cursor;
                        nal_offset = cursor + 3;
                        break;
                    }
                    if (cursor + 4 < access_unit.size() && access_unit[cursor + 2] == 0x00 && access_unit[cursor + 3] == 0x01) {
                        start_code = cursor;
                        nal_offset = cursor + 4;
                        break;
                    }
                }
            }
            if (start_code == std::string::npos || nal_offset >= access_unit.size()) {
                return false;
            }
            const uint8_t nal_type = access_unit[nal_offset] & 0x1f;
            if (nal_type == 5) {
                return true;
            }
            index = nal_offset + 1;
        }
        return false;
    }

    void send_control_packet(uint8_t packet_type,
                             uint8_t flags,
                             uint32_t connection_id,
                             uint32_t sequence,
                             uint32_t ack,
                             uint32_t ack_mask,
                             const sockaddr_in &remote)
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
        write_be32(response, 8, connection_id);
        write_be32(response, 12, sequence);
        write_be32(response, 16, ack);
        write_be32(response, 20, ack_mask);
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
        const auto [ack, ack_mask] = ack_tracker_.state();
        write_be32(response, 16, ack);
        write_be32(response, 20, ack_mask);
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
    std::optional<sockaddr_in> last_audio_remote_;
#endif
    std::atomic<bool> running_{false};
    std::atomic<uint64_t> started_ms_{0};
    std::atomic<uint64_t> last_packet_ms_{0};
    std::atomic<uint64_t> last_video_forwarded_ms_{0};
    std::atomic<uint64_t> last_audio_forwarded_ms_{0};
    std::atomic<bool> stale_reported_{false};
    std::atomic<bool> decoder_reset_requested_{false};
    std::thread worker_;
    muninn_rudp_url::RudpUrlParts parts_;
    std::map<std::string, MuninnVideoFrameAssembly> video_assemblies_;
    std::map<std::string, std::vector<MuninnVideoParityCacheEntry>> video_parity_cache_;
    std::deque<std::string> expired_video_frame_order_;
    std::set<std::string> expired_video_frame_keys_;
    muninn_rudp_fragments::FragmentAssembler packet_fragments_;
    muninn_rudp_fragments::FragmentAssembler audio_packet_fragments_;
    muninn_rudp_ack::AckTracker ack_tracker_;
    muninn_rudp_ack::AckTracker audio_ack_tracker_;
    uint64_t packets_forwarded_ = 0;
    uint64_t bytes_forwarded_ = 0;
    uint64_t payloads_decoded_dropped_ = 0;
    uint64_t video_assemblies_expired_ = 0;
    uint64_t video_recovery_dropped_ = 0;
    uint64_t video_parity_received_ = 0;
    uint64_t video_parity_cached_ = 0;
    uint64_t video_parity_ignored_ = 0;
    uint64_t video_parity_recovered_ = 0;
    uint64_t video_parity_unusable_ = 0;
    uint64_t video_forward_failures_ = 0;
    uint64_t audio_forward_failures_ = 0;
    uint64_t audio_packets_reordered_ = 0;
    uint64_t audio_packets_skipped_ = 0;
    uint64_t audio_packets_stale_ = 0;
    std::string audio_stream_id_;
    std::string audio_session_id_;
    bool next_audio_packet_ready_ = false;
    uint64_t next_audio_packet_id_ = 0;
    std::chrono::steady_clock::time_point audio_gap_started_at_ = std::chrono::steady_clock::time_point::min();
    std::map<uint64_t, MuninnPendingAudioPacket> pending_audio_packets_;
    bool media_sequence_initialized_ = false;
    uint32_t last_media_sequence_ = 0;
    uint64_t media_sequence_gap_events_ = 0;
    uint64_t media_sequence_gap_packets_ = 0;
    uint64_t last_sequence_gap_log_ms_ = 0;
    uint64_t last_video_expiry_scan_ms_ = 0;
    bool waiting_for_video_keyframe_ = true;
    bool media_connected_ = false;
    std::atomic<uint64_t> last_keyframe_request_ms_ = 0;
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

static bool is_playable_muninn_stream(const MuninnStreamOption &option)
{
    return !option.stream_id.empty() && option.state != "affordance";
}

static MuninnStreamOption default_muninn_stream_option()
{
    return MuninnStreamOption{
        "muninn.raven.av.rudp",
        "raven screen and loopback A/V",
        "",
        "activation-ready",
        MuninnCommandRudpTarget,
        MuninnDefaultMediaTargetHost,
        MuninnDefaultMediaPort,
        MuninnDefaultMediaPacketBytes,
        MuninnDefaultRudpVideoBitrateKbps,
        MuninnDefaultRudpLatencyBudgetMs,
        {{"display:0", "raven display 1"}},
        {{"wasapi-loopback:Realtek", "raven loopback (Realtek)"}},
    };
}

static void queue_planar_audio(MuninnSource *ctx,
                               const float *const *planes,
                               uint32_t frames,
                               enum speaker_layout speakers,
                               uint32_t samples_per_sec)
{
    if (!ctx || !ctx->source || !planes || frames == 0) {
        return;
    }

    MuninnQueuedAudio packet;
    packet.frames = frames;
    packet.timestamp = os_gettime_ns();
    packet.speakers = speakers;
    packet.samples_per_sec = samples_per_sec;
    const size_t bytes_per_plane = static_cast<size_t>(frames) * sizeof(float);
    for (size_t plane = 0; plane < MAX_AV_PLANES; ++plane) {
        if (!planes[plane]) {
            continue;
        }
        packet.planes[plane].resize(bytes_per_plane);
        std::memcpy(packet.planes[plane].data(), planes[plane], bytes_per_plane);
    }

    {
        std::lock_guard<std::mutex> lock(ctx->audio_mutex);
        if (!ctx->audio_worker_running) {
            return;
        }
        while (ctx->audio_queue.size() >= MuninnAudioQueueMaxPackets) {
            ctx->audio_queue.pop_front();
        }
        ctx->audio_queue.push_back(std::move(packet));
    }
    ctx->audio_cv.notify_one();
}

static void queue_stereo_interleaved_audio(MuninnSource *ctx, const float *samples, uint32_t frames)
{
    if (!samples || frames == 0) {
        return;
    }
    std::vector<float> left(frames);
    std::vector<float> right(frames);
    for (uint32_t frame = 0; frame < frames; ++frame) {
        left[frame] = samples[frame * 2];
        right[frame] = samples[frame * 2 + 1];
    }
    const float *planes[MAX_AV_PLANES] = {};
    planes[0] = left.data();
    planes[1] = right.data();
    queue_planar_audio(ctx, planes, frames, SPEAKERS_STEREO, 48000);
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

class FfmpegAudioDecoder {
public:
    ~FfmpegAudioDecoder()
    {
        stop();
    }

    void start(MuninnSource *ctx, const std::string &audio_url)
    {
        stop();
        if (!ctx || audio_url.empty()) {
            return;
        }

#ifdef _WIN32
        SECURITY_ATTRIBUTES pipe_attrs = {};
        pipe_attrs.nLength = sizeof(pipe_attrs);
        pipe_attrs.bInheritHandle = TRUE;

        HANDLE stdout_read = nullptr;
        HANDLE stdout_write = nullptr;
        if (!CreatePipe(&stdout_read, &stdout_write, &pipe_attrs, 0)) {
            blog(LOG_WARNING, "Muninn Stream could not create ffmpeg audio decoder pipe");
            return;
        }
        SetHandleInformation(stdout_read, HANDLE_FLAG_INHERIT, 0);

        HANDLE nul = CreateFileA("NUL", GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, &pipe_attrs, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (nul == INVALID_HANDLE_VALUE) {
            nul = nullptr;
        }

        std::vector<std::string> args{
            "ffmpeg.exe",
            "-hide_banner",
            "-loglevel",
            "warning",
            "-fflags",
            "nobuffer",
            "-flags",
            "low_delay",
            "-probesize",
            "32768",
            "-analyzeduration",
            "0",
            "-f",
            "aac",
            "-i",
            audio_url,
            "-vn",
            "-f",
            "f32le",
            "-ac",
            "2",
            "-ar",
            "48000",
            "pipe:1",
        };

        std::string command_line;
        for (const auto &arg : args) {
            if (!command_line.empty()) {
                command_line.push_back(' ');
            }
            command_line += quote_windows_argument(arg);
        }

        STARTUPINFOA startup = {};
        startup.cb = sizeof(startup);
        startup.dwFlags = STARTF_USESTDHANDLES;
        startup.hStdInput = GetStdHandle(STD_INPUT_HANDLE);
        startup.hStdOutput = stdout_write;
        startup.hStdError = nul ? nul : GetStdHandle(STD_ERROR_HANDLE);

        PROCESS_INFORMATION process = {};
        std::vector<char> mutable_command(command_line.begin(), command_line.end());
        mutable_command.push_back('\0');
        if (!CreateProcessA(nullptr, mutable_command.data(), nullptr, nullptr, TRUE, CREATE_NO_WINDOW, nullptr, nullptr, &startup, &process)) {
            blog(LOG_WARNING, "Muninn Stream could not start local ffmpeg audio decoder for %s", audio_url.c_str());
            CloseHandle(stdout_read);
            CloseHandle(stdout_write);
            if (nul) {
                CloseHandle(nul);
            }
            return;
        }
        CloseHandle(stdout_write);
        if (nul) {
            CloseHandle(nul);
        }

        ctx_ = ctx;
        process_ = process.hProcess;
        thread_ = process.hThread;
        stdout_read_ = stdout_read;
        running_.store(true);
        worker_ = std::thread([this] { read_loop(); });
        blog(LOG_INFO, "Muninn Stream local ffmpeg audio decoder started for %s", audio_url.c_str());
#else
        (void)ctx;
        (void)audio_url;
#endif
    }

    void stop()
    {
#ifdef _WIN32
        running_.store(false);
        if (process_) {
            TerminateProcess(process_, 0);
        }
        if (worker_.joinable()) {
            worker_.join();
        }
        if (stdout_read_) {
            CloseHandle(stdout_read_);
            stdout_read_ = nullptr;
        }
        if (thread_) {
            CloseHandle(thread_);
            thread_ = nullptr;
        }
        if (process_) {
            CloseHandle(process_);
            process_ = nullptr;
        }
        ctx_ = nullptr;
#endif
    }

private:
    void read_loop()
    {
#ifdef _WIN32
        constexpr size_t frames_per_chunk = 480;
        constexpr size_t bytes_per_frame = sizeof(float) * 2;
        std::vector<uint8_t> buffer(frames_per_chunk * bytes_per_frame);
        std::vector<uint8_t> pending;
        pending.reserve(buffer.size() * 2);
        while (running_.load()) {
            DWORD read = 0;
            if (!ReadFile(stdout_read_, buffer.data(), static_cast<DWORD>(buffer.size()), &read, nullptr) || read == 0) {
                break;
            }
            pending.insert(pending.end(), buffer.begin(), buffer.begin() + read);
            const size_t complete_bytes = (pending.size() / bytes_per_frame) * bytes_per_frame;
            if (complete_bytes == 0) {
                continue;
            }
            const auto *samples = reinterpret_cast<const float *>(pending.data());
            queue_stereo_interleaved_audio(ctx_, samples, static_cast<uint32_t>(complete_bytes / bytes_per_frame));
            pending.erase(pending.begin(), pending.begin() + static_cast<std::ptrdiff_t>(complete_bytes));
        }
#endif
    }

    std::atomic<bool> running_ = false;
    MuninnSource *ctx_ = nullptr;
    std::thread worker_;
#ifdef _WIN32
    HANDLE process_ = nullptr;
    HANDLE thread_ = nullptr;
    HANDLE stdout_read_ = nullptr;
#endif
};

static std::string command_observed_at()
{
    const auto now = std::chrono::system_clock::now().time_since_epoch();
    const auto seconds = std::chrono::duration_cast<std::chrono::seconds>(now).count();
    return "unix-" + std::to_string(seconds);
}

static bool split_ipv4_endpoint(const std::string &endpoint, std::string &host, uint16_t &port)
{
    const auto colon = endpoint.rfind(':');
    if (colon == std::string::npos || colon + 1 >= endpoint.size()) {
        return false;
    }
    host = endpoint.substr(0, colon);
    try {
        const auto parsed = std::stoul(endpoint.substr(colon + 1));
        if (parsed == 0 || parsed > 65535) {
            return false;
        }
        port = static_cast<uint16_t>(parsed);
        return true;
    } catch (...) {
        return false;
    }
}

static std::string replace_endpoint_host_if_unspecified(const std::string &endpoint, const std::string &host)
{
    std::string endpoint_host;
    uint16_t port = 0;
    if (!split_ipv4_endpoint(endpoint, endpoint_host, port)) {
        return endpoint;
    }
    if (endpoint_host == "0.0.0.0" || endpoint_host == "::" || endpoint_host.empty()) {
        return host + ":" + std::to_string(port);
    }
    return endpoint;
}

#ifdef _WIN32
static std::string sockaddr_ipv4_string(const sockaddr_in &addr)
{
    char text[INET_ADDRSTRLEN] = {};
    if (inet_ntop(AF_INET, &addr.sin_addr, text, sizeof(text))) {
        return text;
    }
    return {};
}
#endif

static std::vector<uint8_t> make_rudp_packet(uint8_t packet_type,
                                             uint8_t flags,
                                             uint32_t connection_id,
                                             uint32_t sequence,
                                             const std::string &channel,
                                             const std::vector<uint8_t> &payload)
{
    const uint8_t header_bytes = static_cast<uint8_t>(36 + channel.size());
    std::vector<uint8_t> packet(static_cast<size_t>(header_bytes) + payload.size());
    packet[0] = 'C';
    packet[1] = 'N';
    packet[2] = 'R';
    packet[3] = '0';
    packet[4] = 0;
    packet[5] = packet_type;
    packet[6] = flags;
    packet[7] = header_bytes;
    write_be32(packet, 8, connection_id);
    write_be32(packet, 12, sequence);
    write_be32(packet, 16, 0);
    write_be32(packet, 20, 0);
    write_be16(packet, 24, 0);
    write_be16(packet, 26, 0);
    write_be16(packet, 28, 0);
    write_be32(packet, 30, static_cast<uint32_t>(payload.size()));
    packet[34] = static_cast<uint8_t>(channel.size());
    packet[35] = 0;
    std::copy(channel.begin(), channel.end(), packet.begin() + 36);
    std::copy(payload.begin(), payload.end(), packet.begin() + header_bytes);
    return packet;
}

static std::vector<uint8_t> encode_capture_stream_command_wire(const MuninnSource *ctx, const std::string &stream_id, const std::string &observed_at)
{
    const std::string canonical_stream_id = canonical_muninn_stream_id(stream_id);
    const std::string command_id =
        ctx->host_id + ":" + canonical_stream_id + ":capture-stream:start:" + observed_at;

    muninn_media_wire::MsgpackWriter record;
    record.write_array_len(18);
    record.write_string(command_id);
    record.write_string(ctx->host_id);
    record.write_string(canonical_stream_id);
    record.write_string("pending");
    record.write_string("start");
    record.write_string(ctx->media_target_host);
    record.write_u64(ctx->media_port);
    record.write_nil();
    record.write_u64(MuninnDefaultMediaPort);
    record.write_string("rudp");
    record.write_u64(ctx->media_packet_bytes);
    record.write_string("mimir.obs");
    record.write_string("OBS requested a daemon-owned capture stream over CultNet RUDP");
    record.write_string(observed_at);
    record.write_u64(ctx->rudp_video_bitrate_kbps);
    record.write_u64(ctx->rudp_latency_budget_ms);
    record.write_string(ctx->video_source_id);
    record.write_string(ctx->audio_source_id);

    muninn_media_wire::MsgpackWriter document;
    document.write_map_len(9);
    document.write_string("schemaId");
    document.write_string("muninn.capture_stream_command");
    document.write_string("recordKey");
    document.write_string(command_id);
    document.write_string("storedAt");
    document.write_string(observed_at);
    document.write_string("payloadEncoding");
    document.write_string("messagepack");
    document.write_string("payload");
    document.write_binary(record.bytes());
    document.write_string("sourceRuntimeId");
    document.write_string("mimir-obs-plugin");
    document.write_string("sourceAgentId");
    document.write_nil();
    document.write_string("sourceRole");
    document.write_string("capture-command-publisher");
    document.write_string("tags");
    muninn_media_wire::write_string_array(document, {"cultnet.transport.rudp.v0"});

    muninn_media_wire::MsgpackWriter wire;
    wire.write_map_len(3);
    wire.write_string("schemaVersion");
    wire.write_string("cultnet.document_put_raw.v0");
    wire.write_string("messageId");
    wire.write_string("muninn-capture-command:" + command_id);
    wire.write_string("document");
    wire.write_raw(document.bytes());
    return wire.bytes();
}

static bool publish_capture_command_rudp(const std::string &target, const std::vector<uint8_t> &payload)
{
#ifdef _WIN32
    std::string host;
    uint16_t port = 0;
    if (!split_ipv4_endpoint(target, host, port)) {
        blog(LOG_WARNING, "Muninn Stream cannot parse command RUDP target %s", target.c_str());
        return false;
    }

    WSADATA wsa = {};
    if (WSAStartup(MAKEWORD(2, 2), &wsa) != 0) {
        return false;
    }
    SOCKET socket = ::socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
    if (socket == INVALID_SOCKET) {
        WSACleanup();
        return false;
    }
    const DWORD timeout_ms = 100;
    setsockopt(socket, SOL_SOCKET, SO_RCVTIMEO, reinterpret_cast<const char *>(&timeout_ms), sizeof(timeout_ms));

    sockaddr_in remote = {};
    remote.sin_family = AF_INET;
    remote.sin_port = htons(port);
    if (inet_pton(AF_INET, host.c_str(), &remote.sin_addr) != 1) {
        closesocket(socket);
        WSACleanup();
        blog(LOG_WARNING, "Muninn Stream command RUDP target is not IPv4: %s", target.c_str());
        return false;
    }

    const auto connect = make_rudp_packet(1, 0, MuninnCommandRudpConnectionId, 1, "control", {});
    const auto data = make_rudp_packet(3, 0, MuninnCommandRudpConnectionId, 2, "schema", payload);
    const auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds(2);
    std::array<uint8_t, 2048> buffer = {};
    while (std::chrono::steady_clock::now() < deadline) {
        sendto(socket, reinterpret_cast<const char *>(connect.data()), static_cast<int>(connect.size()), 0,
               reinterpret_cast<const sockaddr *>(&remote), sizeof(remote));
        sockaddr_in source = {};
        int source_len = sizeof(source);
        const int received = recvfrom(socket, reinterpret_cast<char *>(buffer.data()), static_cast<int>(buffer.size()), 0,
                                      reinterpret_cast<sockaddr *>(&source), &source_len);
        if (received >= 36 &&
            buffer[0] == 'C' && buffer[1] == 'N' && buffer[2] == 'R' && buffer[3] == '0' &&
            buffer[5] == 2 &&
            read_be32(buffer.data() + 8) == MuninnCommandRudpConnectionId) {
            const int sent = sendto(socket, reinterpret_cast<const char *>(data.data()), static_cast<int>(data.size()), 0,
                                    reinterpret_cast<const sockaddr *>(&remote), sizeof(remote));
            closesocket(socket);
            WSACleanup();
            return sent == static_cast<int>(data.size());
        }
    }

    closesocket(socket);
    WSACleanup();
    blog(LOG_WARNING, "Muninn Stream timed out connecting command RUDP target %s", target.c_str());
    return false;
#else
    (void)target;
    (void)payload;
    return false;
#endif
}

static std::string expected_rudp_stream_url(const MuninnSource *ctx, const std::string &stream_id)
{
    const auto reliable_expire_after_ms = std::max<uint32_t>(1, ctx->rudp_latency_budget_ms);
    return "rudp://" + ctx->media_target_host + ":" + std::to_string(ctx->media_port) + "/" + stream_id +
           "?channel=media&format=muninn-typed-media&connection=0x6d750001&audio_connection=0x6d750004&profile=muninn.rudp.low_latency_h264_lan.v1&sender_resend_delay_ms=5&reliable_expire_after_ms=" +
           std::to_string(reliable_expire_after_ms) + "&assembly_deadline_ms=" + std::to_string(ctx->rudp_latency_budget_ms) +
           "&gap_wait_ms=16&audio_reorder_ms=" + std::to_string(ctx->rudp_audio_reorder_ms);
}

static std::string playable_rudp_stream_url(const MuninnSource *ctx, const MuninnStreamOption &selected)
{
    if (starts_with(selected.url, "rudp://")) {
        return expected_rudp_stream_url(ctx, canonical_muninn_stream_id(selected.stream_id));
    }
    return selected.url.empty() ? expected_rudp_stream_url(ctx, selected.stream_id) : selected.url;
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

static std::string rudp_url_stream_id(const std::string &url)
{
    const auto authority_start = std::string("rudp://").size();
    const auto path_start = url.find('/', authority_start);
    if (path_start == std::string::npos || path_start + 1 >= url.size()) {
        return {};
    }
    const auto query_start = url.find('?', path_start + 1);
    return url.substr(path_start + 1, (query_start == std::string::npos ? url.size() : query_start) - path_start - 1);
}

static bool equivalent_rudp_stream_url(const std::string &left, const std::string &right)
{
    if (!starts_with(left, "rudp://") || !starts_with(right, "rudp://")) {
        return left == right;
    }
    try {
        const auto left_parts = muninn_rudp_url::parse(left);
        const auto right_parts = muninn_rudp_url::parse(right);
        if (!left_parts || !right_parts) {
            return left == right;
        }
        return left_parts->port == right_parts->port &&
               canonical_muninn_stream_id(rudp_url_stream_id(left)) == canonical_muninn_stream_id(rudp_url_stream_id(right)) &&
               left_parts->connection_id == right_parts->connection_id &&
               left_parts->audio_connection_id == right_parts->audio_connection_id &&
               left_parts->sender_resend_delay_ms == right_parts->sender_resend_delay_ms &&
               left_parts->reliable_expire_after_ms == right_parts->reliable_expire_after_ms &&
               left_parts->video_assembly_deadline_ms == right_parts->video_assembly_deadline_ms &&
               left_parts->gap_wait_ms == right_parts->gap_wait_ms;
    } catch (...) {
        return left == right;
    }
}

static std::string selected_source_id(
    const std::vector<std::pair<std::string, std::string>> &sources,
    const std::string &current)
{
    if (!current.empty()) {
        for (const auto &source : sources) {
            if (source.first == current) {
                return current;
            }
        }
        for (const auto &source : sources) {
            if (source.first.rfind(current, 0) == 0) {
                return source.first;
            }
        }
    }
    return sources.empty() ? std::string{} : sources.front().first;
}

static std::string endpoint_setting(obs_data_t *settings, const char *key, const char *current_default, const char *deprecated_default)
{
    std::string value = obs_string(settings, key, current_default);
    if (value == deprecated_default) {
        obs_data_set_string(settings, key, current_default);
        return current_default;
    }
    return value;
}

static void request_stream_activation(MuninnSource *ctx, const std::string &stream_id, bool force = false)
{
    const auto canonical_stream_id = canonical_muninn_stream_id(stream_id);
    if (!ctx || canonical_stream_id.empty() || ctx->command_rudp_target.empty()) {
        return;
    }
    const auto now = std::chrono::steady_clock::now();
    if (!force &&
        ctx->last_requested_stream_id == canonical_stream_id &&
        ctx->last_requested_video_source_id == ctx->video_source_id &&
        ctx->last_requested_audio_source_id == ctx->audio_source_id &&
        now - ctx->last_activation_request < std::chrono::milliseconds(MuninnActivationRetryMs)) {
        return;
    }
    ctx->last_requested_stream_id = canonical_stream_id;
    ctx->last_requested_video_source_id = ctx->video_source_id;
    ctx->last_requested_audio_source_id = ctx->audio_source_id;
    ctx->last_activation_request = now;

    const auto command_target = ctx->command_rudp_target;
    const auto payload = encode_capture_stream_command_wire(ctx, canonical_stream_id, command_observed_at());
    std::thread([command_target, payload] {
        if (!publish_capture_command_rudp(command_target, payload)) {
            blog(LOG_WARNING, "Muninn Stream failed to publish native RUDP activation request");
        }
    }).detach();
    blog(LOG_INFO,
         "Muninn Stream requested activation for %s video_source=%s audio_source=%s over native RUDP %s video_bitrate_kbps=%u latency_budget_ms=%u audio_reorder_ms=%u",
         canonical_stream_id.c_str(),
         ctx->video_source_id.c_str(),
         ctx->audio_source_id.c_str(),
         command_target.c_str(),
         ctx->rudp_video_bitrate_kbps,
         ctx->rudp_latency_budget_ms,
         ctx->rudp_audio_reorder_ms);
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

static std::vector<std::pair<std::string, std::string>> pair_source_options(
    const std::vector<std::string> &ids,
    const std::vector<std::string> &labels)
{
    std::vector<std::pair<std::string, std::string>> options;
    options.reserve(ids.size());
    for (size_t index = 0; index < ids.size(); ++index) {
        if (ids[index].empty()) {
            continue;
        }
        const std::string label = index < labels.size() && !labels[index].empty() ? labels[index] : ids[index];
        options.emplace_back(ids[index], label);
    }
    return options;
}

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
    std::string command_rudp_target = MuninnCommandRudpTarget;
    std::string media_target_host = MuninnDefaultMediaTargetHost;
    uint16_t media_port = MuninnDefaultMediaPort;
    uint32_t media_packet_bytes = MuninnDefaultMediaPacketBytes;
    uint32_t rudp_video_bitrate_kbps = MuninnDefaultRudpVideoBitrateKbps;
    uint32_t rudp_latency_budget_ms = MuninnDefaultRudpLatencyBudgetMs;
    std::vector<std::pair<std::string, std::string>> video_sources;
    std::vector<std::pair<std::string, std::string>> audio_sources;
    if (length > 7) {
        command_rudp_target = reader.read_string();
    }
    if (length > 8) {
        media_target_host = reader.read_string();
    }
    if (length > 9) {
        const auto parsed = reader.read_unsigned();
        if (parsed > 0 && parsed <= 65535) {
            media_port = static_cast<uint16_t>(parsed);
        }
    }
    if (length > 10) {
        const auto parsed = reader.read_unsigned();
        if (parsed > 0 && parsed <= UINT32_MAX) {
            media_packet_bytes = static_cast<uint32_t>(parsed);
        }
    }
    if (length > 11) {
        const auto parsed = reader.read_unsigned();
        if (parsed > 0 && parsed <= MuninnMaxRudpVideoBitrateKbps) {
            rudp_video_bitrate_kbps = static_cast<uint32_t>(parsed);
        }
    }
    if (length > 12) {
        const auto parsed = reader.read_unsigned();
        if (parsed > 0 && parsed <= MuninnMaxRudpLatencyBudgetMs) {
            rudp_latency_budget_ms = static_cast<uint32_t>(parsed);
        }
    }
    if (length > 16) {
        auto video_source_ids = reader.read_string_array();
        auto video_source_labels = reader.read_string_array();
        auto audio_source_ids = reader.read_string_array();
        auto audio_source_labels = reader.read_string_array();
        video_sources = pair_source_options(video_source_ids, video_source_labels);
        audio_sources = pair_source_options(audio_source_ids, audio_source_labels);
    }
    if (video_sources.empty()) {
        video_sources.emplace_back("display:0", "Display 1");
    }
    if (audio_sources.empty()) {
        audio_sources.emplace_back("wasapi-loopback:Realtek", "Realtek loopback");
    }
    for (uint32_t index = length > 16 ? 17 : 13; index < length; ++index) {
        reader.skip();
    }

    std::vector<MuninnStreamOption> options;
    const size_t count = std::min({stream_ids.size(), labels.size(), urls.size(), states.size()});
    options.reserve(count);
    for (size_t index = 0; index < count; ++index) {
        if (stream_ids[index].empty()) {
            continue;
        }
        MuninnStreamOption option{
            stream_ids[index],
            labels[index].empty() ? stream_ids[index] : labels[index],
            urls[index],
            states[index],
            command_rudp_target,
            media_target_host,
            media_port,
            media_packet_bytes,
            rudp_video_bitrate_kbps,
            rudp_latency_budget_ms,
            video_sources,
            audio_sources,
        };
        if (!is_playable_muninn_stream(option)) {
            continue;
        }
        options.push_back(std::move(option));
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

class CatalogDiscoveryReceiver {
public:
    CatalogDiscoveryReceiver() = default;

    ~CatalogDiscoveryReceiver()
    {
        stop();
    }

    void start()
    {
        if (running_) {
            return;
        }
        running_ = true;
        worker_ = std::thread([this] { run(); });
    }

    void stop()
    {
        running_ = false;
        if (socket_ != INVALID_SOCKET) {
            closesocket(socket_);
            socket_ = INVALID_SOCKET;
        }
        if (worker_.joinable()) {
            worker_.join();
        }
    }

    std::vector<MuninnStreamOption> options() const
    {
        std::lock_guard<std::mutex> lock(mutex_);
        return options_;
    }

private:
    void run()
    {
#ifdef _WIN32
        WSADATA wsa = {};
        if (WSAStartup(MAKEWORD(2, 2), &wsa) != 0) {
            return;
        }
#endif
        socket_ = ::socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
        if (socket_ == INVALID_SOCKET) {
#ifdef _WIN32
            WSACleanup();
#endif
            return;
        }
        sockaddr_in addr = {};
        addr.sin_family = AF_INET;
        addr.sin_addr.s_addr = htonl(INADDR_ANY);
        addr.sin_port = htons(MuninnCatalogDiscoveryPort);
        if (bind(socket_, reinterpret_cast<const sockaddr *>(&addr), sizeof(addr)) != 0) {
            closesocket(socket_);
            socket_ = INVALID_SOCKET;
#ifdef _WIN32
            WSACleanup();
#endif
            return;
        }

        timeval timeout = {};
        timeout.tv_sec = 0;
        timeout.tv_usec = 200000;
        setsockopt(socket_, SOL_SOCKET, SO_RCVTIMEO, reinterpret_cast<const char *>(&timeout), sizeof(timeout));

        std::array<uint8_t, 4096> buffer = {};
        uint32_t bridge_sequence = 1;
        while (running_) {
            sockaddr_in remote = {};
            int remote_len = sizeof(remote);
            const int received = recvfrom(socket_, reinterpret_cast<char *>(buffer.data()), static_cast<int>(buffer.size()), 0,
                                          reinterpret_cast<sockaddr *>(&remote), &remote_len);
            if (received <= 0) {
                continue;
            }
            handle_packet(buffer.data(), static_cast<size_t>(received), remote, bridge_sequence);
        }

        if (socket_ != INVALID_SOCKET) {
            closesocket(socket_);
            socket_ = INVALID_SOCKET;
        }
#ifdef _WIN32
        WSACleanup();
#endif
    }

    void handle_packet(const uint8_t *packet, size_t size, const sockaddr_in &remote, uint32_t &bridge_sequence)
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
        if (version != 0 || connection_id != MuninnCatalogDiscoveryConnectionId || header_bytes < 36 || size < header_bytes) {
            return;
        }
        const size_t payload_offset = static_cast<size_t>(header_bytes);
        if (payload_offset + payload_len > size || 36 + channel_len > header_bytes) {
            return;
        }

        if (packet_type == 1) {
            ack_tracker_.reset();
            ack_tracker_.remember(sequence);
            const auto [ack, ack_mask] = ack_tracker_.state();
            send_control_packet(2, 0x03, bridge_sequence++, ack, ack_mask, remote);
            return;
        }

        if (packet_type != 3 || fragment_count != 0) {
            return;
        }
        ack_tracker_.remember(sequence);
        try {
            auto decoded = decode_obs_catalog_payload(
                std::vector<uint8_t>(packet + payload_offset, packet + payload_offset + payload_len));
            if (!decoded.empty()) {
                const auto remote_host = sockaddr_ipv4_string(remote);
                for (auto &option : decoded) {
                    if (option.command_rudp_target.empty()) {
                        option.command_rudp_target = MuninnCommandRudpTarget;
                    }
                    if (!remote_host.empty()) {
                        option.command_rudp_target =
                            replace_endpoint_host_if_unspecified(option.command_rudp_target, remote_host);
                    }
                    if (option.media_target_host.empty()) {
                        option.media_target_host = MuninnDefaultMediaTargetHost;
                    }
                    if (option.media_port == 0) {
                        option.media_port = MuninnDefaultMediaPort;
                    }
                    if (option.media_packet_bytes == 0) {
                        option.media_packet_bytes = MuninnDefaultMediaPacketBytes;
                    }
                    if (option.rudp_video_bitrate_kbps == 0) {
                        option.rudp_video_bitrate_kbps = MuninnDefaultRudpVideoBitrateKbps;
                    }
                    if (option.rudp_latency_budget_ms == 0) {
                        option.rudp_latency_budget_ms = MuninnDefaultRudpLatencyBudgetMs;
                    }
                }
                std::lock_guard<std::mutex> lock(mutex_);
                const bool first_discovery = options_.empty();
                options_ = std::move(decoded);
                if (first_discovery) {
                    blog(LOG_INFO, "Muninn Stream accepted %llu stream option(s) from RUDP catalog discovery",
                         static_cast<unsigned long long>(options_.size()));
                }
            }
        } catch (const std::exception &error) {
            blog(LOG_WARNING, "Muninn Stream could not decode RUDP catalog discovery payload: %s", error.what());
        }
        const auto [ack, ack_mask] = ack_tracker_.state();
        send_control_packet(2, 0x02, bridge_sequence++, ack, ack_mask, remote);
    }

    void send_control_packet(uint8_t packet_type, uint8_t flags, uint32_t sequence, uint32_t ack, uint32_t ack_mask, const sockaddr_in &remote)
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
        write_be32(response, 8, MuninnCatalogDiscoveryConnectionId);
        write_be32(response, 12, sequence);
        write_be32(response, 16, ack);
        write_be32(response, 20, ack_mask);
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

    std::atomic<bool> running_ = false;
    SOCKET socket_ = INVALID_SOCKET;
    std::thread worker_;
    mutable std::mutex mutex_;
    std::vector<MuninnStreamOption> options_;
    muninn_rudp_ack::AckTracker ack_tracker_;
};

static void release_ffmpeg_children(MuninnSource *ctx)
{
    if (ctx->audio_decoder) {
        ctx->audio_decoder->stop();
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

static void release_media(MuninnSource *ctx)
{
    if (ctx->bridge) {
        ctx->bridge->stop();
    }
    release_ffmpeg_children(ctx);
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

static void create_or_update_media(MuninnSource *ctx, const std::string &url, bool force_restart = false)
{
    if (url.empty()) {
        release_media(ctx);
        ctx->stream_url.clear();
        return;
    }

    if (!force_restart && ctx->media && equivalent_rudp_stream_url(ctx->stream_url, url)) {
        ctx->stream_url = url;
        return;
    }

    if (starts_with(url, "rudp://")) {
        if (!force_restart && ctx->bridge && ctx->media && ctx->audio_decoder && !ctx->stream_url.empty()) {
            const auto previous_parts = muninn_rudp_url::parse(ctx->stream_url);
            const auto next_parts = muninn_rudp_url::parse(url);
            if (previous_parts && next_parts &&
                previous_parts->video_local_port == next_parts->video_local_port &&
                previous_parts->audio_local_port == next_parts->audio_local_port) {
                ctx->bridge->start(url);
                ctx->stream_url = url;
                return;
            }
        }
        if (force_restart && ctx->bridge && equivalent_rudp_stream_url(ctx->stream_url, url)) {
            release_ffmpeg_children(ctx);
        } else {
            release_media(ctx);
        }
        if (!ctx->bridge) {
            ctx->bridge = std::make_unique<RudpMediaBridge>();
        }
        const auto outputs = ctx->bridge->start(url);
        ctx->media = create_ffmpeg_child_source(ctx, "Muninn Stream Video", outputs.video_url, "h264", false);
        if (!ctx->audio_decoder) {
            ctx->audio_decoder = std::make_unique<FfmpegAudioDecoder>();
        }
        ctx->audio_decoder->start(ctx, outputs.audio_url);
        if (ctx->media) {
            ctx->stream_url = url;
        } else {
            ctx->stream_url.clear();
        }
        return;
    } else if (ctx->bridge) {
        ctx->bridge->stop();
    }

    release_ffmpeg_children(ctx);
    ctx->media = create_ffmpeg_child_source(ctx, "Muninn Stream Media", url, "mpegts", true);
    if (ctx->media) {
        ctx->stream_url = url;
    } else {
        ctx->stream_url.clear();
    }
}

static void refresh_catalog(MuninnSource *ctx)
{
    if (!ctx) {
        return;
    }
    ctx->catalog_options =
        ctx->catalog_discovery ? ctx->catalog_discovery->options() : std::vector<MuninnStreamOption>{};
    if (ctx->catalog_options.empty()) {
        ctx->catalog_options = read_catalog(ctx->store_path);
    }
    if (ctx->catalog_options.empty()) {
        ctx->catalog_options.push_back(default_muninn_stream_option());
    }
    ctx->next_catalog_refresh =
        std::chrono::steady_clock::now() + std::chrono::milliseconds(MuninnCatalogRefreshMs);
}

static void reconcile_selected_stream(MuninnSource *ctx)
{
    if (!ctx) {
        return;
    }
    if (ctx->catalog_options.empty()) {
        create_or_update_media(ctx, "");
        return;
    }

    const auto *selected = find_selected(ctx->catalog_options, ctx->stream_id);
    if (!selected) {
        create_or_update_media(ctx, "");
        return;
    }

    const auto previous_canonical_stream_id = canonical_muninn_stream_id(ctx->stream_id);
    const auto selected_canonical_stream_id = canonical_muninn_stream_id(selected->stream_id);
    const bool selection_changed =
        !previous_canonical_stream_id.empty() && previous_canonical_stream_id != selected_canonical_stream_id;
    ctx->stream_id = selected->stream_id;
    const auto previous_video_source_id = ctx->video_source_id;
    const auto previous_audio_source_id = ctx->audio_source_id;
    ctx->video_source_id = selected_source_id(selected->video_sources, ctx->video_source_id);
    ctx->audio_source_id = selected_source_id(selected->audio_sources, ctx->audio_source_id);
    const bool source_selection_changed =
        ctx->video_source_id != previous_video_source_id || ctx->audio_source_id != previous_audio_source_id;
    if (selection_changed || source_selection_changed) {
        ctx->last_requested_stream_id.clear();
        ctx->last_requested_video_source_id.clear();
        ctx->last_requested_audio_source_id.clear();
        ctx->last_media_restart = std::chrono::steady_clock::time_point::min();
    }
    const auto previous_command_rudp_target = ctx->command_rudp_target;
    const auto previous_media_target_host = ctx->media_target_host;
    const auto previous_media_port = ctx->media_port;
    const auto previous_media_packet_bytes = ctx->media_packet_bytes;
    ctx->command_rudp_target = selected->command_rudp_target.empty() ? MuninnCommandRudpTarget : selected->command_rudp_target;
    std::string bootstrap_host;
    uint16_t bootstrap_port = 0;
    if (split_ipv4_endpoint(MuninnCommandRudpTarget, bootstrap_host, bootstrap_port)) {
        ctx->command_rudp_target = replace_endpoint_host_if_unspecified(ctx->command_rudp_target, bootstrap_host);
    }
    ctx->media_target_host = selected->media_target_host.empty() ? MuninnDefaultMediaTargetHost : selected->media_target_host;
    ctx->media_port = selected->media_port == 0 ? MuninnDefaultMediaPort : selected->media_port;
    ctx->media_packet_bytes =
        selected->media_packet_bytes == 0 ? MuninnDefaultMediaPacketBytes : selected->media_packet_bytes;
    if (ctx->command_rudp_target != previous_command_rudp_target ||
        ctx->media_target_host != previous_media_target_host ||
        ctx->media_port != previous_media_port ||
        ctx->media_packet_bytes != previous_media_packet_bytes) {
        ctx->last_requested_stream_id.clear();
    }

    const auto stream_url = selected->url.empty() && ctx->bridge && !ctx->stream_url.empty()
                                ? ctx->stream_url
                                : playable_rudp_stream_url(ctx, *selected);
    const bool stream_url_changed = stream_url != ctx->stream_url;
    if (stream_url_changed) {
        blog(LOG_INFO,
             "Muninn Stream selected RUDP URL changed: previous='%s' selected='%s' effective='%s'",
             ctx->stream_url.c_str(),
             selected->url.c_str(),
             stream_url.c_str());
    }
    create_or_update_media(ctx, stream_url);

    bool activation_needed = stream_url_changed || ctx->last_requested_stream_id != selected_canonical_stream_id;
    bool force_activation = stream_url_changed;
    if (ctx->bridge && starts_with(stream_url, "rudp://")) {
        const uint64_t now_ms = steady_millis();
        if (ctx->bridge->consume_decoder_reset_request()) {
            blog(LOG_INFO, "Muninn Stream RUDP sender session changed; refreshing local decoder readers");
            const auto now = std::chrono::steady_clock::now();
            if (now - ctx->last_media_restart >= std::chrono::milliseconds(MuninnMediaRestartCooldownMs)) {
                create_or_update_media(ctx, stream_url, true);
                ctx->last_media_restart = now;
            }
        }
        if (ctx->bridge->consume_stale_transition(now_ms)) {
            blog(LOG_WARNING, "Muninn Stream receiver media is stale; refreshing local media readers and re-requesting activation");
            const auto now = std::chrono::steady_clock::now();
            if (now - ctx->last_media_restart >= std::chrono::milliseconds(MuninnMediaRestartCooldownMs)) {
                create_or_update_media(ctx, stream_url, true);
                ctx->last_media_restart = now;
            }
            ctx->last_requested_stream_id.clear();
            activation_needed = true;
            force_activation = true;
        } else if (ctx->bridge->needs_activation(now_ms)) {
            if (!ctx->bridge->is_running()) {
                blog(LOG_WARNING, "Muninn Stream RUDP bridge is not running; rebuilding local media bridge");
                create_or_update_media(ctx, stream_url, true);
            }
            activation_needed = true;
        }
    }

    if (activation_needed) {
        request_stream_activation(ctx, selected->stream_id, force_activation);
    }
}

static void populate_stream_list_from_options(obs_property_t *property, const std::vector<MuninnStreamOption> &options)
{
    obs_property_list_clear(property);
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

static void populate_source_list(
    obs_property_t *property,
    const std::vector<std::pair<std::string, std::string>> &sources)
{
    obs_property_list_clear(property);
    for (const auto &source : sources) {
        obs_property_list_add_string(property, source.second.c_str(), source.first.c_str());
    }
}

static std::vector<std::pair<std::string, std::string>> catalog_video_sources(const MuninnSource *ctx)
{
    const auto *selected = ctx ? find_selected(ctx->catalog_options, ctx->stream_id) : nullptr;
    if (selected && !selected->video_sources.empty()) {
        return selected->video_sources;
    }
    return {{"display:0", "Display 1"}};
}

static std::vector<std::pair<std::string, std::string>> catalog_audio_sources(const MuninnSource *ctx)
{
    const auto *selected = ctx ? find_selected(ctx->catalog_options, ctx->stream_id) : nullptr;
    if (selected && !selected->audio_sources.empty()) {
        return selected->audio_sources;
    }
    return {{"wasapi-loopback:Realtek", "Realtek loopback"}};
}

static void populate_stream_list(obs_property_t *property, const std::string &store_path)
{
    auto options = read_catalog(store_path);
    if (options.empty()) {
        options.push_back(default_muninn_stream_option());
    }
    populate_stream_list_from_options(property, options);
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
    if (data) {
        auto *ctx = static_cast<MuninnSource *>(data);
        refresh_catalog(ctx);
        obs_property_t *video_list = obs_properties_get(props, "video_source_id");
        obs_property_t *audio_list = obs_properties_get(props, "audio_source_id");
        if (video_list) {
            populate_source_list(video_list, catalog_video_sources(ctx));
        }
        if (audio_list) {
            populate_source_list(audio_list, catalog_audio_sources(ctx));
        }
    }
    return true;
}

static const char *muninn_get_name(void *)
{
    return obs_module_text("MuninnStream");
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
    const auto previous_command_rudp_target = ctx->command_rudp_target;
    const auto previous_media_target_host = ctx->media_target_host;
    const auto previous_media_port = ctx->media_port;
    const auto previous_rudp_video_bitrate_kbps = ctx->rudp_video_bitrate_kbps;
    const auto previous_rudp_latency_budget_ms = ctx->rudp_latency_budget_ms;
    const auto previous_rudp_audio_reorder_ms = ctx->rudp_audio_reorder_ms;
    const auto previous_video_source_id = ctx->video_source_id;
    const auto previous_audio_source_id = ctx->audio_source_id;

    ctx->store_path = obs_string(settings, "store_path", MuninnStorePath);
    ctx->command_exe_path = obs_string(settings, "command_exe_path", MuninnCommandExePath);
    ctx->command_rudp_target = endpoint_setting(
        settings,
        "command_rudp_target",
        MuninnCommandRudpTarget,
        MuninnDeprecatedWireGuardCommandRudpTarget);
    ctx->host_id = obs_string(settings, "host_id", MuninnDefaultHostId);
    ctx->media_target_host = endpoint_setting(
        settings,
        "media_target_host",
        MuninnDefaultMediaTargetHost,
        MuninnDeprecatedWireGuardMediaTargetHost);
    ctx->media_port = static_cast<uint16_t>(obs_data_get_int(settings, "media_port") > 0
                                                ? obs_data_get_int(settings, "media_port")
                                                : MuninnDefaultMediaPort);
    const auto configured_bitrate = obs_data_get_int(settings, "rudp_video_bitrate_kbps");
    ctx->rudp_video_bitrate_kbps = static_cast<uint32_t>(
        configured_bitrate > 0 ? std::min<int64_t>(configured_bitrate, MuninnMaxRudpVideoBitrateKbps)
                               : MuninnDefaultRudpVideoBitrateKbps);
    const auto configured_latency = obs_data_get_int(settings, "rudp_latency_budget_ms");
    ctx->rudp_latency_budget_ms = static_cast<uint32_t>(
        configured_latency > 0 ? std::min<int64_t>(configured_latency, MuninnMaxRudpLatencyBudgetMs)
                               : MuninnDefaultRudpLatencyBudgetMs);
    const auto configured_audio_reorder = obs_data_get_int(settings, "rudp_audio_reorder_ms");
    ctx->rudp_audio_reorder_ms = static_cast<uint32_t>(
        configured_audio_reorder >= 0 ? std::min<int64_t>(configured_audio_reorder, MuninnMaxRudpAudioReorderMs)
                                      : MuninnDefaultRudpAudioReorderMs);
    ctx->stream_id = obs_string(settings, "stream_id", "");
    ctx->video_source_id = obs_string(settings, "video_source_id", ctx->video_source_id.c_str());
    ctx->audio_source_id = obs_string(settings, "audio_source_id", ctx->audio_source_id.c_str());
    if (ctx->command_rudp_target != previous_command_rudp_target ||
        ctx->media_target_host != previous_media_target_host ||
        ctx->media_port != previous_media_port ||
        ctx->rudp_video_bitrate_kbps != previous_rudp_video_bitrate_kbps ||
        ctx->rudp_latency_budget_ms != previous_rudp_latency_budget_ms ||
        ctx->rudp_audio_reorder_ms != previous_rudp_audio_reorder_ms ||
        ctx->video_source_id != previous_video_source_id ||
        ctx->audio_source_id != previous_audio_source_id) {
        ctx->last_requested_stream_id.clear();
        ctx->last_requested_video_source_id.clear();
        ctx->last_requested_audio_source_id.clear();
    }

    refresh_catalog(ctx);
    reconcile_selected_stream(ctx);
}

static void *muninn_create(obs_data_t *settings, obs_source_t *source)
{
    auto *ctx = new MuninnSource();
    ctx->source = source;
    ctx->bridge = std::make_unique<RudpMediaBridge>();
    ctx->catalog_discovery = std::make_unique<CatalogDiscoveryReceiver>();
    ctx->catalog_discovery->start();
    start_audio_worker(ctx);
    muninn_update(ctx, settings);
    return ctx;
}

static void muninn_destroy(void *data)
{
    auto *ctx = static_cast<MuninnSource *>(data);
    release_media(ctx);
    if (ctx->catalog_discovery) {
        ctx->catalog_discovery->stop();
    }
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
    obs_data_set_default_int(settings, "rudp_video_bitrate_kbps", MuninnDefaultRudpVideoBitrateKbps);
    obs_data_set_default_int(settings, "rudp_latency_budget_ms", MuninnDefaultRudpLatencyBudgetMs);
    obs_data_set_default_int(settings, "rudp_audio_reorder_ms", MuninnDefaultRudpAudioReorderMs);
    obs_data_set_default_string(settings, "stream_id", "");
    obs_data_set_default_string(settings, "video_source_id", "display:0");
    obs_data_set_default_string(settings, "audio_source_id", "wasapi-loopback:Realtek");
}

static obs_properties_t *muninn_properties(void *data)
{
    auto *ctx = static_cast<MuninnSource *>(data);
    const auto store_path = ctx ? ctx->store_path : std::string(MuninnStorePath);

    obs_properties_t *props = obs_properties_create();
    obs_property_t *streams =
        obs_properties_add_list(props, "stream_id", obs_module_text("MuninnStream.Stream"), OBS_COMBO_TYPE_LIST, OBS_COMBO_FORMAT_STRING);
    if (ctx) {
        refresh_catalog(ctx);
        populate_stream_list_from_options(streams, ctx->catalog_options);
    } else {
        populate_stream_list(streams, store_path);
    }
    obs_property_t *video_sources =
        obs_properties_add_list(props, "video_source_id", obs_module_text("MuninnStream.Video"), OBS_COMBO_TYPE_LIST, OBS_COMBO_FORMAT_STRING);
    obs_property_t *audio_sources =
        obs_properties_add_list(props, "audio_source_id", obs_module_text("MuninnStream.Audio"), OBS_COMBO_TYPE_LIST, OBS_COMBO_FORMAT_STRING);
    if (ctx) {
        populate_source_list(video_sources, catalog_video_sources(ctx));
        populate_source_list(audio_sources, catalog_audio_sources(ctx));
    } else {
        populate_source_list(video_sources, {{"display:0", "Display 1"}});
        populate_source_list(audio_sources, {{"wasapi-loopback:Realtek", "Realtek loopback"}});
    }
    obs_properties_add_int(
        props,
        "rudp_video_bitrate_kbps",
        obs_module_text("MuninnStream.VideoBitrateKbps"),
        1000,
        MuninnMaxRudpVideoBitrateKbps,
        500);
    obs_properties_add_int(
        props,
        "rudp_latency_budget_ms",
        obs_module_text("MuninnStream.VideoLatencyMs"),
        100,
        MuninnMaxRudpLatencyBudgetMs,
        50);
    obs_properties_add_int(
        props,
        "rudp_audio_reorder_ms",
        obs_module_text("MuninnStream.AudioReorderMs"),
        0,
        MuninnMaxRudpAudioReorderMs,
        25);
    obs_properties_add_button(props, "refresh_streams", obs_module_text("MuninnStream.Refresh"), muninn_refresh_streams);
    return props;
}

static void muninn_video_tick(void *data, float)
{
    auto *ctx = static_cast<MuninnSource *>(data);
    if (!ctx) {
        return;
    }
    if (std::chrono::steady_clock::now() >= ctx->next_catalog_refresh) {
        refresh_catalog(ctx);
    }
    reconcile_selected_stream(ctx);
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

    const float *planes[MAX_AV_PLANES] = {};
    for (size_t plane = 0; plane < MAX_AV_PLANES; ++plane) {
        if (!audio_data->data[plane]) {
            continue;
        }
        planes[plane] = reinterpret_cast<const float *>(audio_data->data[plane]);
    }
    queue_planar_audio(ctx, planes, audio_data->frames, audio_info.speakers, audio_info.samples_per_sec);
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
    .video_tick = muninn_video_tick,
    .video_render = muninn_render,
    .enum_active_sources = muninn_enum_active_sources,
};

} // namespace

void mimir_register_muninn_source()
{
    obs_register_source(&muninn_source_info);
}
