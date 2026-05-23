// Sketch only. Native capture handoff ring for one producer and one consumer.

#include <atomic>
#include <array>
#include <cstdint>

struct AudioBlockHandle
{
    std::uint64_t dataHandle;
    std::uint64_t timestampNs;
    std::uint64_t sequence;
    std::uint32_t frameCount;
    std::uint32_t sampleRate;
    std::uint32_t channelCount;
    std::uint32_t format;
};

template <std::size_t CapacityPowerOfTwo>
class SpscAudioBlockRing
{
public:
    static_assert((CapacityPowerOfTwo & (CapacityPowerOfTwo - 1)) == 0);

    bool push(const AudioBlockHandle& block)
    {
        auto head = head_.load(std::memory_order_relaxed);
        auto next = head + 1;
        if (next - tail_.load(std::memory_order_acquire) > CapacityPowerOfTwo)
        {
            return false;
        }

        blocks_[head & (CapacityPowerOfTwo - 1)] = block;
        head_.store(next, std::memory_order_release);
        return true;
    }

    bool pop(AudioBlockHandle& block)
    {
        auto tail = tail_.load(std::memory_order_relaxed);
        if (tail == head_.load(std::memory_order_acquire))
        {
            return false;
        }

        block = blocks_[tail & (CapacityPowerOfTwo - 1)];
        tail_.store(tail + 1, std::memory_order_release);
        return true;
    }

private:
    alignas(64) std::atomic<std::uint64_t> head_{0};
    alignas(64) std::atomic<std::uint64_t> tail_{0};
    std::array<AudioBlockHandle, CapacityPowerOfTwo> blocks_{};
};

