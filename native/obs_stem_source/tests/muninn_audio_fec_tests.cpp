#include "muninn_audio_fec.h"

#include <cstdlib>
#include <iostream>
#include <stdexcept>

static void require(bool condition, const char *message) { if (!condition) throw std::runtime_error(message); }

static std::array<muninn_audio_fec::Bytes, 4> fixture()
{
    std::array<muninn_audio_fec::Bytes, 4> data;
    for (size_t shard = 0; shard < data.size(); ++shard) {
        data[shard].resize(257);
        for (size_t i = 0; i < data[shard].size(); ++i) data[shard][i] = uint8_t((shard * 71 + i * 29) & 0xff);
    }
    return data;
}

static muninn_audio_fec::Shards all_shards(const std::array<muninn_audio_fec::Bytes, 4> &data)
{
    auto parity = muninn_audio_fec::encode(data);
    return {data[0], data[1], data[2], data[3], parity[0], parity[1]};
}

int main()
{
    try {
        const auto data = fixture();
        for (size_t erased = 0; erased < 6; ++erased) {
            auto shards = all_shards(data); shards[erased].reset();
            require(muninn_audio_fec::recover(shards), "one-erasure recovery failed");
            for (size_t i = 0; i < 4; ++i) require(shards[i] == data[i], "one-erasure data mismatch");
        }
        for (size_t first = 0; first < 6; ++first) for (size_t second = first + 1; second < 6; ++second) {
            auto shards = all_shards(data); shards[first].reset(); shards[second].reset();
            require(muninn_audio_fec::recover(shards), "two-erasure recovery failed");
            for (size_t i = 0; i < 4; ++i) require(shards[i] == data[i], "two-erasure data mismatch");
        }
        {
            auto shards = all_shards(data);
            shards[0].reset(); shards[1].reset(); shards[2].reset();
            require(!muninn_audio_fec::recover(shards), "three erasures were incorrectly recoverable");
        }
        {
            auto shards = all_shards(data);
            shards[5]->pop_back();
            require(!muninn_audio_fec::recover(shards), "unequal shard sizes were accepted");
        }

        using namespace muninn_audio_fec;
        BlockReceiver receiver(std::chrono::milliseconds(10), 2);
        BlockKey key{"stream", "session", 100};
        auto shards = all_shards(data);
        auto t0 = BlockReceiver::Clock::time_point{};
        require(receiver.insert(key, 5, *shards[5], t0).empty(), "early parity emitted data");
        require(receiver.insert(key, 2, *shards[2], t0).empty(), "partial reorder emitted data");
        require(receiver.insert(key, 2, *shards[2], t0).empty(), "duplicate emitted data");
        require(receiver.insert(key, 1, *shards[1], t0).empty(), "partial block emitted data");
        auto recovered = receiver.insert(key, 4, *shards[4], t0);
        require(recovered.size() == 2 && recovered[0].packet_id == 100 && recovered[1].packet_id == 103,
                "reordered block did not recover missing packet ids");
        require(recovered[0].payload == data[0] && recovered[1].payload == data[3], "receiver recovery mismatch");
        require(receiver.insert(key, 0, data[0], t0).empty(), "completed-block duplicate was admitted");

        BlockKey expired{"stream", "session", 200};
        receiver.insert(expired, 0, data[0], t0);
        receiver.expire(t0 + std::chrono::milliseconds(10));
        require(receiver.pending_blocks() == 0, "expired block survived deadline");

        BlockReceiver bounded(std::chrono::seconds(1), 2);
        bounded.insert({"s", "x", 0}, 0, data[0], t0);
        bounded.insert({"s", "x", 4}, 0, data[0], t0);
        bounded.insert({"s", "x", 8}, 0, data[0], t0);
        require(bounded.pending_blocks() == 2, "block bound was not enforced");
    } catch (const std::exception &error) {
        std::cerr << error.what() << '\n'; return EXIT_FAILURE;
    }
    return EXIT_SUCCESS;
}
