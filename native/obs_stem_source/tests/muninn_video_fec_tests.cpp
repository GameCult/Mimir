#include "muninn_video_fec.h"

#include <chrono>
#include <cstdlib>
#include <cstdint>
#include <map>
#include <vector>

void require(bool condition)
{
    if (!condition)
        std::abort();
}

int main()
{
    constexpr uint16_t data_count = 64;
    constexpr uint16_t parity_count = 16;
    constexpr size_t shard_bytes = 1400;
    std::vector<std::vector<uint8_t>> original(data_count, std::vector<uint8_t>(shard_bytes));
    for (uint16_t data = 0; data < data_count; ++data) {
        for (size_t offset = 0; offset < shard_bytes; ++offset)
            original[data][offset] = static_cast<uint8_t>((data * 29 + offset * 17) & 0xff);
    }
    std::map<uint16_t, std::vector<uint8_t>> parity;
    for (uint16_t row = 0; row < parity_count; ++row) {
        auto &shard = parity[row];
        shard.assign(shard_bytes, 0);
        for (uint16_t data = 0; data < data_count; ++data) {
            const uint8_t coefficient = muninn_video_fec::coefficient(row, parity_count, data);
            for (size_t offset = 0; offset < shard_bytes; ++offset)
                shard[offset] ^= muninn_video_fec::multiply(original[data][offset], coefficient);
        }
    }
    const std::vector<uint32_t> lengths(data_count, shard_bytes);
    for (uint16_t burst = 1; burst <= parity_count; ++burst) {
        auto damaged = original;
        for (uint16_t index = 0; index < burst; ++index)
            damaged[20 + index].clear();
        const auto started = std::chrono::steady_clock::now();
        require(muninn_video_fec::recover(damaged, lengths, parity_count, parity));
        require(std::chrono::steady_clock::now() - started < std::chrono::milliseconds(50));
        require(damaged == original);
    }
    auto excessive = original;
    for (uint16_t index = 0; index <= parity_count; ++index)
        excessive[index].clear();
    require(!muninn_video_fec::recover(excessive, lengths, parity_count, parity));

    constexpr uint16_t block_data = 8;
    constexpr uint16_t frame_data = 80;
    constexpr uint16_t block_count = frame_data / block_data;
    constexpr size_t block_shard_bytes = 96;
    std::vector<std::vector<uint8_t>> frame(frame_data, std::vector<uint8_t>(block_shard_bytes));
    for (uint16_t data = 0; data < frame_data; ++data) {
        for (size_t offset = 0; offset < block_shard_bytes; ++offset)
            frame[data][offset] = static_cast<uint8_t>((data * 31 + offset * 13) & 0xff);
    }
    std::vector<std::map<uint16_t, std::vector<uint8_t>>> block_parity(block_count);
    for (uint16_t block = 0; block < block_count; ++block) {
        for (uint16_t row = 0; row < block_data; ++row) {
            auto &shard = block_parity[block][row];
            shard.assign(block_shard_bytes, 0);
            for (uint16_t local = 0; local < block_data; ++local) {
                const auto &data = frame[block * block_data + local];
                const uint8_t coefficient = muninn_video_fec::coefficient(row, block_data, local);
                for (size_t offset = 0; offset < block_shard_bytes; ++offset)
                    shard[offset] ^= muninn_video_fec::multiply(data[offset], coefficient);
            }
        }
    }
    struct WireShard {
        bool parity;
        uint16_t block;
        uint16_t local;
    };
    std::vector<WireShard> wire;
    for (uint16_t local = 0; local < block_data; ++local) {
        for (uint16_t block = 0; block < block_count; ++block)
            wire.push_back({false, block, local});
        for (uint16_t block = 0; block < block_count; ++block)
            wire.push_back({true, block, local});
    }
    require(wire.size() == frame_data * 2);
    const std::vector<uint32_t> block_lengths(block_data, block_shard_bytes);
    for (size_t burst = 1; burst <= 54; ++burst) {
        for (size_t start = 0; start + burst <= wire.size(); ++start) {
            auto damaged = frame;
            auto available_parity = block_parity;
            for (size_t index = start; index < start + burst; ++index) {
                const auto shard = wire[index];
                if (shard.parity)
                    available_parity[shard.block].erase(shard.local);
                else
                    damaged[shard.block * block_data + shard.local].clear();
            }
            const auto started = std::chrono::steady_clock::now();
            for (uint16_t block = 0; block < block_count; ++block) {
                require(muninn_video_fec::recover_block(
                    damaged,
                    block * block_data,
                    block_lengths,
                    block_data,
                    available_parity[block]));
            }
            require(std::chrono::steady_clock::now() - started < std::chrono::milliseconds(50));
            require(damaged == frame);
        }
    }
    return 0;
}
