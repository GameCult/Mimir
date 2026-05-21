#include <atomic>
#include <chrono>
#include <cstdio>
#include <cstdlib>
#include <cstdint>
#include <cstring>
#include <mutex>
#include <thread>
#include <vector>

#include "ps3eye_capi.h"

namespace
{
long long nowNs()
{
    return std::chrono::duration_cast<std::chrono::nanoseconds>(
        std::chrono::steady_clock::now().time_since_epoch()).count();
}

bool hasArg(int argc, char** argv, const char* value)
{
    for (int index = 1; index < argc; ++index)
    {
        if (std::strcmp(argv[index], value) == 0)
        {
            return true;
        }
    }

    return false;
}

struct Stats
{
    int camera = 0;
    int frames = 0;
    long long minDelta = INT64_MAX;
    long long maxDelta = 0;
    long long sumDelta = 0;
    int deltas = 0;
    bool opened = false;
};
}

int main(int argc, char** argv)
{
    std::setvbuf(stdout, nullptr, _IONBF, 0);

    const int width = argc > 1 ? std::atoi(argv[1]) : 320;
    const int height = argc > 2 ? std::atoi(argv[2]) : 240;
    const int fps = argc > 3 ? std::atoi(argv[3]) : 187;
    const double seconds = argc > 4 ? std::atof(argv[4]) : 5.0;
    const bool emitJsonFrames = hasArg(argc, argv, "--emit-json-frames");

    ps3eye_init();
    const int count = ps3eye_count_connected();
    std::printf("ps3eye connected=%d\n", count);
    if (count < 1)
    {
        ps3eye_uninit();
        return 2;
    }

    const int cameras = count < 2 ? count : 2;
    std::vector<ps3eye_t*> eyes(cameras, nullptr);
    for (int index = 0; index < cameras; ++index)
    {
        eyes[index] = ps3eye_open(index, width, height, fps, PS3EYE_FORMAT_BAYER);
        if (!eyes[index])
        {
            std::fprintf(stderr, "ps3eye_open failed camera=%d mode=%dx%d@%d\n", index, width, height, fps);
        }
    }

    std::atomic<bool> start{false};
    std::vector<Stats> stats(cameras);
    std::vector<std::thread> threads;
    std::mutex stdoutMutex;
    long long deadline = 0;

    for (int index = 0; index < cameras; ++index)
    {
        stats[index].camera = index;
        if (!eyes[index])
        {
            continue;
        }

        stats[index].opened = true;
        threads.emplace_back([&, index]() {
            std::vector<unsigned char> frame(static_cast<std::size_t>(width) * static_cast<std::size_t>(height));
            while (!start.load(std::memory_order_acquire))
            {
                std::this_thread::yield();
            }

            long long last = 0;
            while (nowNs() < deadline)
            {
                ps3eye_grab_frame(eyes[index], frame.data());
                const long long timestamp = nowNs();
                if (last != 0)
                {
                    const long long delta = timestamp - last;
                    if (delta < stats[index].minDelta)
                    {
                        stats[index].minDelta = delta;
                    }
                    if (delta > stats[index].maxDelta)
                    {
                        stats[index].maxDelta = delta;
                    }
                    stats[index].sumDelta += delta;
                    ++stats[index].deltas;
                }

                last = timestamp;
                ++stats[index].frames;

                if (emitJsonFrames)
                {
                    std::lock_guard<std::mutex> lock(stdoutMutex);
                    std::printf(
                        "{\"type\":\"video-frame\",\"sourceId\":\"ps3-eye-%d\",\"timestampNs\":%lld,\"sequence\":%d,\"width\":%d,\"height\":%d,\"pixelFormat\":\"Bayer8\",\"strideBytes\":%d,\"byteLength\":%zu}\n",
                        index,
                        timestamp,
                        stats[index].frames,
                        width,
                        height,
                        width,
                        frame.size());
                }
            }
        });
    }

    const long long begin = nowNs();
    deadline = begin + static_cast<long long>(seconds * 1000000000.0);
    start.store(true, std::memory_order_release);
    for (auto& thread : threads)
    {
        thread.join();
    }

    const long long end = nowNs();
    const double elapsed = (end - begin) / 1000000000.0;

    for (int index = 0; index < cameras; ++index)
    {
        if (!stats[index].opened)
        {
            std::printf("camera=%d open=false\n", index);
            continue;
        }

        const double delivered = stats[index].frames / elapsed;
        const double avgDeltaMs = stats[index].deltas
            ? (stats[index].sumDelta / static_cast<double>(stats[index].deltas)) / 1000000.0
            : 0.0;
        std::printf(
            "camera=%d requested=%dx%d@%d elapsed=%.3f frames=%d delivered_fps=%.2f avg_delta_ms=%.3f min_delta_ms=%.3f max_delta_ms=%.3f\n",
            index,
            width,
            height,
            fps,
            elapsed,
            stats[index].frames,
            delivered,
            avgDeltaMs,
            stats[index].minDelta == INT64_MAX ? 0.0 : stats[index].minDelta / 1000000.0,
            stats[index].maxDelta / 1000000.0);
    }

    for (auto* eye : eyes)
    {
        if (eye)
        {
            ps3eye_close(eye);
        }
    }

    ps3eye_uninit();
    return 0;
}
