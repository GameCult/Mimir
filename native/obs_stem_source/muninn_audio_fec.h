#pragma once

#include <array>
#include <chrono>
#include <cstddef>
#include <cstdint>
#include <iterator>
#include <map>
#include <optional>
#include <string>
#include <tuple>
#include <utility>
#include <vector>

namespace muninn_audio_fec {

inline constexpr size_t DataShards = 4;
inline constexpr size_t ParityShards = 2;
inline constexpr size_t TotalShards = DataShards + ParityShards;
using Bytes = std::vector<uint8_t>;
using Shards = std::array<std::optional<Bytes>, TotalShards>;

inline uint8_t multiply(uint8_t left, uint8_t right)
{
    uint16_t product = 0;
    uint16_t a = left;
    uint16_t b = right;
    while (b != 0) {
        if ((b & 1u) != 0) product ^= a;
        b >>= 1u;
        a <<= 1u;
        if ((a & 0x100u) != 0) a ^= 0x11du;
    }
    return static_cast<uint8_t>(product);
}

inline uint8_t power(uint8_t value, uint16_t exponent)
{
    uint8_t result = 1;
    while (exponent != 0) {
        if ((exponent & 1u) != 0) result = multiply(result, value);
        value = multiply(value, value);
        exponent >>= 1u;
    }
    return result;
}

inline uint8_t inverse(uint8_t value)
{
    return value == 0 ? 0 : power(value, 254);
}

inline constexpr std::array<std::array<uint8_t, DataShards>, TotalShards> Generator{{
    {{1, 0, 0, 0}}, {{0, 1, 0, 0}}, {{0, 0, 1, 0}}, {{0, 0, 0, 1}},
    {{1, 1, 1, 1}}, {{1, 2, 4, 8}}
}};

inline std::array<Bytes, ParityShards> encode(const std::array<Bytes, DataShards> &data)
{
    const size_t bytes = data[0].size();
    for (const auto &shard : data) if (shard.size() != bytes) return {};
    std::array<Bytes, ParityShards> parity{Bytes(bytes), Bytes(bytes)};
    for (size_t p = 0; p < ParityShards; ++p)
        for (size_t d = 0; d < DataShards; ++d)
            for (size_t i = 0; i < bytes; ++i)
                parity[p][i] ^= multiply(Generator[DataShards + p][d], data[d][i]);
    return parity;
}

inline bool recover(Shards &shards)
{
    size_t shard_bytes = 0;
    std::array<size_t, DataShards> available{};
    size_t count = 0;
    for (size_t i = 0; i < TotalShards; ++i) {
        if (!shards[i]) continue;
        if (shard_bytes == 0) shard_bytes = shards[i]->size();
        if (shards[i]->size() != shard_bytes) return false;
        if (count < DataShards) available[count++] = i;
    }
    if (count < DataShards || shard_bytes == 0) return false;

    std::array<std::array<uint8_t, DataShards * 2>, DataShards> matrix{};
    for (size_t row = 0; row < DataShards; ++row) {
        for (size_t col = 0; col < DataShards; ++col) matrix[row][col] = Generator[available[row]][col];
        matrix[row][DataShards + row] = 1;
    }
    for (size_t col = 0; col < DataShards; ++col) {
        size_t pivot = col;
        while (pivot < DataShards && matrix[pivot][col] == 0) ++pivot;
        if (pivot == DataShards) return false;
        if (pivot != col) std::swap(matrix[pivot], matrix[col]);
        const uint8_t scale = inverse(matrix[col][col]);
        for (size_t j = 0; j < DataShards * 2; ++j) matrix[col][j] = multiply(matrix[col][j], scale);
        for (size_t row = 0; row < DataShards; ++row) {
            if (row == col) continue;
            const uint8_t factor = matrix[row][col];
            for (size_t j = 0; j < DataShards * 2; ++j)
                matrix[row][j] ^= multiply(factor, matrix[col][j]);
        }
    }
    for (size_t missing = 0; missing < DataShards; ++missing) {
        if (shards[missing]) continue;
        Bytes decoded(shard_bytes);
        for (size_t row = 0; row < DataShards; ++row) {
            const uint8_t coefficient = matrix[missing][DataShards + row];
            for (size_t i = 0; i < shard_bytes; ++i)
                decoded[i] ^= multiply(coefficient, (*shards[available[row]])[i]);
        }
        shards[missing] = std::move(decoded);
    }
    return true;
}

struct BlockKey {
    std::string stream_id;
    std::string session_id;
    uint64_t base_packet_id = 0;
    bool operator<(const BlockKey &other) const
    {
        return std::tie(stream_id, session_id, base_packet_id) <
               std::tie(other.stream_id, other.session_id, other.base_packet_id);
    }
};

struct RecoveredPacket { uint64_t packet_id; Bytes payload; };

class BlockReceiver {
public:
    using Clock = std::chrono::steady_clock;
    explicit BlockReceiver(std::chrono::milliseconds deadline, size_t max_blocks = 128)
        : deadline_(deadline), max_blocks_(max_blocks) {}

    std::vector<RecoveredPacket> insert(const BlockKey &key, size_t shard_index, Bytes payload,
                                        Clock::time_point now)
    {
        expire(now);
        if (shard_index >= TotalShards || payload.empty() || completed_.count(key) != 0) return {};
        if (blocks_.size() >= max_blocks_ && blocks_.count(key) == 0) blocks_.erase(blocks_.begin());
        auto [it, created] = blocks_.try_emplace(key, Block{now, payload.size(), {}});
        Block &block = it->second;
        if (payload.size() != block.shard_bytes || block.shards[shard_index]) return {};
        block.shards[shard_index] = std::move(payload);
        size_t present = 0;
        for (const auto &shard : block.shards) if (shard) ++present;
        if (present < DataShards) return {};
        Shards before = block.shards;
        if (!recover(block.shards)) return {};
        std::vector<RecoveredPacket> result;
        for (size_t i = 0; i < DataShards; ++i)
            if (!before[i] && block.shards[i]) result.push_back({key.base_packet_id + i, *block.shards[i]});
        blocks_.erase(it);
        completed_[key] = now;
        return result;
    }

    void expire(Clock::time_point now)
    {
        for (auto it = blocks_.begin(); it != blocks_.end();)
            it = now - it->second.created_at >= deadline_ ? blocks_.erase(it) : std::next(it);
        for (auto it = completed_.begin(); it != completed_.end();)
            it = now - it->second >= deadline_ ? completed_.erase(it) : std::next(it);
    }
    size_t pending_blocks() const { return blocks_.size(); }
    void clear()
    {
        blocks_.clear();
        completed_.clear();
    }

private:
    struct Block { Clock::time_point created_at; size_t shard_bytes; Shards shards; };
    std::chrono::milliseconds deadline_;
    size_t max_blocks_;
    std::map<BlockKey, Block> blocks_;
    std::map<BlockKey, Clock::time_point> completed_;
};

} // namespace muninn_audio_fec
