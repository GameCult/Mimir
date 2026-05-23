#include <windows.h>
#include <objbase.h>

#include <atomic>
#include <chrono>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <thread>
#include <string>
#include <vector>

using ASIOBool = long;
using ASIOError = long;
using ASIOSampleRate = double;
using ASIOSampleType = long;

constexpr ASIOError ASE_OK = 0;

struct ASIODriverInfo
{
    long asioVersion;
    long driverVersion;
    char name[32];
    char errorMessage[124];
    void* sysRef;
};

struct ASIOClockSource
{
    long index;
    long associatedChannel;
    long associatedGroup;
    ASIOBool isCurrentSource;
    char name[32];
};

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

struct CaptureState
{
    std::vector<ASIOBufferInfo>* buffers = nullptr;
    std::vector<ASIOChannelInfo>* channels = nullptr;
    long bufferSize = 0;
    std::atomic<unsigned long long> callbacks{0};
    std::atomic<unsigned long long> frames{0};
    std::atomic<unsigned long long> nonZeroSamples{0};
};

CaptureState g_capture;

int bytesPerSample(ASIOSampleType type)
{
    switch (type)
    {
    case 16: return 2;
    case 17: return 3;
    case 18: return 4;
    case 19: return 4;
    case 20: return 8;
    case 24: return 4;
    case 25: return 4;
    case 26: return 4;
    case 27: return 4;
    default: return 0;
    }
}

bool sampleIsNonZero(const unsigned char* sample, int byteCount)
{
    for (int index = 0; index < byteCount; ++index)
    {
        if (sample[index] != 0)
        {
            return true;
        }
    }
    return false;
}

void bufferSwitch(long doubleBufferIndex, ASIOBool)
{
    g_capture.callbacks.fetch_add(1, std::memory_order_relaxed);
    g_capture.frames.fetch_add(static_cast<unsigned long long>(g_capture.bufferSize), std::memory_order_relaxed);
    if (!g_capture.buffers || !g_capture.channels)
    {
        return;
    }

    unsigned long long nonZero = 0;
    for (size_t channel = 0; channel < g_capture.buffers->size(); ++channel)
    {
        auto& buffer = (*g_capture.buffers)[channel];
        auto& info = (*g_capture.channels)[channel];
        auto* raw = static_cast<unsigned char*>(buffer.buffers[doubleBufferIndex]);
        const auto sampleBytes = bytesPerSample(info.type);
        if (!raw || sampleBytes <= 0)
        {
            continue;
        }

        for (long frame = 0; frame < g_capture.bufferSize; ++frame)
        {
            if (sampleIsNonZero(raw + frame * sampleBytes, sampleBytes))
            {
                ++nonZero;
            }
        }
    }
    g_capture.nonZeroSamples.fetch_add(nonZero, std::memory_order_relaxed);
}

void sampleRateDidChange(ASIOSampleRate) {}

long asioMessage(long, long, void*, double*)
{
    return 0;
}

ASIOTime* bufferSwitchTimeInfo(ASIOTime* params, long doubleBufferIndex, ASIOBool directProcess)
{
    bufferSwitch(doubleBufferIndex, directProcess);
    return params;
}

struct ComInit
{
    HRESULT hr;
    ComInit() : hr(CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED)) {}
    ~ComInit()
    {
        if (SUCCEEDED(hr))
        {
            CoUninitialize();
        }
    }
};

template <typename T>
struct ComPtr
{
    T* value = nullptr;
    ~ComPtr()
    {
        if (value)
        {
            value->Release();
        }
    }
    T** operator&() { return &value; }
    T* operator->() const { return value; }
    explicit operator bool() const { return value != nullptr; }
};

const char* option(int argc, char** argv, const char* name, const char* fallback)
{
    for (int index = 1; index + 1 < argc; ++index)
    {
        if (std::strcmp(argv[index], name) == 0)
        {
            return argv[index + 1];
        }
    }
    return fallback;
}

double doubleOption(int argc, char** argv, const char* name, double fallback)
{
    const char* raw = option(argc, argv, name, nullptr);
    return raw ? std::atof(raw) : fallback;
}

std::string hresultString(HRESULT hr)
{
    char buffer[32]{};
    std::snprintf(buffer, sizeof(buffer), "0x%08lx", static_cast<unsigned long>(hr));
    return buffer;
}

std::string asioErrorName(ASIOError error)
{
    switch (error)
    {
    case 0: return "ASE_OK";
    case 1: return "ASE_NotPresent";
    case 2: return "ASE_HWMalfunction";
    case 3: return "ASE_InvalidParameter";
    case 4: return "ASE_InvalidMode";
    case 5: return "ASE_SPNotAdvancing";
    case 6: return "ASE_NoClock";
    case 7: return "ASE_NoMemory";
    default:
        char buffer[48]{};
        std::snprintf(buffer, sizeof(buffer), "ASIOError(%ld)", error);
        return buffer;
    }
}

std::string sampleTypeName(ASIOSampleType type)
{
    switch (type)
    {
    case 16: return "Int16LSB";
    case 17: return "Int24LSB";
    case 18: return "Int32LSB";
    case 19: return "Float32LSB";
    case 20: return "Float64LSB";
    case 24: return "Int32LSB16";
    case 25: return "Int32LSB18";
    case 26: return "Int32LSB20";
    case 27: return "Int32LSB24";
    default:
        char buffer[48]{};
        std::snprintf(buffer, sizeof(buffer), "ASIOSampleType(%ld)", type);
        return buffer;
    }
}

CLSID clsidFromString(const char* raw)
{
    wchar_t wide[64]{};
    MultiByteToWideChar(CP_UTF8, 0, raw, -1, wide, static_cast<int>(std::size(wide)));
    CLSID clsid{};
    CLSIDFromString(wide, &clsid);
    return clsid;
}

int main(int argc, char** argv)
{
    const char* clsidText = option(argc, argv, "--clsid", "{AC4D0455-50D7-4498-B3CD-9A41D130B759}");
    const char* driverLabel = option(argc, argv, "--driver", "Focusrite USB ASIO");
    const auto captureSeconds = doubleOption(argc, argv, "--capture-seconds", 0.0);
    const auto setSampleRate = doubleOption(argc, argv, "--set-sample-rate", 0.0);
    const auto clsid = clsidFromString(clsidText);

    ComInit com;
    if (FAILED(com.hr))
    {
        std::fprintf(stderr, "CoInitializeEx failed: %s\n", hresultString(com.hr).c_str());
        return 2;
    }

    ComPtr<IASIO> driver;
    const HRESULT hr = CoCreateInstance(clsid, nullptr, CLSCTX_INPROC_SERVER, clsid, reinterpret_cast<void**>(&driver.value));
    if (FAILED(hr) || !driver)
    {
        std::fprintf(stderr, "CoCreateInstance failed for %s %s: %s\n", driverLabel, clsidText, hresultString(hr).c_str());
        return 3;
    }

    HWND hwnd = GetConsoleWindow();
    if (!hwnd)
    {
        hwnd = GetDesktopWindow();
    }

    if (!driver->init(hwnd))
    {
        char error[124]{};
        driver->getErrorMessage(error);
        std::fprintf(stderr, "ASIO init failed for %s: %s\n", driverLabel, error);
        return 4;
    }

    char name[32]{};
    char error[124]{};
    driver->getDriverName(name);
    driver->getErrorMessage(error);

    long inputs = 0;
    long outputs = 0;
    auto asioError = driver->getChannels(&inputs, &outputs);
    if (asioError != ASE_OK)
    {
        std::fprintf(stderr, "getChannels failed: %s\n", asioErrorName(asioError).c_str());
        return 5;
    }

    long minSize = 0;
    long maxSize = 0;
    long preferredSize = 0;
    long granularity = 0;
    asioError = driver->getBufferSize(&minSize, &maxSize, &preferredSize, &granularity);
    if (asioError != ASE_OK)
    {
        std::fprintf(stderr, "getBufferSize failed: %s\n", asioErrorName(asioError).c_str());
        return 6;
    }

    ASIOSampleRate currentRate = 0.0;
    asioError = driver->getSampleRate(&currentRate);
    if (asioError != ASE_OK)
    {
        std::fprintf(stderr, "getSampleRate failed: %s\n", asioErrorName(asioError).c_str());
    }

    if (setSampleRate > 0.0)
    {
        asioError = driver->setSampleRate(setSampleRate);
        std::printf("asio setSampleRate %.0f: %s\n", setSampleRate, asioErrorName(asioError).c_str());
        if (asioError == ASE_OK)
        {
            driver->getSampleRate(&currentRate);
        }
    }

    long inputLatency = 0;
    long outputLatency = 0;
    asioError = driver->getLatencies(&inputLatency, &outputLatency);

    std::printf(
        "asio driver: requested=\"%s\" reported=\"%s\" version=%ld inputs=%ld outputs=%ld currentRate=%.0f buffer[min=%ld max=%ld preferred=%ld granularity=%ld] latency[input=%ld output=%ld status=%s]\n",
        driverLabel,
        name,
        driver->getDriverVersion(),
        inputs,
        outputs,
        currentRate,
        minSize,
        maxSize,
        preferredSize,
        granularity,
        inputLatency,
        outputLatency,
        asioErrorName(asioError).c_str());

    constexpr ASIOSampleRate rates[] = {44100.0, 48000.0, 88200.0, 96000.0, 176400.0, 192000.0};
    for (const auto rate : rates)
    {
        const auto result = driver->canSampleRate(rate);
        std::printf("asio canSampleRate %.0f: %s\n", rate, asioErrorName(result).c_str());
    }

    const long channelLimit = inputs < 8 ? inputs : 8;
    for (long channel = 0; channel < channelLimit; ++channel)
    {
        ASIOChannelInfo info{};
        info.channel = channel;
        info.isInput = 1;
        asioError = driver->getChannelInfo(&info);
        std::printf(
            "asio input[%ld]: status=%s active=%ld group=%ld type=%s name=\"%s\"\n",
            channel,
            asioErrorName(asioError).c_str(),
            info.isActive,
            info.channelGroup,
            sampleTypeName(info.type).c_str(),
            info.name);
    }

    if (captureSeconds > 0.0)
    {
        std::vector<ASIOBufferInfo> buffers(static_cast<size_t>(inputs));
        std::vector<ASIOChannelInfo> channels(static_cast<size_t>(inputs));
        for (long channel = 0; channel < inputs; ++channel)
        {
            buffers[static_cast<size_t>(channel)].isInput = 1;
            buffers[static_cast<size_t>(channel)].channelNum = channel;
            channels[static_cast<size_t>(channel)].channel = channel;
            channels[static_cast<size_t>(channel)].isInput = 1;
            driver->getChannelInfo(&channels[static_cast<size_t>(channel)]);
        }

        ASIOCallbacks callbacks{};
        callbacks.bufferSwitch = bufferSwitch;
        callbacks.sampleRateDidChange = sampleRateDidChange;
        callbacks.asioMessage = asioMessage;
        callbacks.bufferSwitchTimeInfo = bufferSwitchTimeInfo;

        asioError = driver->createBuffers(buffers.data(), inputs, preferredSize, &callbacks);
        if (asioError != ASE_OK)
        {
            std::fprintf(stderr, "createBuffers failed: %s\n", asioErrorName(asioError).c_str());
            return 7;
        }

        g_capture.buffers = &buffers;
        g_capture.channels = &channels;
        g_capture.bufferSize = preferredSize;
        g_capture.callbacks.store(0, std::memory_order_relaxed);
        g_capture.frames.store(0, std::memory_order_relaxed);
        g_capture.nonZeroSamples.store(0, std::memory_order_relaxed);

        asioError = driver->start();
        if (asioError != ASE_OK)
        {
            std::fprintf(stderr, "start failed: %s\n", asioErrorName(asioError).c_str());
            driver->disposeBuffers();
            return 8;
        }

        const auto captureStart = std::chrono::steady_clock::now();
        std::this_thread::sleep_for(std::chrono::duration<double>(captureSeconds));
        driver->stop();
        const auto captureStop = std::chrono::steady_clock::now();
        const auto elapsed = std::chrono::duration<double>(captureStop - captureStart).count();

        const auto callbackCount = g_capture.callbacks.load(std::memory_order_relaxed);
        const auto frameCount = g_capture.frames.load(std::memory_order_relaxed);
        const auto nonZeroSamples = g_capture.nonZeroSamples.load(std::memory_order_relaxed);
        std::printf(
            "asio capture: requestedSeconds=%.3f elapsed=%.6f callbacks=%llu frames=%llu estimatedRate=%.3f nonZeroSamples=%llu channels=%ld bufferSize=%ld\n",
            captureSeconds,
            elapsed,
            callbackCount,
            frameCount,
            elapsed > 0.0 ? static_cast<double>(frameCount) / elapsed : 0.0,
            nonZeroSamples,
            inputs,
            preferredSize);

        g_capture.buffers = nullptr;
        g_capture.channels = nullptr;
        driver->disposeBuffers();
    }

    return 0;
}
