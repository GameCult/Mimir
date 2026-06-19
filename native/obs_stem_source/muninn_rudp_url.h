#pragma once

#include <algorithm>
#include <cstdint>
#include <optional>
#include <sstream>
#include <string>

namespace muninn_rudp_url {

inline constexpr uint32_t DefaultMediaRudpConnectionId = 0x6d750001;
inline constexpr uint32_t DefaultReliableExpireAfterMs = 75;
inline constexpr uint32_t DefaultVideoAssemblyDeadlineMs = 75;
inline constexpr uint32_t DefaultGapWaitMs = 8;
inline constexpr uint32_t DefaultAudioReorderMs = 1000;
inline constexpr uint32_t DefaultSenderResendDelayMs = 5;
inline constexpr uint32_t MaxLatencyBudgetMs = 2000;

struct RudpUrlParts {
    uint16_t port = 0;
    uint16_t video_local_port = 0;
    uint16_t audio_local_port = 0;
    uint32_t connection_id = DefaultMediaRudpConnectionId;
    uint32_t sender_resend_delay_ms = DefaultSenderResendDelayMs;
    uint32_t reliable_expire_after_ms = DefaultReliableExpireAfterMs;
    uint32_t video_assembly_deadline_ms = DefaultVideoAssemblyDeadlineMs;
    uint32_t gap_wait_ms = DefaultGapWaitMs;
    uint32_t audio_reorder_ms = DefaultAudioReorderMs;
};

inline bool starts_with(const std::string &value, const std::string &prefix)
{
    return value.rfind(prefix, 0) == 0;
}

inline std::optional<uint32_t> parse_u32_hex_or_decimal(const std::string &value)
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

inline std::optional<RudpUrlParts> parse(const std::string &url)
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
                if (const auto parsed = parse_u32_hex_or_decimal(value)) {
                    parts.connection_id = *parsed;
                }
            } else if (key == "local_port" || key == "video_local_port") {
                parts.video_local_port = static_cast<uint16_t>(std::stoul(value));
            } else if (key == "audio_local_port") {
                parts.audio_local_port = static_cast<uint16_t>(std::stoul(value));
            } else if (key == "reliable_expire_after_ms") {
                if (const auto parsed = parse_u32_hex_or_decimal(value)) {
                    parts.reliable_expire_after_ms = std::clamp(*parsed, 1u, MaxLatencyBudgetMs);
                }
            } else if (key == "sender_resend_delay_ms") {
                if (const auto parsed = parse_u32_hex_or_decimal(value)) {
                    parts.sender_resend_delay_ms = std::clamp(*parsed, 1u, 1000u);
                }
            } else if (key == "assembly_deadline_ms") {
                if (const auto parsed = parse_u32_hex_or_decimal(value)) {
                    parts.video_assembly_deadline_ms = std::clamp(*parsed, 1u, MaxLatencyBudgetMs);
                }
            } else if (key == "gap_wait_ms") {
                if (const auto parsed = parse_u32_hex_or_decimal(value)) {
                    parts.gap_wait_ms = std::clamp(*parsed, 0u, 1000u);
                }
            } else if (key == "audio_reorder_ms") {
                if (const auto parsed = parse_u32_hex_or_decimal(value)) {
                    parts.audio_reorder_ms = std::clamp(*parsed, 0u, MaxLatencyBudgetMs);
                }
            }
        }
    }

    return parts;
}

} // namespace muninn_rudp_url
