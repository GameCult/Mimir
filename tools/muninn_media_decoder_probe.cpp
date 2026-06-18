#include <cstdint>
#include <fstream>
#include <iostream>
#include <optional>
#include <stdexcept>
#include <string>
#include <vector>

struct MuninnMediaPayload {
    enum class Kind { Unknown, Video, Audio };
    Kind kind = Kind::Unknown;
    std::vector<uint8_t> payload;
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
        for (uint32_t index = 0; index < 13; ++index) record.skip();
        return MuninnMediaPayload{MuninnMediaPayload::Kind::Video, record.read_bytes()};
    }
    if (schema_id == "muninn.media_audio_packet.v1") {
        if (record_len < 10) return std::nullopt;
        for (uint32_t index = 0; index < 9; ++index) record.skip();
        return MuninnMediaPayload{MuninnMediaPayload::Kind::Audio, record.read_bytes()};
    }
    return std::nullopt;
}

int main(int argc, char **argv)
{
    if (argc != 2) {
        std::cerr << "usage: muninn_media_decoder_probe <wire.bin>\n";
        return 2;
    }
    std::ifstream file(argv[1], std::ios::binary);
    std::vector<uint8_t> bytes((std::istreambuf_iterator<char>(file)), std::istreambuf_iterator<char>());
    const auto decoded = decode_muninn_media_payload(bytes.data(), static_cast<uint32_t>(bytes.size()));
    if (!decoded) {
        std::cout << "none\n";
        return 1;
    }
    std::cout << (decoded->kind == MuninnMediaPayload::Kind::Video ? "video" : "audio")
              << " payload_bytes=" << decoded->payload.size() << " first=";
    if (!decoded->payload.empty()) {
        std::cout << static_cast<unsigned>(decoded->payload[0]);
    }
    std::cout << "\n";
    return 0;
}
