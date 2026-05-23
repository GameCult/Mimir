#include <windows.h>
#include <objbase.h>

#include <algorithm>
#include <atomic>
#include <condition_variable>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <deque>
#include <mutex>
#include <string>
#include <vector>

using ASIOBool = long;
using ASIOError = long;
using ASIOSampleRate = double;
using ASIOSampleType = long;

constexpr ASIOError ASE_OK = 0;

struct ASIOSamples
{
    unsigned long hi;
    unsigned long lo;
};

struct ASIOTimeStamp
{
    unsigned long hi;
    unsigned long lo;
};

struct ASIOClockSource
{
    long index;
    long associatedChannel;
    long associatedGroup;
    ASIOBool isCurrentSource;
    char name[32];
};

struct ASIOChannelInfo
{
    long channel;
    ASIOBool isInput;
    ASIOBool isActive;
    long channelGroup;
    ASIOSampleType type;
    char name[32];
};

struct ASIOBufferInfo
{
    ASIOBool isInput;
    long channelNum;
    void* buffers[2];
};

struct ASIOTime;

struct ASIOCallbacks
{
    void (*bufferSwitch)(long doubleBufferIndex, ASIOBool directProcess);
    void (*sampleRateDidChange)(ASIOSampleRate sRate);
    long (*asioMessage)(long selector, long value, void* message, double* opt);
    ASIOTime* (*bufferSwitchTimeInfo)(ASIOTime* params, long doubleBufferIndex, ASIOBool directProcess);
};

struct IASIO : public IUnknown
{
    virtual ASIOBool init(void* sysHandle) = 0;
    virtual void getDriverName(char* name) = 0;
    virtual long getDriverVersion() = 0;
    virtual void getErrorMessage(char* string) = 0;
    virtual ASIOError start() = 0;
    virtual ASIOError stop() = 0;
    virtual ASIOError getChannels(long* numInputChannels, long* numOutputChannels) = 0;
    virtual ASIOError getLatencies(long* inputLatency, long* outputLatency) = 0;
    virtual ASIOError getBufferSize(long* minSize, long* maxSize, long* preferredSize, long* granularity) = 0;
    virtual ASIOError canSampleRate(ASIOSampleRate sampleRate) = 0;
    virtual ASIOError getSampleRate(ASIOSampleRate* sampleRate) = 0;
    virtual ASIOError setSampleRate(ASIOSampleRate sampleRate) = 0;
    virtual ASIOError getClockSources(ASIOClockSource* clocks, long* numSources) = 0;
    virtual ASIOError setClockSource(long reference) = 0;
    virtual ASIOError getSamplePosition(ASIOSamples* samplePosition, ASIOTimeStamp* timeStamp) = 0;
    virtual ASIOError getChannelInfo(ASIOChannelInfo* info) = 0;
    virtual ASIOError createBuffers(ASIOBufferInfo* bufferInfos, long numChannels, long bufferSize, ASIOCallbacks* callbacks) = 0;
    virtual ASIOError disposeBuffers() = 0;
    virtual ASIOError controlPanel() = 0;
    virtual ASIOError future(long selector, void* opt) = 0;
    virtual ASIOError outputReady() = 0;
};

struct MimirAsioBlock
{
    int channel = 0;
    int sampleRate = 0;
    int frameCount = 0;
    std::uint64_t sequence = 0;
    std::int64_t timestampNs = 0;
    std::vector<float> samples;
};

struct MimirAsioCapture
{
    IASIO* driver = nullptr;
    ASIOCallbacks callbacks{};
    std::vector<ASIOBufferInfo> buffers;
    std::vector<ASIOChannelInfo> channels;
    std::deque<MimirAsioBlock> queue;
    std::mutex mutex;
    long inputs = 0;
    long outputs = 0;
    long bufferSize = 0;
    double sampleRate = 0.0;
    std::uint64_t frameCursor = 0;
    std::uint64_t sequence = 0;
    bool running = false;
};

static MimirAsioCapture* g_capture = nullptr;

static int bytesPerSample(ASIOSampleType type)
{
    switch (type)
    {
    case 16: return 2;
    case 17: return 3;
    case 18: return 4;
    case 19: return 4;
    case 20: return 8;
    case 24:
    case 25:
    case 26:
    case 27: return 4;
    default: return 0;
    }
}

static double readSample(const unsigned char* sample, ASIOSampleType type)
{
    switch (type)
    {
    case 16:
        return *reinterpret_cast<const short*>(sample) / 32768.0;
    case 17:
    {
        int value = sample[0] | (sample[1] << 8) | (sample[2] << 16);
        if ((value & 0x00800000) != 0)
        {
            value |= static_cast<int>(0xff000000);
        }
        return value / 8388608.0;
    }
    case 18:
    case 24:
    case 25:
    case 26:
    case 27:
        return *reinterpret_cast<const int*>(sample) / 2147483648.0;
    case 19:
        return *reinterpret_cast<const float*>(sample);
    case 20:
        return *reinterpret_cast<const double*>(sample);
    default:
        return 0.0;
    }
}

static CLSID clsidFromString(const char* raw)
{
    wchar_t wide[64]{};
    MultiByteToWideChar(CP_UTF8, 0, raw, -1, wide, static_cast<int>(std::size(wide)));
    CLSID clsid{};
    CLSIDFromString(wide, &clsid);
    return clsid;
}

static void pushInputBlocks(MimirAsioCapture& capture, long doubleBufferIndex)
{
    const auto startFrame = capture.frameCursor;
    const auto timestampNs = static_cast<std::int64_t>(
        static_cast<double>(startFrame) * 1000000000.0 / capture.sampleRate);
    std::lock_guard<std::mutex> lock(capture.mutex);
    for (long channel = 0; channel < capture.inputs; ++channel)
    {
        auto& buffer = capture.buffers[static_cast<size_t>(channel)];
        auto& info = capture.channels[static_cast<size_t>(channel)];
        auto* raw = static_cast<unsigned char*>(buffer.buffers[doubleBufferIndex]);
        const auto sampleBytes = bytesPerSample(info.type);
        if (!raw || sampleBytes <= 0)
        {
            continue;
        }

        MimirAsioBlock block;
        block.channel = static_cast<int>(channel);
        block.sampleRate = static_cast<int>(capture.sampleRate);
        block.frameCount = static_cast<int>(capture.bufferSize);
        block.sequence = capture.sequence++;
        block.timestampNs = timestampNs;
        block.samples.resize(static_cast<size_t>(capture.bufferSize));
        for (long frame = 0; frame < capture.bufferSize; ++frame)
        {
            block.samples[static_cast<size_t>(frame)] = static_cast<float>(
                readSample(raw + frame * sampleBytes, info.type));
        }

        capture.queue.push_back(std::move(block));
    }

    while (capture.queue.size() > 4096)
    {
        capture.queue.pop_front();
    }

    capture.frameCursor += static_cast<std::uint64_t>(capture.bufferSize);
}

static void bufferSwitch(long doubleBufferIndex, ASIOBool)
{
    if (g_capture && g_capture->running)
    {
        pushInputBlocks(*g_capture, doubleBufferIndex);
    }
}

static void sampleRateDidChange(ASIOSampleRate) {}

static long asioMessage(long, long, void*, double*)
{
    return 0;
}

static ASIOTime* bufferSwitchTimeInfo(ASIOTime* params, long doubleBufferIndex, ASIOBool directProcess)
{
    bufferSwitch(doubleBufferIndex, directProcess);
    return params;
}

extern "C"
{
__declspec(dllexport) MimirAsioCapture* mimir_asio_create(
    const char* clsidText,
    double requestedSampleRate,
    int* sampleRate,
    int* inputCount,
    int* preferredBufferSize)
{
    CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    auto* capture = new MimirAsioCapture();
    const auto clsid = clsidFromString(clsidText ? clsidText : "{AC4D0455-50D7-4498-B3CD-9A41D130B759}");
    const auto hr = CoCreateInstance(clsid, nullptr, CLSCTX_INPROC_SERVER, clsid, reinterpret_cast<void**>(&capture->driver));
    if (FAILED(hr) || !capture->driver)
    {
        delete capture;
        CoUninitialize();
        return nullptr;
    }

    HWND hwnd = GetConsoleWindow();
    if (!hwnd)
    {
        hwnd = GetDesktopWindow();
    }

    if (!capture->driver->init(hwnd))
    {
        capture->driver->Release();
        delete capture;
        CoUninitialize();
        return nullptr;
    }

    capture->driver->getChannels(&capture->inputs, &capture->outputs);
    long minSize = 0;
    long maxSize = 0;
    long granularity = 0;
    capture->driver->getBufferSize(&minSize, &maxSize, &capture->bufferSize, &granularity);
    if (requestedSampleRate > 0.0)
    {
        capture->driver->setSampleRate(requestedSampleRate);
    }

    capture->driver->getSampleRate(&capture->sampleRate);
    capture->buffers.resize(static_cast<size_t>(capture->inputs));
    capture->channels.resize(static_cast<size_t>(capture->inputs));
    for (long channel = 0; channel < capture->inputs; ++channel)
    {
        capture->buffers[static_cast<size_t>(channel)].isInput = 1;
        capture->buffers[static_cast<size_t>(channel)].channelNum = channel;
        capture->channels[static_cast<size_t>(channel)].channel = channel;
        capture->channels[static_cast<size_t>(channel)].isInput = 1;
        capture->driver->getChannelInfo(&capture->channels[static_cast<size_t>(channel)]);
    }

    capture->callbacks.bufferSwitch = bufferSwitch;
    capture->callbacks.sampleRateDidChange = sampleRateDidChange;
    capture->callbacks.asioMessage = asioMessage;
    capture->callbacks.bufferSwitchTimeInfo = bufferSwitchTimeInfo;
    if (capture->driver->createBuffers(capture->buffers.data(), capture->inputs, capture->bufferSize, &capture->callbacks) != ASE_OK)
    {
        capture->driver->Release();
        delete capture;
        CoUninitialize();
        return nullptr;
    }

    if (sampleRate)
    {
        *sampleRate = static_cast<int>(capture->sampleRate);
    }
    if (inputCount)
    {
        *inputCount = static_cast<int>(capture->inputs);
    }
    if (preferredBufferSize)
    {
        *preferredBufferSize = static_cast<int>(capture->bufferSize);
    }

    return capture;
}

__declspec(dllexport) int mimir_asio_start(MimirAsioCapture* capture)
{
    if (!capture || !capture->driver)
    {
        return 0;
    }

    g_capture = capture;
    capture->running = true;
    return capture->driver->start() == ASE_OK ? 1 : 0;
}

__declspec(dllexport) int mimir_asio_read(
    MimirAsioCapture* capture,
    int* channel,
    std::int64_t* timestampNs,
    std::uint64_t* sequence,
    int* frameCount,
    float* samples,
    int maxFrames)
{
    if (!capture || !samples || maxFrames <= 0)
    {
        return 0;
    }

    std::lock_guard<std::mutex> lock(capture->mutex);
    if (capture->queue.empty())
    {
        return 0;
    }

    auto block = std::move(capture->queue.front());
    capture->queue.pop_front();
    const auto count = std::min(block.frameCount, maxFrames);
    std::memcpy(samples, block.samples.data(), static_cast<size_t>(count) * sizeof(float));
    if (channel)
    {
        *channel = block.channel;
    }
    if (timestampNs)
    {
        *timestampNs = block.timestampNs;
    }
    if (sequence)
    {
        *sequence = block.sequence;
    }
    if (frameCount)
    {
        *frameCount = count;
    }

    return 1;
}

__declspec(dllexport) void mimir_asio_destroy(MimirAsioCapture* capture)
{
    if (!capture)
    {
        return;
    }

    capture->running = false;
    if (g_capture == capture)
    {
        g_capture = nullptr;
    }

    if (capture->driver)
    {
        capture->driver->stop();
        capture->driver->disposeBuffers();
        capture->driver->Release();
    }

    delete capture;
    CoUninitialize();
}
}
