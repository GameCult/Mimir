#include <cstdint>
#include <iomanip>
#include <fstream>
#include <iostream>
#include <map>
#include <optional>
#include <stdexcept>
#include <string>
#include <vector>

#include "muninn_media_wire.h"
#include "muninn_rudp_url.h"

struct MuninnMediaPayload {
    enum class Kind { Unknown, Video, Audio, Feedback };
    Kind kind = Kind::Unknown;
    std::string stream_id;
    std::string session_id;
    uint64_t frame_id = 0;
    uint16_t chunk_index = 0;
    uint16_t chunk_count = 1;
    uint64_t receiver_late_frames = 0;
    uint64_t receiver_missing_chunks = 0;
    bool receiver_requested_keyframe = false;
    std::vector<uint8_t> payload;
};

struct MuninnVideoFrameAssembly {
    uint16_t chunk_count = 0;
    uint16_t chunks_received = 0;
    std::vector<std::vector<uint8_t>> chunks;
};

class MediaMsgpackReader {
public:
    MediaMsgpackReader(const uint8_t *data, size_t size) : data_(data), size_(size) {}
    explicit MediaMsgpackReader(const std::vector<uint8_t> &bytes) : data_(bytes.data()), size_(bytes.size()) {}

    uint32_t read_map_len()
    {
        const uint8_t tag = read_u8();
        if ((tag & 0xf0) == 0x80) return tag & 0x0f;
        if (tag == 0xde) return read_u16();
        if (tag == 0xdf) return read_u32();
        throw std::runtime_error("expected MessagePack map");
    }

    uint32_t read_array_len()
    {
        const uint8_t tag = read_u8();
        if ((tag & 0xf0) == 0x90) return tag & 0x0f;
        if (tag == 0xdc) return read_u16();
        if (tag == 0xdd) return read_u32();
        throw std::runtime_error("expected MessagePack array");
    }

    std::string read_string()
    {
        const uint8_t tag = read_u8();
        uint32_t length = 0;
        if ((tag & 0xe0) == 0xa0) length = tag & 0x1f;
        else if (tag == 0xd9) length = read_u8();
        else if (tag == 0xda) length = read_u16();
        else if (tag == 0xdb) length = read_u32();
        else throw std::runtime_error("expected MessagePack string");
        require(length);
        std::string value(reinterpret_cast<const char *>(data_ + pos_), length);
        pos_ += length;
        return value;
    }

    std::vector<uint8_t> read_bytes()
    {
        const uint8_t tag = read_u8();
        uint32_t length = 0;
        if (tag == 0xc4) length = read_u8();
        else if (tag == 0xc5) length = read_u16();
        else if (tag == 0xc6) length = read_u32();
        else if ((tag & 0xf0) == 0x90 || tag == 0xdc || tag == 0xdd) {
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
        if (tag <= 0x7f) return tag;
        if (tag == 0xcc) return read_u8();
        if (tag == 0xcd) return read_u16();
        if (tag == 0xce) return read_u32();
        if (tag == 0xcf) return read_u64();
        throw std::runtime_error("expected MessagePack unsigned integer");
    }

    bool read_bool()
    {
        const uint8_t tag = read_u8();
        if (tag == 0xc2) return false;
        if (tag == 0xc3) return true;
        throw std::runtime_error("expected MessagePack bool");
    }

    void skip()
    {
        const uint8_t tag = read_u8();
        if (tag <= 0x7f || tag >= 0xe0 || tag == 0xc0 || tag == 0xc2 || tag == 0xc3) return;
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
        if (count > size_ - pos_) throw std::runtime_error("truncated MessagePack input");
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
        for (uint32_t index = 0; index < count; ++index) skip();
    }
};

static std::optional<MuninnMediaPayload> decode_muninn_media_payload(const uint8_t *payload, uint32_t payload_len)
{
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
                if (document_key == "schemaId") schema_id = wire.read_string();
                else if (document_key == "payload") document_payload = wire.read_bytes();
                else wire.skip();
            }
        } else {
            wire.skip();
        }
    }

    if (schema_version != "cultnet.document_put_raw.v0" || document_payload.empty()) return std::nullopt;

    MediaMsgpackReader record(document_payload);
    const uint32_t record_len = record.read_array_len();
    if (schema_id == "muninn.media_video_access_unit.v1") {
        if (record_len < 14) return std::nullopt;
        MuninnMediaPayload media;
        media.kind = MuninnMediaPayload::Kind::Video;
        media.stream_id = record.read_string();
        media.session_id = record.read_string();
        media.frame_id = record.read_unsigned();
        record.skip();
        record.skip();
        record.skip();
        record.skip();
        record.skip();
        record.skip();
        record.skip();
        record.skip();
        media.chunk_index = static_cast<uint16_t>(record.read_unsigned());
        media.chunk_count = static_cast<uint16_t>(record.read_unsigned());
        media.payload = record.read_bytes();
        return media;
    }
    if (schema_id == "muninn.media_audio_packet.v1") {
        if (record_len < 10) return std::nullopt;
        for (uint32_t index = 0; index < 9; ++index) record.skip();
        MuninnMediaPayload media;
        media.kind = MuninnMediaPayload::Kind::Audio;
        media.payload = record.read_bytes();
        return media;
    }
    if (schema_id == "muninn.media_receiver_feedback.v1") {
        if (record_len < 11) return std::nullopt;
        MuninnMediaPayload media;
        media.kind = MuninnMediaPayload::Kind::Feedback;
        media.stream_id = record.read_string();
        media.session_id = record.read_string();
        record.skip(); // receiver_id
        record.skip(); // highest_decodable_frame_id
        record.skip(); // missing_frame_ids
        media.receiver_late_frames = record.read_array_len();
        for (uint32_t index = 0; index < media.receiver_late_frames; ++index) record.skip();
        media.receiver_requested_keyframe = record.read_bool();
        record.skip(); // jitter_us
        record.skip(); // decode_queue_us
        record.skip(); // observed_at
        media.receiver_missing_chunks = record.read_array_len();
        for (uint32_t index = 0; index < media.receiver_missing_chunks; ++index) record.skip();
        return media;
    }
    return std::nullopt;
}

static std::optional<std::vector<uint8_t>> assemble_video_payload(
    const MuninnMediaPayload &media,
    std::map<std::string, MuninnVideoFrameAssembly> &assemblies)
{
    if (media.kind != MuninnMediaPayload::Kind::Video || media.payload.empty()) return std::nullopt;
    if (media.chunk_count == 0 || media.chunk_index >= media.chunk_count) return std::nullopt;
    if (media.chunk_count == 1) return media.payload;

    const auto key = media.stream_id + ":" + media.session_id + ":" + std::to_string(media.frame_id);
    auto &assembly = assemblies[key];
    if (assembly.chunks.empty()) {
        assembly.chunk_count = media.chunk_count;
        assembly.chunks.resize(media.chunk_count);
    }
    if (assembly.chunk_count != media.chunk_count) {
        assemblies.erase(key);
        return std::nullopt;
    }

    auto &slot = assembly.chunks[media.chunk_index];
    if (!slot.empty()) return std::nullopt;
    slot = media.payload;
    ++assembly.chunks_received;
    if (assembly.chunks_received != assembly.chunk_count) return std::nullopt;

    size_t byte_count = 0;
    for (const auto &chunk : assembly.chunks) byte_count += chunk.size();
    std::vector<uint8_t> assembled;
    assembled.reserve(byte_count);
    for (const auto &chunk : assembly.chunks) {
        assembled.insert(assembled.end(), chunk.begin(), chunk.end());
    }
    assemblies.erase(key);
    return assembled;
}

static void print_payload(const MuninnMediaPayload &decoded)
{
    if (decoded.kind == MuninnMediaPayload::Kind::Feedback) {
        std::cout << "feedback stream_id=" << decoded.stream_id
                  << " session_id=" << decoded.session_id
                  << " requested_keyframe=" << (decoded.receiver_requested_keyframe ? 1 : 0)
                  << " late_frames=" << decoded.receiver_late_frames
                  << " missing_chunks=" << decoded.receiver_missing_chunks << "\n";
        return;
    }
    std::cout << (decoded.kind == MuninnMediaPayload::Kind::Video ? "video" : "audio");
    if (decoded.kind == MuninnMediaPayload::Kind::Video) {
        std::cout << " frame_id=" << decoded.frame_id
                  << " chunk=" << decoded.chunk_index << "/" << decoded.chunk_count;
    }
    std::cout << " payload_bytes=" << decoded.payload.size() << " first=";
    if (!decoded.payload.empty()) {
        std::cout << static_cast<unsigned>(decoded.payload[0]);
    }
    std::cout << "\n";
}

int main(int argc, char **argv)
{
    if (argc < 2) {
        std::cerr << "usage: muninn_media_decoder_probe <wire.bin> [more-wire.bin ...]\n"
                  << "       muninn_media_decoder_probe --feedback-fixture\n"
                  << "       muninn_media_decoder_probe --feedback-fixture-hex\n"
                  << "       muninn_media_decoder_probe --rudp-url <url>\n";
        return 2;
    }

    std::map<std::string, MuninnVideoFrameAssembly> video_assemblies;
    bool decoded_any = false;
    for (int index = 1; index < argc; ++index) {
        std::vector<uint8_t> bytes;
        if (std::string(argv[index]) == "--feedback-fixture") {
            bytes = muninn_media_wire::encode_receiver_feedback_payload(
                "muninn.raven.av.rudp", "raven:test:video", 42, {"42:1", "42:3"});
        } else if (std::string(argv[index]) == "--feedback-fixture-hex") {
            bytes = muninn_media_wire::encode_receiver_feedback_payload(
                "muninn.raven.av.rudp", "raven:test:video", 42, {"42:1", "42:3"}, "unix-ms:1000");
            for (const auto byte : bytes) {
                std::cout << std::hex << std::setw(2) << std::setfill('0')
                          << static_cast<unsigned>(byte);
            }
            std::cout << "\n";
            decoded_any = true;
            continue;
        } else if (std::string(argv[index]) == "--rudp-url") {
            if (index + 1 >= argc) {
                std::cerr << "--rudp-url requires a URL\n";
                return 2;
            }
            const auto parsed = muninn_rudp_url::parse(argv[++index]);
            if (!parsed) {
                std::cerr << "invalid RUDP URL\n";
                return 1;
            }
            std::cout << "rudp"
                      << " port=" << parsed->port
                      << " video_local_port=" << parsed->video_local_port
                      << " audio_local_port=" << parsed->audio_local_port
                      << " connection=0x" << std::hex << parsed->connection_id << std::dec
                      << " reliable_expire_after_ms=" << parsed->reliable_expire_after_ms
                      << " assembly_deadline_ms=" << parsed->video_assembly_deadline_ms
                      << " gap_wait_ms=" << parsed->gap_wait_ms << "\n";
            decoded_any = true;
            continue;
        } else {
            std::ifstream file(argv[index], std::ios::binary);
            bytes.assign(std::istreambuf_iterator<char>(file), std::istreambuf_iterator<char>());
        }
        const auto decoded = decode_muninn_media_payload(bytes.data(), static_cast<uint32_t>(bytes.size()));
        if (!decoded) {
            std::cout << "none path=" << argv[index] << "\n";
            continue;
        }
        decoded_any = true;
        print_payload(*decoded);
        if (const auto assembled = assemble_video_payload(*decoded, video_assemblies)) {
            std::cout << "assembled_video frame_id=" << decoded->frame_id
                      << " payload_bytes=" << assembled->size() << " first=";
            if (!assembled->empty()) {
                std::cout << static_cast<unsigned>((*assembled)[0]);
            }
            std::cout << "\n";
        }
    }
    return decoded_any ? 0 : 1;
}
