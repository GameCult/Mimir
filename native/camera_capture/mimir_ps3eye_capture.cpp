#include <algorithm>
#include <atomic>
#include <chrono>
#include <condition_variable>
#include <cstdint>
#include <cstring>
#include <deque>
#include <mutex>
#include <thread>
#include <vector>

#include "ps3eye_capi.h"

namespace
{
struct MimirPs3EyeFrame
{
    std::vector<unsigned char> data;
    std::int64_t timestampNs = 0;
    std::uint64_t sequence = 0;
};

struct MimirPs3EyeCapture
{
    ps3eye_t* eye = nullptr;
    std::thread worker;
    std::mutex mutex;
    std::condition_variable condition;
    std::deque<MimirPs3EyeFrame> queue;
    std::atomic<bool> running = false;
    int cameraIndex = 0;
    int width = 0;
    int height = 0;
    int fps = 0;
    std::uint64_t sequence = 0;
};

std::mutex g_ps3eye_mutex;
int g_ps3eye_users = 0;

long long nowNs()
{
    return std::chrono::duration_cast<std::chrono::nanoseconds>(
        std::chrono::steady_clock::now().time_since_epoch()).count();
}

void retainPs3Eye()
{
    std::lock_guard<std::mutex> lock(g_ps3eye_mutex);
    if (g_ps3eye_users++ == 0)
    {
        ps3eye_init();
    }
}

void releasePs3Eye()
{
    std::lock_guard<std::mutex> lock(g_ps3eye_mutex);
    if (g_ps3eye_users > 0 && --g_ps3eye_users == 0)
    {
        ps3eye_uninit();
    }
}

void pushFrame(MimirPs3EyeCapture& capture, const std::vector<unsigned char>& source)
{
    MimirPs3EyeFrame frame;
    frame.data = source;
    frame.timestampNs = nowNs();
    frame.sequence = capture.sequence++;

    {
        std::lock_guard<std::mutex> lock(capture.mutex);
        capture.queue.push_back(std::move(frame));
        while (capture.queue.size() > 32)
        {
            capture.queue.pop_front();
        }
    }

    capture.condition.notify_one();
}

void captureThread(MimirPs3EyeCapture* capture)
{
    std::vector<unsigned char> frame(static_cast<std::size_t>(capture->width) * static_cast<std::size_t>(capture->height));
    while (capture->running.load(std::memory_order_acquire))
    {
        ps3eye_grab_frame(capture->eye, frame.data());
        pushFrame(*capture, frame);
    }
}
}

extern "C"
{
__declspec(dllexport) MimirPs3EyeCapture* mimir_ps3eye_create(int cameraIndex, int width, int height, int fps)
{
    if (cameraIndex < 0 || width <= 0 || height <= 0 || fps <= 0)
    {
        return nullptr;
    }

    retainPs3Eye();
    if (cameraIndex >= ps3eye_count_connected())
    {
        releasePs3Eye();
        return nullptr;
    }

    auto* capture = new MimirPs3EyeCapture();
    capture->cameraIndex = cameraIndex;
    capture->width = width;
    capture->height = height;
    capture->fps = fps;
    capture->eye = ps3eye_open(cameraIndex, width, height, fps, PS3EYE_FORMAT_BAYER);
    if (!capture->eye)
    {
        delete capture;
        releasePs3Eye();
        return nullptr;
    }

    return capture;
}

__declspec(dllexport) int mimir_ps3eye_start(MimirPs3EyeCapture* capture)
{
    if (!capture || capture->running.exchange(true))
    {
        return 0;
    }

    capture->worker = std::thread(captureThread, capture);
    return 1;
}

__declspec(dllexport) int mimir_ps3eye_read(
    MimirPs3EyeCapture* capture,
    int* width,
    int* height,
    int* stride,
    std::int64_t* timestampNs,
    std::uint64_t* sequence,
    unsigned char* destination,
    int destinationBytes)
{
    if (!capture || !destination || destinationBytes <= 0)
    {
        return 0;
    }

    MimirPs3EyeFrame frame;
    {
        std::lock_guard<std::mutex> lock(capture->mutex);
        if (capture->queue.empty())
        {
            return 0;
        }

        frame = std::move(capture->queue.front());
        capture->queue.pop_front();
    }

    if (frame.data.size() > static_cast<std::size_t>(destinationBytes))
    {
        return -static_cast<int>(frame.data.size());
    }

    std::memcpy(destination, frame.data.data(), frame.data.size());
    if (width)
    {
        *width = capture->width;
    }
    if (height)
    {
        *height = capture->height;
    }
    if (stride)
    {
        *stride = capture->width;
    }
    if (timestampNs)
    {
        *timestampNs = frame.timestampNs;
    }
    if (sequence)
    {
        *sequence = frame.sequence;
    }

    return static_cast<int>(frame.data.size());
}

__declspec(dllexport) void mimir_ps3eye_destroy(MimirPs3EyeCapture* capture)
{
    if (!capture)
    {
        return;
    }

    capture->running.store(false, std::memory_order_release);
    if (capture->worker.joinable())
    {
        capture->worker.join();
    }
    if (capture->eye)
    {
        ps3eye_close(capture->eye);
    }

    delete capture;
    releasePs3Eye();
}
}
