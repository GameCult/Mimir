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
    return 0;
}
