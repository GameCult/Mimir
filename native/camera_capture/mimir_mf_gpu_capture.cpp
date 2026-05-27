#include <windows.h>
#include <d3d11_4.h>
#include <dxgi1_6.h>
#include <mfapi.h>
#include <mferror.h>
#include <mfidl.h>
#include <mfreadwrite.h>
#include <wrl/client.h>

#include <algorithm>
#include <cctype>
#include <cstdint>
#include <cstdio>
#include <cstdarg>
#include <string>
#include <vector>

using Microsoft::WRL::ComPtr;

namespace
{
struct MimirMfGpuCapture
{
    ComPtr<IMFMediaSource> source;
    ComPtr<IMFSourceReader> reader;
    ComPtr<ID3D11Device> device;
    ComPtr<ID3D11DeviceContext> context;
    ComPtr<IMFDXGIDeviceManager> deviceManager;
    ComPtr<ID3D11Texture2D> sharedTexture;
    HANDLE sharedHandle = nullptr;
    int width = 0;
    int height = 0;
    DXGI_FORMAT format = DXGI_FORMAT_UNKNOWN;
    std::uint64_t sequence = 0;
};

bool debugEnabled()
{
    return GetEnvironmentVariableA("MIMIR_MF_GPU_DEBUG", nullptr, 0) > 0;
}

void debugLog(const char* format, ...)
{
    if (!debugEnabled())
    {
        return;
    }

    std::fprintf(stderr, "mimir-mf-gpu: ");
    va_list args;
    va_start(args, format);
    std::vfprintf(stderr, format, args);
    va_end(args);
    std::fprintf(stderr, "\n");
}

std::wstring widen(const char* value)
{
    if (!value)
    {
        return {};
    }

    const int chars = MultiByteToWideChar(CP_UTF8, 0, value, -1, nullptr, 0);
    if (chars <= 1)
    {
        return {};
    }

    std::wstring result(static_cast<std::size_t>(chars - 1), L'\0');
    MultiByteToWideChar(CP_UTF8, 0, value, -1, result.data(), chars);
    return result;
}

std::wstring lowercase(std::wstring value)
{
    std::transform(value.begin(), value.end(), value.begin(), [](wchar_t c) {
        return static_cast<wchar_t>(towlower(c));
    });
    return value;
}

GUID subtypeFromText(const char* text)
{
    const std::string value = text ? text : "";
    std::string upper;
    upper.resize(value.size());
    std::transform(value.begin(), value.end(), upper.begin(), [](unsigned char c) {
        return static_cast<char>(std::toupper(c));
    });

    if (upper == "MJPG") return MFVideoFormat_MJPG;
    if (upper == "H264") return MFVideoFormat_H264;
    if (upper == "NV12") return MFVideoFormat_NV12;
    if (upper == "YUY2") return MFVideoFormat_YUY2;
    if (upper == "RGB32" || upper == "BGRA") return MFVideoFormat_RGB32;
    return GUID_NULL;
}

DXGI_FORMAT dxgiFormatForSubtype(const GUID& subtype)
{
    if (subtype == MFVideoFormat_NV12) return DXGI_FORMAT_NV12;
    if (subtype == MFVideoFormat_RGB32) return DXGI_FORMAT_B8G8R8A8_UNORM;
    if (subtype == MFVideoFormat_YUY2) return DXGI_FORMAT_YUY2;
    return DXGI_FORMAT_UNKNOWN;
}

const char* textForSubtype(const GUID& subtype)
{
    if (subtype == MFVideoFormat_NV12) return "NV12";
    if (subtype == MFVideoFormat_RGB32) return "Bgra8";
    if (subtype == MFVideoFormat_YUY2) return "YUY2";
    return "Unknown";
}

bool findVideoSource(const wchar_t* needle, IMFMediaSource** mediaSource)
{
    *mediaSource = nullptr;
    ComPtr<IMFAttributes> attributes;
    if (FAILED(MFCreateAttributes(&attributes, 2)))
    {
        debugLog("MFCreateAttributes for devices failed");
        return false;
    }

    attributes->SetGUID(MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE, MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_GUID);
    IMFActivate** rawDevices = nullptr;
    UINT32 count = 0;
    if (FAILED(MFEnumDeviceSources(attributes.Get(), &rawDevices, &count)))
    {
        debugLog("MFEnumDeviceSources failed");
        return false;
    }

    debugLog("device sources=%u", count);

    const std::wstring target = lowercase(needle ? needle : L"");
    bool found = false;
    for (UINT32 index = 0; index < count; ++index)
    {
        ComPtr<IMFActivate> activate(rawDevices[index]);
        rawDevices[index] = nullptr;
        WCHAR* symbolicLink = nullptr;
        UINT32 symbolicLength = 0;
        activate->GetAllocatedString(MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_SYMBOLIC_LINK, &symbolicLink, &symbolicLength);
        const std::wstring link = lowercase(symbolicLink ? symbolicLink : L"");
        if (debugEnabled())
        {
            std::fwprintf(stderr, L"mimir-mf-gpu: source[%u]=%ls\n", index, link.c_str());
        }
        CoTaskMemFree(symbolicLink);
        if (!target.empty() && link.find(target) == std::wstring::npos)
        {
            continue;
        }

        if (SUCCEEDED(activate->ActivateObject(IID_PPV_ARGS(mediaSource))))
        {
            debugLog("activated source index=%u", index);
            found = true;
            break;
        }
    }

    for (UINT32 index = 0; index < count; ++index)
    {
        if (rawDevices[index])
        {
            rawDevices[index]->Release();
        }
    }
    CoTaskMemFree(rawDevices);
    return found;
}

bool mediaTypeMatches(IMFMediaType* type, int width, int height, const GUID& subtype, double minFps)
{
    GUID actualSubtype{};
    UINT32 actualWidth = 0;
    UINT32 actualHeight = 0;
    UINT32 fpsNumerator = 0;
    UINT32 fpsDenominator = 0;
    if (FAILED(type->GetGUID(MF_MT_SUBTYPE, &actualSubtype)) ||
        actualSubtype != subtype ||
        FAILED(MFGetAttributeSize(type, MF_MT_FRAME_SIZE, &actualWidth, &actualHeight)) ||
        static_cast<int>(actualWidth) != width ||
        static_cast<int>(actualHeight) != height)
    {
        return false;
    }

    if (SUCCEEDED(MFGetAttributeRatio(type, MF_MT_FRAME_RATE, &fpsNumerator, &fpsDenominator)) &&
        fpsDenominator != 0)
    {
        return static_cast<double>(fpsNumerator) / fpsDenominator >= minFps;
    }

    return minFps <= 0.0;
}

bool setInputType(IMFSourceReader* reader, int width, int height, const GUID& subtype, double minFps)
{
    for (DWORD index = 0;; ++index)
    {
        ComPtr<IMFMediaType> type;
        const HRESULT hr = reader->GetNativeMediaType(MF_SOURCE_READER_FIRST_VIDEO_STREAM, index, &type);
        if (hr == MF_E_NO_MORE_TYPES)
        {
            debugLog("no input type matched %dx%d minFps=%.3f", width, height, minFps);
            return false;
        }

        if (FAILED(hr))
        {
            debugLog("GetNativeMediaType failed index=%lu hr=0x%08lx", index, static_cast<unsigned long>(hr));
            return false;
        }

        if (debugEnabled())
        {
            GUID actualSubtype{};
            UINT32 actualWidth = 0;
            UINT32 actualHeight = 0;
            UINT32 fpsNumerator = 0;
            UINT32 fpsDenominator = 0;
            type->GetGUID(MF_MT_SUBTYPE, &actualSubtype);
            MFGetAttributeSize(type.Get(), MF_MT_FRAME_SIZE, &actualWidth, &actualHeight);
            MFGetAttributeRatio(type.Get(), MF_MT_FRAME_RATE, &fpsNumerator, &fpsDenominator);
            debugLog(
                "nativeType[%lu] %ux%u subtype=%08lx fps=%u/%u",
                index,
                actualWidth,
                actualHeight,
                static_cast<unsigned long>(actualSubtype.Data1),
                fpsNumerator,
                fpsDenominator);
        }

        if (mediaTypeMatches(type.Get(), width, height, subtype, minFps))
        {
            const HRESULT setHr = reader->SetCurrentMediaType(MF_SOURCE_READER_FIRST_VIDEO_STREAM, nullptr, type.Get());
            debugLog("SetCurrentMediaType input index=%lu hr=0x%08lx", index, static_cast<unsigned long>(setHr));
            return SUCCEEDED(setHr);
        }
    }
}

bool setOutputType(IMFSourceReader* reader, const GUID& subtype)
{
    ComPtr<IMFMediaType> output;
    if (FAILED(MFCreateMediaType(&output)))
    {
        return false;
    }

    output->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
    output->SetGUID(MF_MT_SUBTYPE, subtype);
    const HRESULT hr = reader->SetCurrentMediaType(MF_SOURCE_READER_FIRST_VIDEO_STREAM, nullptr, output.Get());
    debugLog("SetCurrentMediaType output hr=0x%08lx", static_cast<unsigned long>(hr));
    return SUCCEEDED(hr);
}

bool createSharedTexture(MimirMfGpuCapture& capture, const GUID& outputSubtype)
{
    ComPtr<IMFMediaType> current;
    if (FAILED(capture.reader->GetCurrentMediaType(MF_SOURCE_READER_FIRST_VIDEO_STREAM, &current)))
    {
        debugLog("GetCurrentMediaType output failed");
        return false;
    }

    UINT32 width = 0;
    UINT32 height = 0;
    GUID subtype{};
    if (FAILED(MFGetAttributeSize(current.Get(), MF_MT_FRAME_SIZE, &width, &height)) ||
        FAILED(current->GetGUID(MF_MT_SUBTYPE, &subtype)))
    {
        return false;
    }

    const auto format = dxgiFormatForSubtype(subtype);
    if (format == DXGI_FORMAT_UNKNOWN || subtype != outputSubtype)
    {
        debugLog("output texture subtype/format unsupported subtype=%08lx", static_cast<unsigned long>(subtype.Data1));
        return false;
    }

    D3D11_TEXTURE2D_DESC desc{};
    desc.Width = width;
    desc.Height = height;
    desc.MipLevels = 1;
    desc.ArraySize = 1;
    desc.Format = format;
    desc.SampleDesc.Count = 1;
    desc.Usage = D3D11_USAGE_DEFAULT;
    desc.BindFlags = D3D11_BIND_SHADER_RESOURCE;
    desc.MiscFlags = D3D11_RESOURCE_MISC_SHARED_NTHANDLE | D3D11_RESOURCE_MISC_SHARED_KEYEDMUTEX;

    const HRESULT textureHr = capture.device->CreateTexture2D(&desc, nullptr, &capture.sharedTexture);
    if (FAILED(textureHr))
    {
        debugLog("CreateTexture2D shared NT failed hr=0x%08lx; retrying legacy shared", static_cast<unsigned long>(textureHr));
        desc.MiscFlags = D3D11_RESOURCE_MISC_SHARED;
        const HRESULT legacyTextureHr = capture.device->CreateTexture2D(&desc, nullptr, &capture.sharedTexture);
        if (FAILED(legacyTextureHr))
        {
            debugLog("CreateTexture2D legacy shared failed hr=0x%08lx", static_cast<unsigned long>(legacyTextureHr));
            return false;
        }
    }

    ComPtr<IDXGIResource1> dxgiResource;
    const HRESULT asHr = capture.sharedTexture.As(&dxgiResource);
    const HRESULT handleHr = SUCCEEDED(asHr)
        ? dxgiResource->CreateSharedHandle(nullptr, DXGI_SHARED_RESOURCE_READ | DXGI_SHARED_RESOURCE_WRITE, nullptr, &capture.sharedHandle)
        : asHr;
    if (FAILED(asHr) || FAILED(handleHr))
    {
        ComPtr<IDXGIResource> legacyResource;
        const HRESULT legacyAsHr = capture.sharedTexture.As(&legacyResource);
        const HRESULT legacyHandleHr = SUCCEEDED(legacyAsHr)
            ? legacyResource->GetSharedHandle(&capture.sharedHandle)
            : legacyAsHr;
        if (FAILED(legacyAsHr) || FAILED(legacyHandleHr))
        {
            debugLog(
                "CreateSharedHandle failed as=0x%08lx handle=0x%08lx legacyAs=0x%08lx legacyHandle=0x%08lx",
                static_cast<unsigned long>(asHr),
                static_cast<unsigned long>(handleHr),
                static_cast<unsigned long>(legacyAsHr),
                static_cast<unsigned long>(legacyHandleHr));
            return false;
        }
    }

    capture.width = static_cast<int>(width);
    capture.height = static_cast<int>(height);
    capture.format = format;
    return true;
}

} // namespace

extern "C"
{
__declspec(dllexport) MimirMfGpuCapture* mimir_mf_gpu_create(
    const char* pathNeedle,
    int width,
    int height,
    const char* inputSubtypeText,
    const char* outputSubtypeText,
    double minFps)
{
    CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    if (FAILED(MFStartup(MF_VERSION, MFSTARTUP_NOSOCKET)))
    {
        debugLog("MFStartup failed");
        return nullptr;
    }

    auto capture = new MimirMfGpuCapture();
    const UINT deviceFlags = D3D11_CREATE_DEVICE_VIDEO_SUPPORT | D3D11_CREATE_DEVICE_BGRA_SUPPORT;
    D3D_FEATURE_LEVEL featureLevels[] = { D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL_11_0 };
    D3D_FEATURE_LEVEL featureLevel{};
    const HRESULT deviceHr = D3D11CreateDevice(
            nullptr,
            D3D_DRIVER_TYPE_HARDWARE,
            nullptr,
            deviceFlags,
            featureLevels,
            ARRAYSIZE(featureLevels),
            D3D11_SDK_VERSION,
            &capture->device,
            &featureLevel,
            &capture->context);
    if (FAILED(deviceHr))
    {
        debugLog("D3D11CreateDevice failed hr=0x%08lx", static_cast<unsigned long>(deviceHr));
        delete capture;
        return nullptr;
    }

    ComPtr<ID3D10Multithread> multithread;
    if (SUCCEEDED(capture->device.As(&multithread)))
    {
        multithread->SetMultithreadProtected(TRUE);
    }

    UINT resetToken = 0;
    if (FAILED(MFCreateDXGIDeviceManager(&resetToken, &capture->deviceManager)) ||
        FAILED(capture->deviceManager->ResetDevice(capture->device.Get(), resetToken)))
    {
        debugLog("DXGI device manager setup failed");
        delete capture;
        return nullptr;
    }

    const auto needle = widen(pathNeedle);
    if (!findVideoSource(needle.c_str(), &capture->source))
    {
        debugLog("no source matched needle");
        delete capture;
        return nullptr;
    }

    ComPtr<IMFAttributes> readerAttributes;
    if (FAILED(MFCreateAttributes(&readerAttributes, 4)))
    {
        debugLog("MFCreateAttributes reader failed");
        delete capture;
        return nullptr;
    }

    readerAttributes->SetUnknown(MF_SOURCE_READER_D3D_MANAGER, capture->deviceManager.Get());
    readerAttributes->SetUINT32(MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS, TRUE);
    readerAttributes->SetUINT32(MF_LOW_LATENCY, TRUE);
    readerAttributes->SetUINT32(MF_SOURCE_READER_ENABLE_ADVANCED_VIDEO_PROCESSING, TRUE);

    const HRESULT readerHr = MFCreateSourceReaderFromMediaSource(capture->source.Get(), readerAttributes.Get(), &capture->reader);
    if (FAILED(readerHr))
    {
        debugLog("MFCreateSourceReaderFromMediaSource failed hr=0x%08lx", static_cast<unsigned long>(readerHr));
        delete capture;
        return nullptr;
    }

    const auto inputSubtype = subtypeFromText(inputSubtypeText);
    const auto outputSubtype = subtypeFromText(outputSubtypeText && outputSubtypeText[0] ? outputSubtypeText : "NV12");
    if (inputSubtype == GUID_NULL ||
        outputSubtype == GUID_NULL ||
        !setInputType(capture->reader.Get(), width, height, inputSubtype, minFps) ||
        !setOutputType(capture->reader.Get(), outputSubtype) ||
        !createSharedTexture(*capture, outputSubtype))
    {
        debugLog("create failed input=%s output=%s", inputSubtypeText ? inputSubtypeText : "", outputSubtypeText ? outputSubtypeText : "");
        delete capture;
        return nullptr;
    }

    return capture;
}

__declspec(dllexport) int mimir_mf_gpu_read(
    MimirMfGpuCapture* capture,
    HANDLE* sharedHandle,
    int* width,
    int* height,
    char* format,
    int formatCapacity,
    std::int64_t* timestampNs,
    std::uint64_t* sequence)
{
    if (!capture || !capture->reader || !capture->sharedTexture || !sharedHandle)
    {
        return 0;
    }

    DWORD streamIndex = 0;
    DWORD flags = 0;
    LONGLONG timestamp = 0;
    ComPtr<IMFSample> sample;
    const HRESULT hr = capture->reader->ReadSample(
        MF_SOURCE_READER_FIRST_VIDEO_STREAM,
        0,
        &streamIndex,
        &flags,
        &timestamp,
        &sample);
    if (FAILED(hr) || !sample || (flags & MF_SOURCE_READERF_STREAMTICK) != 0)
    {
        return 0;
    }

    ComPtr<IMFMediaBuffer> buffer;
    ComPtr<IMFDXGIBuffer> dxgiBuffer;
    ComPtr<ID3D11Texture2D> decodedTexture;
    UINT subresource = 0;
    if (FAILED(sample->GetBufferByIndex(0, &buffer)) ||
        FAILED(buffer.As(&dxgiBuffer)) ||
        FAILED(dxgiBuffer->GetResource(IID_PPV_ARGS(&decodedTexture))) ||
        FAILED(dxgiBuffer->GetSubresourceIndex(&subresource)))
    {
        return 0;
    }

    capture->context->CopySubresourceRegion(capture->sharedTexture.Get(), 0, 0, 0, 0, decodedTexture.Get(), subresource, nullptr);
    capture->context->Flush();
    *sharedHandle = capture->sharedHandle;
    if (width) *width = capture->width;
    if (height) *height = capture->height;
    if (format && formatCapacity > 0)
    {
        const char* text = capture->format == DXGI_FORMAT_NV12
            ? textForSubtype(MFVideoFormat_NV12)
            : capture->format == DXGI_FORMAT_B8G8R8A8_UNORM
                ? textForSubtype(MFVideoFormat_RGB32)
                : textForSubtype(MFVideoFormat_YUY2);
        std::snprintf(format, static_cast<std::size_t>(formatCapacity), "%s", text);
    }
    if (timestampNs) *timestampNs = timestamp * 100;
    if (sequence) *sequence = ++capture->sequence;
    return 1;
}

__declspec(dllexport) void mimir_mf_gpu_destroy(MimirMfGpuCapture* capture)
{
    if (!capture)
    {
        return;
    }

    if (capture->sharedHandle)
    {
        CloseHandle(capture->sharedHandle);
    }
    if (capture->source)
    {
        capture->source->Shutdown();
    }
    delete capture;
    MFShutdown();
    CoUninitialize();
}
}
