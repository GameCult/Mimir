#pragma once

#include <chrono>
#include <climits>
#include <cstdint>
#include <string>
#include <vector>

namespace muninn_media_wire {

inline constexpr const char *ReceiverFeedbackSchema = "muninn.media_receiver_feedback.v1";

class MsgpackWriter {
public:
    const std::vector<uint8_t> &bytes() const { return bytes_; }

    void write_raw(const std::vector<uint8_t> &value)
    {
        bytes_.insert(bytes_.end(), value.begin(), value.end());
    }

    void write_nil() { bytes_.push_back(0xc0); }

    void write_bool(bool value) { bytes_.push_back(value ? 0xc3 : 0xc2); }

    void write_u64(uint64_t value)
    {
        if (value <= 0x7f) {
            bytes_.push_back(static_cast<uint8_t>(value));
        } else if (value <= 0xff) {
            bytes_.push_back(0xcc);
            bytes_.push_back(static_cast<uint8_t>(value));
        } else if (value <= 0xffff) {
            bytes_.push_back(0xcd);
            write_be16_value(static_cast<uint16_t>(value));
        } else if (value <= 0xffffffffull) {
            bytes_.push_back(0xce);
            write_be32_value(static_cast<uint32_t>(value));
        } else {
            bytes_.push_back(0xcf);
            write_be64_value(value);
        }
    }

    void write_i64(int64_t value)
    {
        if (value >= 0) {
            write_u64(static_cast<uint64_t>(value));
            return;
        }
        if (value >= -32) {
            bytes_.push_back(static_cast<uint8_t>(0xe0 | (value + 32)));
        } else if (value >= INT8_MIN) {
            bytes_.push_back(0xd0);
            bytes_.push_back(static_cast<uint8_t>(value));
        } else if (value >= INT16_MIN) {
            bytes_.push_back(0xd1);
            write_be16_value(static_cast<uint16_t>(value));
        } else if (value >= INT32_MIN) {
            bytes_.push_back(0xd2);
            write_be32_value(static_cast<uint32_t>(value));
        } else {
            bytes_.push_back(0xd3);
            write_be64_value(static_cast<uint64_t>(value));
        }
    }

    void write_string(const std::string &value)
    {
        const auto length = value.size();
        if (length <= 31) {
            bytes_.push_back(static_cast<uint8_t>(0xa0 | length));
        } else if (length <= 0xff) {
            bytes_.push_back(0xd9);
            bytes_.push_back(static_cast<uint8_t>(length));
        } else if (length <= 0xffff) {
            bytes_.push_back(0xda);
            write_be16_value(static_cast<uint16_t>(length));
        } else {
            bytes_.push_back(0xdb);
            write_be32_value(static_cast<uint32_t>(length));
        }
        bytes_.insert(bytes_.end(), value.begin(), value.end());
    }

    void write_binary(const std::vector<uint8_t> &value)
    {
        const auto length = value.size();
        if (length <= 0xff) {
            bytes_.push_back(0xc4);
            bytes_.push_back(static_cast<uint8_t>(length));
        } else if (length <= 0xffff) {
            bytes_.push_back(0xc5);
            write_be16_value(static_cast<uint16_t>(length));
        } else {
            bytes_.push_back(0xc6);
            write_be32_value(static_cast<uint32_t>(length));
        }
        bytes_.insert(bytes_.end(), value.begin(), value.end());
    }

    void write_array_len(uint32_t length)
    {
        if (length <= 15) {
            bytes_.push_back(static_cast<uint8_t>(0x90 | length));
        } else if (length <= 0xffff) {
            bytes_.push_back(0xdc);
            write_be16_value(static_cast<uint16_t>(length));
        } else {
            bytes_.push_back(0xdd);
            write_be32_value(length);
        }
    }

    void write_map_len(uint32_t length)
    {
        if (length <= 15) {
            bytes_.push_back(static_cast<uint8_t>(0x80 | length));
        } else if (length <= 0xffff) {
            bytes_.push_back(0xde);
            write_be16_value(static_cast<uint16_t>(length));
        } else {
            bytes_.push_back(0xdf);
            write_be32_value(length);
        }
    }

private:
    std::vector<uint8_t> bytes_;

    void write_be16_value(uint16_t value)
    {
        bytes_.push_back(static_cast<uint8_t>((value >> 8) & 0xff));
        bytes_.push_back(static_cast<uint8_t>(value & 0xff));
    }

    void write_be32_value(uint32_t value)
    {
        bytes_.push_back(static_cast<uint8_t>((value >> 24) & 0xff));
        bytes_.push_back(static_cast<uint8_t>((value >> 16) & 0xff));
        bytes_.push_back(static_cast<uint8_t>((value >> 8) & 0xff));
        bytes_.push_back(static_cast<uint8_t>(value & 0xff));
    }

    void write_be64_value(uint64_t value)
    {
        write_be32_value(static_cast<uint32_t>((value >> 32) & 0xffffffffull));
        write_be32_value(static_cast<uint32_t>(value & 0xffffffffull));
    }
};

inline std::string feedback_observed_at()
{
    const auto now = std::chrono::system_clock::now().time_since_epoch();
    const auto millis = std::chrono::duration_cast<std::chrono::milliseconds>(now).count();
    return "unix-ms:" + std::to_string(millis);
}

inline void write_string_array(MsgpackWriter &writer, const std::vector<std::string> &values)
{
    writer.write_array_len(static_cast<uint32_t>(values.size()));
    for (const auto &value : values) {
        writer.write_string(value);
    }
}

inline void write_u64_array(MsgpackWriter &writer, const std::vector<uint64_t> &values)
{
    writer.write_array_len(static_cast<uint32_t>(values.size()));
    for (const auto value : values) {
        writer.write_u64(value);
    }
}

inline std::vector<uint8_t> encode_receiver_feedback_payload(
    const std::string &stream_id,
    const std::string &session_id,
    uint64_t frame_id,
    const std::vector<std::string> &missing_chunk_keys)
{
    MsgpackWriter record;
    record.write_array_len(11);
    record.write_string(stream_id);
    record.write_string(session_id);
    record.write_string("starfire.obs");
    if (frame_id > 0) {
        record.write_u64(frame_id - 1);
    } else {
        record.write_nil();
    }
    write_u64_array(record, {});
    write_u64_array(record, {frame_id});
    record.write_bool(true);
    record.write_i64(0);
    record.write_i64(0);
    record.write_string(feedback_observed_at());
    write_string_array(record, missing_chunk_keys);

    const std::string record_key = stream_id + ":" + session_id + ":feedback:starfire.obs";
    const std::string message_id =
        "muninn-media:" + std::string(ReceiverFeedbackSchema) + ":" + record_key;

    MsgpackWriter document;
    document.write_map_len(9);
    document.write_string("schemaId");
    document.write_string(ReceiverFeedbackSchema);
    document.write_string("recordKey");
    document.write_string(record_key);
    document.write_string("storedAt");
    document.write_string(feedback_observed_at());
    document.write_string("payloadEncoding");
    document.write_string("messagepack");
    document.write_string("payload");
    document.write_binary(record.bytes());
    document.write_string("sourceRuntimeId");
    document.write_string("starfire");
    document.write_string("sourceAgentId");
    document.write_nil();
    document.write_string("sourceRole");
    document.write_string("mimir.obs");
    document.write_string("tags");
    write_string_array(document, {"muninn.media"});

    MsgpackWriter wire;
    wire.write_map_len(3);
    wire.write_string("schemaVersion");
    wire.write_string("cultnet.document_put_raw.v0");
    wire.write_string("messageId");
    wire.write_string(message_id);
    wire.write_string("document");
    wire.write_raw(document.bytes());
    return wire.bytes();
}

} // namespace muninn_media_wire
