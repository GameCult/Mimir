#include <windows.h>
#include <mmdeviceapi.h>
#include <audioclient.h>
#include <functiondiscoverykeys_devpkey.h>
#include <avrt.h>
#include <ksmedia.h>
#include <wincrypt.h>

#include <chrono>
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
    const char* endpointId = option(argc, argv, "--endpoint", "default");
    const char* sourceId = option(argc, argv, "--source-id", loopback ? "host-loopback" : "host-mic");
    const double seconds = std::atof(option(argc, argv, "--seconds", "0"));
    const bool emitJsonBlocks = hasArg(argc, argv, "--emit-json-blocks");
    const bool includeSamples = hasArg(argc, argv, "--include-samples");
    const EDataFlow flow = loopback ? eRender : eCapture;

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

    WAVEFORMATEX* rawFormat = nullptr;
    hr = audioClient->GetMixFormat(&rawFormat);
    if (FAILED(hr) || !rawFormat)
    {
        std::fprintf(stderr, "GetMixFormat failed: 0x%08lx\n", static_cast<unsigned long>(hr));
        return 6;
    }

    const REFERENCE_TIME requestedDuration = 1000000; // 100 ms
    hr = audioClient->Initialize(
        AUDCLNT_SHAREMODE_SHARED,
        loopback ? AUDCLNT_STREAMFLAGS_LOOPBACK : 0,
        requestedDuration,
        0,
        rawFormat,
        nullptr);
    if (FAILED(hr))
    {
        std::fprintf(stderr, "IAudioClient Initialize failed: 0x%08lx\n", static_cast<unsigned long>(hr));
        CoTaskMemFree(rawFormat);
        return 7;
    }

    ComPtr<IAudioCaptureClient> captureClient;
    hr = audioClient->GetService(__uuidof(IAudioCaptureClient), reinterpret_cast<void**>(&captureClient.value));
    if (FAILED(hr) || !captureClient)
    {
        std::fprintf(stderr, "IAudioCaptureClient failed: 0x%08lx\n", static_cast<unsigned long>(hr));
        CoTaskMemFree(rawFormat);
        return 8;
    }

    DWORD taskIndex = 0;
    HANDLE avrtHandle = AvSetMmThreadCharacteristicsW(L"Pro Audio", &taskIndex);
    audioClient->Start();

    const long long start = nowNs();
    const long long deadline = seconds > 0.0 ? start + static_cast<long long>(seconds * 1000000000.0) : 0;
    const std::string format = sampleFormat(rawFormat);
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
                CoTaskMemFree(rawFormat);
                return 9;
            }

            const int byteLength = static_cast<int>(frames * rawFormat->nBlockAlign);
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
                    static_cast<unsigned long>(rawFormat->nSamplesPerSec),
                    static_cast<unsigned>(rawFormat->nChannels),
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
        static_cast<unsigned long>(rawFormat->nSamplesPerSec),
        static_cast<unsigned>(rawFormat->nChannels),
        format.c_str());
    CoTaskMemFree(rawFormat);
    return 0;
}
