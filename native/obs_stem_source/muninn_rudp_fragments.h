#pragma once

#include <algorithm>
#include <chrono>
#include <cstdint>
#include <map>
#include <optional>
#include <string>
#include <utility>
#include <vector>

namespace muninn_rudp_fragments {

struct FragmentResult {
    uint32_t first_sequence = 0;
    std::vector<uint8_t> payload;
};

class FragmentAssembler {
public:
    void reset()
    {
        assemblies_.clear();
        expired_count_ = 0;
    }

    std::optional<FragmentResult> insert(const std::string &channel_id,
                                         uint16_t fragment_id,
                                         uint16_t fragment_index,
                                         uint16_t fragment_count,
                                         uint32_t sequence,
                                         const uint8_t *payload,
                                         uint32_t payload_len,
                                         std::chrono::steady_clock::time_point now)
    {
        if (fragment_id == 0 || fragment_index >= fragment_count) {
            return std::nullopt;
        }

        const std::string key = channel_id + ":" + std::to_string(fragment_id);
        auto &assembly = assemblies_[key];
        if (assembly.payloads.empty()) {
            assembly.channel_id = channel_id;
            assembly.fragment_count = fragment_count;
            assembly.started_at = now;
        }
        if (assembly.channel_id != channel_id || assembly.fragment_count != fragment_count) {
            assemblies_.erase(key);
            return std::nullopt;
        }

        assembly.payloads.emplace(fragment_index, std::vector<uint8_t>(payload, payload + payload_len));
        assembly.sequences.emplace(fragment_index, sequence);
        if (assembly.payloads.size() < assembly.fragment_count) {
            return std::nullopt;
        }

        FragmentResult result;
        result.first_sequence = UINT32_MAX;
        for (uint16_t index = 0; index < assembly.fragment_count; ++index) {
            const auto payload_found = assembly.payloads.find(index);
            const auto sequence_found = assembly.sequences.find(index);
            if (payload_found == assembly.payloads.end() || sequence_found == assembly.sequences.end()) {
                return std::nullopt;
            }
            result.payload.insert(result.payload.end(), payload_found->second.begin(), payload_found->second.end());
            result.first_sequence = std::min(result.first_sequence, sequence_found->second);
        }
        assemblies_.erase(key);
        return result;
    }

    uint64_t expire(std::chrono::steady_clock::time_point now, uint32_t expire_after_ms)
    {
        uint64_t expired = 0;
        for (auto iterator = assemblies_.begin(); iterator != assemblies_.end();) {
            if (now - iterator->second.started_at < std::chrono::milliseconds(expire_after_ms)) {
                ++iterator;
                continue;
            }
            iterator = assemblies_.erase(iterator);
            ++expired;
        }
        expired_count_ += expired;
        return expired;
    }

    uint64_t expired_count() const { return expired_count_; }

private:
    struct Assembly {
        std::string channel_id;
        uint16_t fragment_count = 0;
        std::map<uint16_t, std::vector<uint8_t>> payloads;
        std::map<uint16_t, uint32_t> sequences;
        std::chrono::steady_clock::time_point started_at;
    };

    std::map<std::string, Assembly> assemblies_;
    uint64_t expired_count_ = 0;
};

} // namespace muninn_rudp_fragments
