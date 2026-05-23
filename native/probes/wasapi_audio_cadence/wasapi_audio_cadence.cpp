#include <windows.h>
#include <mmdeviceapi.h>
#include <audioclient.h>
#include <functiondiscoverykeys_devpkey.h>
#include <avrt.h>
#include <ksmedia.h>
#include <wincrypt.h>

#include <chrono>
#include <algorithm>
#include <cctype>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <cwchar>
#include <string>
#include <vector>

namespace
{
struct ComInit
{
    HRESULT hr = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
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

struct PropVariant
{
    PROPVARIANT value{};
    PropVariant() { PropVariantInit(&value); }
    ~PropVariant() { PropVariantClear(&value); }
};

struct WaveFormatHolder
{
    WAVEFORMATEX* value = nullptr;
    ~WaveFormatHolder()
    {
        if (value)
        {
            CoTaskMemFree(value);
        }
    }
};

long long nowNs()
{
    return std::chrono::duration_cast<std::chrono::nanoseconds>(
        std::chrono::steady_clock::now().time_since_epoch()).count();
}

std::wstring widen(const char* value)
{
    if (!value)
    {
        return {};
    }

    const int bytes = MultiByteToWideChar(CP_UTF8, 0, value, -1, nullptr, 0);
    std::wstring result(static_cast<std::size_t>(bytes > 0 ? bytes - 1 : 0), L'\0');
    if (bytes > 1)
    {
        MultiByteToWideChar(CP_UTF8, 0, value, -1, result.data(), bytes);
    }
    return result;
}

std::string narrow(const wchar_t* value)
{
    if (!value)
    {
        return {};
    }

    const int bytes = WideCharToMultiByte(CP_UTF8, 0, value, -1, nullptr, 0, nullptr, nullptr);
    std::string result(static_cast<std::size_t>(bytes > 0 ? bytes - 1 : 0), '\0');
    if (bytes > 1)
    {
        WideCharToMultiByte(CP_UTF8, 0, value, -1, result.data(), bytes, nullptr, nullptr);
    }
    return result;
}

std::string sampleFormat(const WAVEFORMATEX* format)
{
    if (format->wFormatTag == WAVE_FORMAT_IEEE_FLOAT)
    {
        return "Float32";
    }

    if (format->wFormatTag == WAVE_FORMAT_PCM)
    {
        return format->wBitsPerSample == 16 ? "Int16" :
            format->wBitsPerSample == 24 ? "Int24" :
            format->wBitsPerSample == 32 ? "Int32" : "Unknown";
    }

    if (format->wFormatTag == WAVE_FORMAT_EXTENSIBLE &&
        format->cbSize >= (sizeof(WAVEFORMATEXTENSIBLE) - sizeof(WAVEFORMATEX)))
    {
        const auto* extensible = reinterpret_cast<const WAVEFORMATEXTENSIBLE*>(format);
        if (extensible->SubFormat == KSDATAFORMAT_SUBTYPE_IEEE_FLOAT)
        {
            return "Float32";
        }
        if (extensible->SubFormat == KSDATAFORMAT_SUBTYPE_PCM)
        {
            const WORD bits = extensible->Samples.wValidBitsPerSample != 0
                ? extensible->Samples.wValidBitsPerSample
                : format->wBitsPerSample;
            return bits == 16 ? "Int16" : bits == 24 ? "Int24" : bits == 32 ? "Int32" : "Unknown";
        }
    }

    return "Unknown";
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

const char* option(int argc, char** argv, const char* name, const char* fallback)
{
    for (int index = 1; index < argc - 1; ++index)
    {
        if (std::strcmp(argv[index], name) == 0)
        {
            return argv[index + 1];
        }
    }

    return fallback;
}

int intOption(int argc, char** argv, const char* name, int fallback)
{
    const char* raw = option(argc, argv, name, nullptr);
    return raw ? std::atoi(raw) : fallback;
}

std::string lower(std::string value)
{
    std::transform(value.begin(), value.end(), value.begin(), [](unsigned char ch) {
        return static_cast<char>(std::tolower(ch));
    });
    return value;
}

const char* shareModeName(AUDCLNT_SHAREMODE mode)
{
    return mode == AUDCLNT_SHAREMODE_EXCLUSIVE ? "exclusive" : "shared";
}

std::string describeFormat(const WAVEFORMATEX* format)
{
    char buffer[256]{};
    std::snprintf(
        buffer,
        sizeof(buffer),
        "%luHz %uch %ubit valid=%u block=%u %s",
        static_cast<unsigned long>(format->nSamplesPerSec),
        static_cast<unsigned>(format->nChannels),
        static_cast<unsigned>(format->wBitsPerSample),
        format->wFormatTag == WAVE_FORMAT_EXTENSIBLE
            ? static_cast<unsigned>(reinterpret_cast<const WAVEFORMATEXTENSIBLE*>(format)->Samples.wValidBitsPerSample)
            : static_cast<unsigned>(format->wBitsPerSample),
        static_cast<unsigned>(format->nBlockAlign),
        sampleFormat(format).c_str());
    return buffer;
}

WAVEFORMATEX* buildRequestedFormat(
    const WAVEFORMATEX* mixFormat,
    int sampleRate,
    int bitsPerSample,
    const char* requestedFormat,
    int requestedChannels)
{
    const WORD channels = static_cast<WORD>(requestedChannels > 0 ? requestedChannels : mixFormat->nChannels);
    if (channels == 0 || sampleRate <= 0 || bitsPerSample <= 0)
    {
        return nullptr;
    }

    const auto normalizedFormat = lower(requestedFormat ? requestedFormat : "float");
    const bool floatFormat = normalizedFormat == "float" || normalizedFormat == "float32";
    const WORD containerBits = static_cast<WORD>(floatFormat ? 32 : bitsPerSample == 24 ? 24 : bitsPerSample);
    auto* format = static_cast<WAVEFORMATEXTENSIBLE*>(CoTaskMemAlloc(sizeof(WAVEFORMATEXTENSIBLE)));
    if (!format)
    {
        return nullptr;
    }

    std::memset(format, 0, sizeof(WAVEFORMATEXTENSIBLE));
    format->Format.wFormatTag = WAVE_FORMAT_EXTENSIBLE;
    format->Format.nChannels = channels;
    format->Format.nSamplesPerSec = static_cast<DWORD>(sampleRate);
    format->Format.wBitsPerSample = containerBits;
    format->Format.nBlockAlign = static_cast<WORD>(channels * ((containerBits + 7) / 8));
    format->Format.nAvgBytesPerSec = format->Format.nSamplesPerSec * format->Format.nBlockAlign;
    format->Format.cbSize = sizeof(WAVEFORMATEXTENSIBLE) - sizeof(WAVEFORMATEX);
    format->Samples.wValidBitsPerSample = static_cast<WORD>(bitsPerSample);
    format->dwChannelMask = channels == 1 ? SPEAKER_FRONT_CENTER :
        channels == 2 ? (SPEAKER_FRONT_LEFT | SPEAKER_FRONT_RIGHT) : 0;
    format->SubFormat = floatFormat ? KSDATAFORMAT_SUBTYPE_IEEE_FLOAT : KSDATAFORMAT_SUBTYPE_PCM;
    return &format->Format;
}

std::string base64(const BYTE* data, DWORD byteLength)
{
    DWORD chars = 0;
    if (byteLength == 0)
    {
        return {};
    }

    if (!CryptBinaryToStringA(data, byteLength, CRYPT_STRING_BASE64 | CRYPT_STRING_NOCRLF, nullptr, &chars) || chars == 0)
    {
        return {};
    }

    std::string encoded(chars, '\0');
    if (!CryptBinaryToStringA(data, byteLength, CRYPT_STRING_BASE64 | CRYPT_STRING_NOCRLF, encoded.data(), &chars))
    {
        return {};
    }

    if (!encoded.empty() && encoded.back() == '\0')
    {
        encoded.pop_back();
    }

    return encoded;
}

void listEndpoints(IMMDeviceEnumerator* enumerator)
{
    for (const auto flow : { eCapture, eRender })
    {
        ComPtr<IMMDeviceCollection> collection;
        if (FAILED(enumerator->EnumAudioEndpoints(flow, DEVICE_STATE_ACTIVE, &collection.value)) || !collection)
        {
            continue;
        }

        UINT count = 0;
        collection->GetCount(&count);
        std::printf("%s endpoints=%u\n", flow == eCapture ? "capture" : "render", count);
        for (UINT index = 0; index < count; ++index)
        {
            ComPtr<IMMDevice> device;
            if (FAILED(collection->Item(index, &device.value)) || !device)
            {
                continue;
            }

            LPWSTR id = nullptr;
            device->GetId(&id);
            ComPtr<IPropertyStore> store;
            PropVariant name;
            if (SUCCEEDED(device->OpenPropertyStore(STGM_READ, &store.value)) && store)
            {
                store->GetValue(PKEY_Device_FriendlyName, &name.value);
            }

            std::printf(
                "  %u: %s id=%s\n",
                index,
                name.value.vt == VT_LPWSTR ? narrow(name.value.pwszVal).c_str() : "(unnamed)",
                id ? narrow(id).c_str() : "");
            CoTaskMemFree(id);
        }
    }
}

IMMDevice* openEndpoint(IMMDeviceEnumerator* enumerator, EDataFlow flow, const char* endpoint)
{
    IMMDevice* device = nullptr;
    if (std::strcmp(endpoint, "default") == 0)
    {
        if (SUCCEEDED(enumerator->GetDefaultAudioEndpoint(flow, eConsole, &device)))
        {
            return device;
        }
        return nullptr;
    }

    const std::wstring id = widen(endpoint);
    if (SUCCEEDED(enumerator->GetDevice(id.c_str(), &device)))
    {
        return device;
    }

    return nullptr;
}
}

int main(int argc, char** argv)
{
    std::setvbuf(stdout, nullptr, _IONBF, 0);
    ComInit com;
    if (FAILED(com.hr))
    {
        std::fprintf(stderr, "CoInitializeEx failed: 0x%08lx\n", static_cast<unsigned long>(com.hr));
        return 2;
    }

    ComPtr<IMMDeviceEnumerator> enumerator;
    HRESULT hr = CoCreateInstance(
        __uuidof(MMDeviceEnumerator),
        nullptr,
        CLSCTX_ALL,
        __uuidof(IMMDeviceEnumerator),
        reinterpret_cast<void**>(&enumerator.value));
    if (FAILED(hr) || !enumerator)
    {
        std::fprintf(stderr, "MMDeviceEnumerator failed: 0x%08lx\n", static_cast<unsigned long>(hr));
        return 3;
    }

    if (hasArg(argc, argv, "--list"))
    {
        listEndpoints(enumerator.value);
        return 0;
    }

    const bool loopback = hasArg(argc, argv, "--loopback");
    const bool exclusive = hasArg(argc, argv, "--exclusive");
    const bool requireFormat = hasArg(argc, argv, "--require-format");
    const char* endpointId = option(argc, argv, "--endpoint", "default");
    const char* sourceId = option(argc, argv, "--source-id", loopback ? "host-loopback" : "host-mic");
    const double seconds = std::atof(option(argc, argv, "--seconds", "0"));
    const int requestedSampleRate = intOption(argc, argv, "--sample-rate", 0);
    const int requestedBits = intOption(argc, argv, "--bits", 0);
    const int requestedChannels = intOption(argc, argv, "--channels", 0);
    const char* requestedFormat = option(argc, argv, "--format", "float");
    const bool emitJsonBlocks = hasArg(argc, argv, "--emit-json-blocks");
    const bool includeSamples = hasArg(argc, argv, "--include-samples");
    const EDataFlow flow = loopback ? eRender : eCapture;
    const AUDCLNT_SHAREMODE shareMode = exclusive && !loopback
        ? AUDCLNT_SHAREMODE_EXCLUSIVE
        : AUDCLNT_SHAREMODE_SHARED;
    if (exclusive && loopback)
    {
        std::fprintf(stderr, "WASAPI loopback uses the shared render engine; ignoring --exclusive.\n");
    }

    ComPtr<IMMDevice> device;
    device.value = openEndpoint(enumerator.value, flow, endpointId);
    if (!device)
    {
        std::fprintf(stderr, "Could not open %s endpoint: %s\n", loopback ? "render" : "capture", endpointId);
        return 4;
    }

    ComPtr<IPropertyStore> store;
    PropVariant endpointName;
    if (SUCCEEDED(device->OpenPropertyStore(STGM_READ, &store.value)) && store)
    {
        store->GetValue(PKEY_Device_FriendlyName, &endpointName.value);
    }
    std::printf(
        "wasapi endpoint: source=%s mode=%s name=%s\n",
        sourceId,
        loopback ? "loopback" : "capture",
        endpointName.value.vt == VT_LPWSTR ? narrow(endpointName.value.pwszVal).c_str() : "(unnamed)");

    ComPtr<IAudioClient> audioClient;
    hr = device->Activate(__uuidof(IAudioClient), CLSCTX_ALL, nullptr, reinterpret_cast<void**>(&audioClient.value));
    if (FAILED(hr) || !audioClient)
    {
        std::fprintf(stderr, "IAudioClient activation failed: 0x%08lx\n", static_cast<unsigned long>(hr));
        return 5;
    }

    WaveFormatHolder mixFormat;
    hr = audioClient->GetMixFormat(&mixFormat.value);
    if (FAILED(hr) || !mixFormat.value)
    {
        std::fprintf(stderr, "GetMixFormat failed: 0x%08lx\n", static_cast<unsigned long>(hr));
        return 6;
    }

    WaveFormatHolder requestedHolder;
    WaveFormatHolder closestHolder;
    WAVEFORMATEX* selectedFormat = mixFormat.value;
    const bool hasRequestedFormat = requestedSampleRate > 0 || requestedBits > 0 || requestedChannels > 0;
    if (hasRequestedFormat)
    {
        requestedHolder.value = buildRequestedFormat(
            mixFormat.value,
            requestedSampleRate > 0 ? requestedSampleRate : static_cast<int>(mixFormat.value->nSamplesPerSec),
            requestedBits > 0 ? requestedBits : static_cast<int>(mixFormat.value->wBitsPerSample),
            requestedFormat,
            requestedChannels);
        if (!requestedHolder.value)
        {
            std::fprintf(stderr, "Could not build requested WASAPI format.\n");
            return 7;
        }

        WAVEFORMATEX* closest = nullptr;
        hr = audioClient->IsFormatSupported(shareMode, requestedHolder.value, shareMode == AUDCLNT_SHAREMODE_SHARED ? &closest : nullptr);
        if (hr == S_OK)
        {
            selectedFormat = requestedHolder.value;
        }
        else if (hr == S_FALSE && closest)
        {
            closestHolder.value = closest;
            if (requireFormat)
            {
                std::fprintf(
                    stderr,
                    "Requested WASAPI format unsupported in %s mode: requested=%s closest=%s\n",
                    shareModeName(shareMode),
                    describeFormat(requestedHolder.value).c_str(),
                    describeFormat(closestHolder.value).c_str());
                return 7;
            }

            selectedFormat = closestHolder.value;
        }
        else
        {
            if (closest)
            {
                CoTaskMemFree(closest);
            }
            if (requireFormat)
            {
                std::fprintf(
                    stderr,
                    "Requested WASAPI format unsupported in %s mode: requested=%s hr=0x%08lx\n",
                    shareModeName(shareMode),
                    describeFormat(requestedHolder.value).c_str(),
                    static_cast<unsigned long>(hr));
                return 7;
            }
        }
    }

    std::fprintf(
        stderr,
        "wasapi format: source=%s mode=%s mix=%s selected=%s\n",
        sourceId,
        shareModeName(shareMode),
        describeFormat(mixFormat.value).c_str(),
        describeFormat(selectedFormat).c_str());

    const REFERENCE_TIME requestedDuration = 1000000; // 100 ms
    hr = audioClient->Initialize(
        shareMode,
        loopback ? AUDCLNT_STREAMFLAGS_LOOPBACK : 0,
        requestedDuration,
        shareMode == AUDCLNT_SHAREMODE_EXCLUSIVE ? requestedDuration : 0,
        selectedFormat,
        nullptr);
    if (FAILED(hr))
    {
        std::fprintf(stderr, "IAudioClient Initialize failed: 0x%08lx\n", static_cast<unsigned long>(hr));
        return 7;
    }

    ComPtr<IAudioCaptureClient> captureClient;
    hr = audioClient->GetService(__uuidof(IAudioCaptureClient), reinterpret_cast<void**>(&captureClient.value));
    if (FAILED(hr) || !captureClient)
    {
        std::fprintf(stderr, "IAudioCaptureClient failed: 0x%08lx\n", static_cast<unsigned long>(hr));
        return 8;
    }

    DWORD taskIndex = 0;
    HANDLE avrtHandle = AvSetMmThreadCharacteristicsW(L"Pro Audio", &taskIndex);
    audioClient->Start();

    const long long start = nowNs();
    const long long deadline = seconds > 0.0 ? start + static_cast<long long>(seconds * 1000000000.0) : 0;
    const std::string format = sampleFormat(selectedFormat);
    unsigned long long sequence = 0;
    unsigned long long totalFrames = 0;

    while (deadline == 0 || nowNs() < deadline)
    {
        Sleep(5);
        UINT32 packetFrames = 0;
        while (SUCCEEDED(captureClient->GetNextPacketSize(&packetFrames)) && packetFrames > 0)
        {
            BYTE* data = nullptr;
            UINT32 frames = 0;
            DWORD flags = 0;
            hr = captureClient->GetBuffer(&data, &frames, &flags, nullptr, nullptr);
            if (FAILED(hr))
            {
                std::fprintf(stderr, "GetBuffer failed: 0x%08lx\n", static_cast<unsigned long>(hr));
                audioClient->Stop();
                return 9;
            }

            const int byteLength = static_cast<int>(frames * selectedFormat->nBlockAlign);
            totalFrames += frames;
            ++sequence;
            if (emitJsonBlocks)
            {
                std::vector<BYTE> silentBytes;
                const BYTE* payload = data;
                if ((flags & AUDCLNT_BUFFERFLAGS_SILENT) != 0)
                {
                    silentBytes.assign(static_cast<std::size_t>(byteLength), 0);
                    payload = silentBytes.data();
                }

                const std::string encoded = includeSamples ? base64(payload, static_cast<DWORD>(byteLength)) : std::string();
                std::printf(
                    "{\"type\":\"audio-block\",\"sourceId\":\"%s\",\"timestampNs\":%lld,\"sequence\":%llu,\"sampleRate\":%lu,\"channels\":%u,\"sampleFormat\":\"%s\",\"frameCount\":%u,\"byteLength\":%d",
                    sourceId,
                    nowNs(),
                    sequence,
                    static_cast<unsigned long>(selectedFormat->nSamplesPerSec),
                    static_cast<unsigned>(selectedFormat->nChannels),
                    format.c_str(),
                    static_cast<unsigned>(frames),
                    byteLength);
                if (includeSamples)
                {
                    std::printf(",\"samplesBase64\":\"%s\"", encoded.c_str());
                }
                std::printf("}\n");
            }

            captureClient->ReleaseBuffer(frames);
        }
    }

    audioClient->Stop();
    if (avrtHandle)
    {
        AvRevertMmThreadCharacteristics(avrtHandle);
    }

    const double elapsed = (nowNs() - start) / 1000000000.0;
    std::printf(
        "measured: source=%s blocks=%llu frames=%llu elapsed=%.3f sampleRate=%lu channels=%u format=%s\n",
        sourceId,
        sequence,
        totalFrames,
        elapsed,
        static_cast<unsigned long>(selectedFormat->nSamplesPerSec),
        static_cast<unsigned>(selectedFormat->nChannels),
        format.c_str());
    return 0;
}
