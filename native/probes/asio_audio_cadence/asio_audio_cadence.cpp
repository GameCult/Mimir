#include <windows.h>
#include <objbase.h>

#include <atomic>
#include <algorithm>
#include <chrono>
#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <fstream>
#include <thread>
#include <string>
#include <vector>

using ASIOBool = long;
using ASIOError = long;
using ASIOSampleRate = double;
using ASIOSampleType = long;

constexpr ASIOError ASE_OK = 0;
constexpr double Pi = 3.1415926535897932384626433832795;

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
    std::vector<ASIOBufferInfo>* outputBuffers = nullptr;
    std::vector<ASIOChannelInfo>* outputChannels = nullptr;
    long bufferSize = 0;
    long inputCount = 0;
    long outputCount = 0;
    double sampleRate = 0.0;
    double sweepToneSeconds = 0.0;
    double sweepGapSeconds = 0.0;
    double sweepGain = 0.0;
    std::vector<double> sweepFrequencies;
    std::vector<double> sweepEnergy;
    std::vector<double> sweepPeak;
    std::vector<double> sweepSin;
    std::vector<double> sweepCos;
    std::vector<unsigned long long> sweepSamples;
    std::vector<float>* playbackSamples = nullptr;
    double playbackGain = 1.0;
    std::vector<float>* recordedInput = nullptr;
    unsigned long long recordFrames = 0;
    unsigned long long sweepFrame = 0;
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

double readSample(const unsigned char* sample, ASIOSampleType type)
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

void writeSample(unsigned char* sample, ASIOSampleType type, double value)
{
    value = std::clamp(value, -0.98, 0.98);
    switch (type)
    {
    case 16:
        *reinterpret_cast<short*>(sample) = static_cast<short>(value * 32767.0);
        break;
    case 18:
    case 24:
    case 25:
    case 26:
    case 27:
        *reinterpret_cast<int*>(sample) = static_cast<int>(value * 2147483647.0);
        break;
    case 19:
        *reinterpret_cast<float*>(sample) = static_cast<float>(value);
        break;
    case 20:
        *reinterpret_cast<double*>(sample) = value;
        break;
    default:
        break;
    }
}

void processOutput(long doubleBufferIndex)
{
    const bool hasPlayback = g_capture.playbackSamples && !g_capture.playbackSamples->empty();
    const bool hasSweep = !g_capture.sweepFrequencies.empty();
    if (!g_capture.outputBuffers || !g_capture.outputChannels || (!hasPlayback && !hasSweep))
    {
        return;
    }

    const auto toneFrames = static_cast<unsigned long long>(g_capture.sweepToneSeconds * g_capture.sampleRate);
    const auto gapFrames = static_cast<unsigned long long>(g_capture.sweepGapSeconds * g_capture.sampleRate);
    const auto slotFrames = toneFrames + gapFrames;
    if (hasSweep && slotFrames == 0)
    {
        return;
    }

    for (long frame = 0; frame < g_capture.bufferSize; ++frame)
    {
        const auto absoluteFrame = g_capture.sweepFrame + static_cast<unsigned long long>(frame);
        double sampleValue = 0.0;
        if (hasPlayback)
        {
            if (absoluteFrame < g_capture.playbackSamples->size())
            {
                sampleValue = (*g_capture.playbackSamples)[static_cast<size_t>(absoluteFrame)] * g_capture.playbackGain;
            }
        }
        else
        {
            const auto slot = absoluteFrame / slotFrames;
            const auto slotOffset = absoluteFrame % slotFrames;
            if (slot < g_capture.sweepFrequencies.size() && slotOffset < toneFrames)
            {
                const auto fadeFrames = static_cast<unsigned long long>(0.01 * g_capture.sampleRate);
                auto envelope = 1.0;
                if (fadeFrames > 0 && slotOffset < fadeFrames)
                {
                    envelope = static_cast<double>(slotOffset) / static_cast<double>(fadeFrames);
                }
                else if (fadeFrames > 0 && toneFrames - slotOffset < fadeFrames)
                {
                    envelope = static_cast<double>(toneFrames - slotOffset) / static_cast<double>(fadeFrames);
                }

                const auto phase = 2.0 * Pi * g_capture.sweepFrequencies[slot] *
                    (static_cast<double>(slotOffset) / g_capture.sampleRate);
                sampleValue = std::sin(phase) * envelope * g_capture.sweepGain;
            }
        }

        for (size_t channel = 0; channel < g_capture.outputBuffers->size(); ++channel)
        {
            auto& buffer = (*g_capture.outputBuffers)[channel];
            auto& info = (*g_capture.outputChannels)[channel];
            auto* raw = static_cast<unsigned char*>(buffer.buffers[doubleBufferIndex]);
            const auto sampleBytes = bytesPerSample(info.type);
            if (raw && sampleBytes > 0)
            {
                writeSample(raw + frame * sampleBytes, info.type, sampleValue);
            }
        }
    }
}

void processSweepInput(long doubleBufferIndex)
{
    if (!g_capture.buffers || !g_capture.channels || g_capture.sweepFrequencies.empty())
    {
        return;
    }

    const auto toneFrames = static_cast<unsigned long long>(g_capture.sweepToneSeconds * g_capture.sampleRate);
    const auto gapFrames = static_cast<unsigned long long>(g_capture.sweepGapSeconds * g_capture.sampleRate);
    const auto slotFrames = toneFrames + gapFrames;
    if (slotFrames == 0)
    {
        return;
    }

    for (long frame = 0; frame < g_capture.bufferSize; ++frame)
    {
        const auto absoluteFrame = g_capture.sweepFrame + static_cast<unsigned long long>(frame);
        const auto slot = absoluteFrame / slotFrames;
        const auto slotOffset = absoluteFrame % slotFrames;
        if (slot >= g_capture.sweepFrequencies.size() || slotOffset >= toneFrames)
        {
            continue;
        }

        for (size_t channel = 0; channel < static_cast<size_t>(g_capture.inputCount); ++channel)
        {
            auto& buffer = (*g_capture.buffers)[channel];
            auto& info = (*g_capture.channels)[channel];
            auto* raw = static_cast<unsigned char*>(buffer.buffers[doubleBufferIndex]);
            const auto sampleBytes = bytesPerSample(info.type);
            if (!raw || sampleBytes <= 0)
            {
                continue;
            }

            const auto value = readSample(raw + frame * sampleBytes, info.type);
            const auto index = slot * static_cast<size_t>(g_capture.inputCount) + channel;
            const auto phase = 2.0 * Pi * g_capture.sweepFrequencies[slot] *
                (static_cast<double>(slotOffset) / g_capture.sampleRate);
            g_capture.sweepEnergy[index] += value * value;
            g_capture.sweepPeak[index] = std::max(g_capture.sweepPeak[index], std::abs(value));
            g_capture.sweepSin[index] += value * std::sin(phase);
            g_capture.sweepCos[index] += value * std::cos(phase);
            ++g_capture.sweepSamples[index];
        }
    }
}

void bufferSwitch(long doubleBufferIndex, ASIOBool)
{
    g_capture.callbacks.fetch_add(1, std::memory_order_relaxed);
    g_capture.frames.fetch_add(static_cast<unsigned long long>(g_capture.bufferSize), std::memory_order_relaxed);
    processOutput(doubleBufferIndex);
    processSweepInput(doubleBufferIndex);
    g_capture.sweepFrame += static_cast<unsigned long long>(g_capture.bufferSize);
    if (!g_capture.buffers || !g_capture.channels)
    {
        return;
    }

    unsigned long long nonZero = 0;
    for (size_t channel = 0; channel < static_cast<size_t>(g_capture.inputCount); ++channel)
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
            const auto absoluteFrame = g_capture.sweepFrame + static_cast<unsigned long long>(frame);
            if (g_capture.recordedInput && absoluteFrame < g_capture.recordFrames)
            {
                (*g_capture.recordedInput)[static_cast<size_t>(absoluteFrame) * static_cast<size_t>(g_capture.inputCount) + channel] =
                    static_cast<float>(readSample(raw + frame * sampleBytes, info.type));
            }

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

bool hasArg(int argc, char** argv, const char* name)
{
    for (int index = 1; index < argc; ++index)
    {
        if (std::strcmp(argv[index], name) == 0)
        {
            return true;
        }
    }
    return false;
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
    const bool monitorSweep = hasArg(argc, argv, "--monitor-sweep");
    const char* playFloat32MonoPath = option(argc, argv, "--play-f32-mono", nullptr);
    const char* recordFloat32InterleavedPath = option(argc, argv, "--record-f32-interleaved", nullptr);
    const auto playGain = doubleOption(argc, argv, "--play-gain", 1.0);
    const auto sweepGain = doubleOption(argc, argv, "--sweep-gain", 0.03);
    const auto sweepToneSeconds = doubleOption(argc, argv, "--sweep-tone-seconds", 0.55);
    const auto sweepGapSeconds = doubleOption(argc, argv, "--sweep-gap-seconds", 0.20);
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

    const long outputChannelLimit = outputs < 8 ? outputs : 8;
    for (long channel = 0; channel < outputChannelLimit; ++channel)
    {
        ASIOChannelInfo info{};
        info.channel = channel;
        info.isInput = 0;
        asioError = driver->getChannelInfo(&info);
        std::printf(
            "asio output[%ld]: status=%s active=%ld group=%ld type=%s name=\"%s\"\n",
            channel,
            asioErrorName(asioError).c_str(),
            info.isActive,
            info.channelGroup,
            sampleTypeName(info.type).c_str(),
            info.name);
    }

    std::vector<float> playbackSamples;
    if (playFloat32MonoPath)
    {
        std::ifstream input(playFloat32MonoPath, std::ios::binary);
        if (!input)
        {
            std::fprintf(stderr, "failed to open --play-f32-mono file: %s\n", playFloat32MonoPath);
            return 9;
        }

        input.seekg(0, std::ios::end);
        const auto byteCount = input.tellg();
        input.seekg(0, std::ios::beg);
        if (byteCount > 0)
        {
            playbackSamples.resize(static_cast<size_t>(byteCount) / sizeof(float));
            input.read(reinterpret_cast<char*>(playbackSamples.data()), static_cast<std::streamsize>(playbackSamples.size() * sizeof(float)));
        }
        std::printf("asio playback-f32-mono: path=%s samples=%zu gain=%.5f\n", playFloat32MonoPath, playbackSamples.size(), playGain);
    }

    if (captureSeconds > 0.0 || monitorSweep || !playbackSamples.empty())
    {
        std::vector<double> sweepFrequencies;
        if (monitorSweep)
        {
            sweepFrequencies = {8000.0, 10000.0, 12000.0, 14000.0, 16000.0, 18000.0, 20000.0, 22000.0, 24000.0, 28000.0, 32000.0, 40000.0};
        }

        auto runSeconds = monitorSweep
            ? (sweepToneSeconds + sweepGapSeconds) * static_cast<double>(sweepFrequencies.size()) + 0.25
            : captureSeconds;
        if (runSeconds <= 0.0 && !playbackSamples.empty() && currentRate > 0.0)
        {
            runSeconds = static_cast<double>(playbackSamples.size()) / currentRate;
        }

        const bool needsOutput = monitorSweep || !playbackSamples.empty();
        std::vector<ASIOBufferInfo> buffers(static_cast<size_t>(inputs + (needsOutput ? outputs : 0)));
        std::vector<ASIOChannelInfo> channels(static_cast<size_t>(inputs));
        std::vector<ASIOChannelInfo> outputChannels(static_cast<size_t>(needsOutput ? outputs : 0));
        for (long channel = 0; channel < inputs; ++channel)
        {
            buffers[static_cast<size_t>(channel)].isInput = 1;
            buffers[static_cast<size_t>(channel)].channelNum = channel;
            channels[static_cast<size_t>(channel)].channel = channel;
            channels[static_cast<size_t>(channel)].isInput = 1;
            driver->getChannelInfo(&channels[static_cast<size_t>(channel)]);
        }
        if (needsOutput)
        {
            for (long channel = 0; channel < outputs; ++channel)
            {
                const auto bufferIndex = static_cast<size_t>(inputs + channel);
                buffers[bufferIndex].isInput = 0;
                buffers[bufferIndex].channelNum = channel;
                outputChannels[static_cast<size_t>(channel)].channel = channel;
                outputChannels[static_cast<size_t>(channel)].isInput = 0;
                driver->getChannelInfo(&outputChannels[static_cast<size_t>(channel)]);
            }
        }

        ASIOCallbacks callbacks{};
        callbacks.bufferSwitch = bufferSwitch;
        callbacks.sampleRateDidChange = sampleRateDidChange;
        callbacks.asioMessage = asioMessage;
        callbacks.bufferSwitchTimeInfo = bufferSwitchTimeInfo;

        asioError = driver->createBuffers(buffers.data(), static_cast<long>(buffers.size()), preferredSize, &callbacks);
        if (asioError != ASE_OK)
        {
            std::fprintf(stderr, "createBuffers failed: %s\n", asioErrorName(asioError).c_str());
            return 7;
        }

        g_capture.buffers = &buffers;
        g_capture.channels = &channels;
        g_capture.outputBuffers = needsOutput ? reinterpret_cast<std::vector<ASIOBufferInfo>*>(nullptr) : nullptr;
        std::vector<ASIOBufferInfo> outputBuffers;
        if (needsOutput)
        {
            outputBuffers.assign(buffers.begin() + inputs, buffers.end());
            g_capture.outputBuffers = &outputBuffers;
            g_capture.outputChannels = &outputChannels;
        }
        std::vector<float> recordedInput;
        if (recordFloat32InterleavedPath)
        {
            const auto requestedFrames = static_cast<unsigned long long>(std::ceil(runSeconds * currentRate)) + static_cast<unsigned long long>(preferredSize);
            recordedInput.assign(static_cast<size_t>(requestedFrames) * static_cast<size_t>(inputs), 0.0f);
            g_capture.recordedInput = &recordedInput;
            g_capture.recordFrames = requestedFrames;
        }
        g_capture.bufferSize = preferredSize;
        g_capture.inputCount = inputs;
        g_capture.outputCount = outputs;
        g_capture.sampleRate = currentRate;
        g_capture.sweepFrequencies = sweepFrequencies;
        g_capture.sweepToneSeconds = sweepToneSeconds;
        g_capture.sweepGapSeconds = sweepGapSeconds;
        g_capture.sweepGain = sweepGain;
        g_capture.playbackSamples = playbackSamples.empty() ? nullptr : &playbackSamples;
        g_capture.playbackGain = playGain;
        g_capture.sweepFrame = 0;
        g_capture.sweepEnergy.assign(sweepFrequencies.size() * static_cast<size_t>(inputs), 0.0);
        g_capture.sweepPeak.assign(sweepFrequencies.size() * static_cast<size_t>(inputs), 0.0);
        g_capture.sweepSin.assign(sweepFrequencies.size() * static_cast<size_t>(inputs), 0.0);
        g_capture.sweepCos.assign(sweepFrequencies.size() * static_cast<size_t>(inputs), 0.0);
        g_capture.sweepSamples.assign(sweepFrequencies.size() * static_cast<size_t>(inputs), 0);
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
        std::this_thread::sleep_for(std::chrono::duration<double>(runSeconds));
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

        if (monitorSweep)
        {
            std::printf("asio monitor-sweep: sampleRate=%.0f gain=%.5f toneSeconds=%.3f gapSeconds=%.3f\n",
                currentRate,
                sweepGain,
                sweepToneSeconds,
                sweepGapSeconds);
            for (size_t freqIndex = 0; freqIndex < sweepFrequencies.size(); ++freqIndex)
            {
                std::printf("sweep frequency %.0f Hz", sweepFrequencies[freqIndex]);
                for (long channel = 0; channel < inputs; ++channel)
                {
                    const auto index = freqIndex * static_cast<size_t>(inputs) + static_cast<size_t>(channel);
                    const auto count = g_capture.sweepSamples[index];
                    const auto rms = count > 0
                        ? std::sqrt(g_capture.sweepEnergy[index] / static_cast<double>(count))
                        : 0.0;
                    const auto detected = count > 0
                        ? 2.0 * std::sqrt(
                            g_capture.sweepSin[index] * g_capture.sweepSin[index] +
                            g_capture.sweepCos[index] * g_capture.sweepCos[index]) / static_cast<double>(count)
                        : 0.0;
                    std::printf(
                        " ch%ld_tone=%.8f ch%ld_rms=%.8f ch%ld_peak=%.8f",
                        channel,
                        detected,
                        channel,
                        rms,
                        channel,
                        g_capture.sweepPeak[index]);
                }
                std::printf("\n");
            }
        }

        if (recordFloat32InterleavedPath)
        {
            const auto capturedFrames = std::min(
                g_capture.frames.load(std::memory_order_relaxed),
                g_capture.recordFrames);
            std::ofstream output(recordFloat32InterleavedPath, std::ios::binary);
            if (!output)
            {
                std::fprintf(stderr, "failed to open --record-f32-interleaved file: %s\n", recordFloat32InterleavedPath);
                driver->disposeBuffers();
                return 10;
            }

            output.write(
                reinterpret_cast<const char*>(recordedInput.data()),
                static_cast<std::streamsize>(capturedFrames * static_cast<unsigned long long>(inputs) * sizeof(float)));
            std::printf("asio record-f32-interleaved: path=%s frames=%llu channels=%ld sampleRate=%.0f\n",
                recordFloat32InterleavedPath,
                capturedFrames,
                inputs,
                currentRate);
        }

        g_capture.buffers = nullptr;
        g_capture.channels = nullptr;
        g_capture.outputBuffers = nullptr;
        g_capture.outputChannels = nullptr;
        g_capture.playbackSamples = nullptr;
        g_capture.recordedInput = nullptr;
        driver->disposeBuffers();
    }

    return 0;
}
