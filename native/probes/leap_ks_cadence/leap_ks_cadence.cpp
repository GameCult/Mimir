#include <windows.h>
#include <winioctl.h>
#include <setupapi.h>
#include <ks.h>
#include <ksmedia.h>

#include <chrono>
#include <atomic>
#include <cstring>
#include <cstdint>
#include <cstdio>
#include <thread>
#include <string>
#include <vector>

namespace
{
struct Handle
{
    HANDLE value = INVALID_HANDLE_VALUE;

    Handle() = default;
    explicit Handle(HANDLE handle) : value(handle) {}
    Handle(const Handle&) = delete;
    Handle& operator=(const Handle&) = delete;
    Handle(Handle&& other) noexcept : value(other.value) { other.value = INVALID_HANDLE_VALUE; }
    Handle& operator=(Handle&& other) noexcept
    {
        if (this != &other)
        {
            close();
            value = other.value;
            other.value = INVALID_HANDLE_VALUE;
        }
        return *this;
    }

    ~Handle() { close(); }

    void close()
    {
        if (value != INVALID_HANDLE_VALUE)
        {
            CloseHandle(value);
            value = INVALID_HANDLE_VALUE;
        }
    }

    explicit operator bool() const { return value != INVALID_HANDLE_VALUE; }
};

struct DeviceInfoSet
{
    HDEVINFO value = INVALID_HANDLE_VALUE;

    explicit DeviceInfoSet(HDEVINFO info) : value(info) {}
    DeviceInfoSet(const DeviceInfoSet&) = delete;
    DeviceInfoSet& operator=(const DeviceInfoSet&) = delete;
    ~DeviceInfoSet()
    {
        if (value != INVALID_HANDLE_VALUE)
        {
            SetupDiDestroyDeviceInfoList(value);
        }
    }
};

struct PinCandidate
{
    ULONG pinId = 0;
    std::vector<std::uint8_t> format;
    LONG width = 0;
    LONG height = 0;
    LONG bitCount = 0;
    LONG imageBytes = 0;
    LONGLONG interval100ns = 0;
    GUID subtype{};
};

bool ksProperty(HANDLE handle, void* property, DWORD propertyBytes, void* output, DWORD outputBytes, DWORD* returned = nullptr)
{
    DWORD bytes = 0;
    const BOOL ok = DeviceIoControl(
        handle,
        IOCTL_KS_PROPERTY,
        property,
        propertyBytes,
        output,
        outputBytes,
        &bytes,
        nullptr);
    if (returned)
    {
        *returned = bytes;
    }
    return ok != FALSE;
}

template <typename T>
bool getPinProperty(HANDLE filter, ULONG pinId, ULONG propertyId, T& value)
{
    KSP_PIN property{};
    property.Property.Set = KSPROPSETID_Pin;
    property.Property.Id = propertyId;
    property.Property.Flags = KSPROPERTY_TYPE_GET;
    property.PinId = pinId;
    return ksProperty(filter, &property, sizeof(property), &value, sizeof(value));
}

std::string lastErrorText()
{
    const DWORD error = GetLastError();
    char* message = nullptr;
    FormatMessageA(
        FORMAT_MESSAGE_ALLOCATE_BUFFER | FORMAT_MESSAGE_FROM_SYSTEM | FORMAT_MESSAGE_IGNORE_INSERTS,
        nullptr,
        error,
        MAKELANGID(LANG_NEUTRAL, SUBLANG_DEFAULT),
        reinterpret_cast<char*>(&message),
        0,
        nullptr);
    std::string text = message ? message : "unknown error";
    if (message)
    {
        LocalFree(message);
    }
    char buffer[64]{};
    std::snprintf(buffer, sizeof(buffer), " (0x%08lx)", static_cast<unsigned long>(error));
    text += buffer;
    return text;
}

std::string wideToUtf8(const wchar_t* value)
{
    if (!value)
    {
        return {};
    }

    const int needed = WideCharToMultiByte(CP_UTF8, 0, value, -1, nullptr, 0, nullptr, nullptr);
    if (needed <= 1)
    {
        return {};
    }

    std::string result(static_cast<std::size_t>(needed - 1), '\0');
    WideCharToMultiByte(CP_UTF8, 0, value, -1, result.data(), needed, nullptr, nullptr);
    return result;
}

std::string guidText(const GUID& guid)
{
    char text[64]{};
    std::snprintf(
        text,
        sizeof(text),
        "{%08lx-%04x-%04x-%02x%02x-%02x%02x%02x%02x%02x%02x}",
        static_cast<unsigned long>(guid.Data1),
        guid.Data2,
        guid.Data3,
        guid.Data4[0],
        guid.Data4[1],
        guid.Data4[2],
        guid.Data4[3],
        guid.Data4[4],
        guid.Data4[5],
        guid.Data4[6],
        guid.Data4[7]);
    return text;
}

std::string fourccOrGuid(const GUID& guid)
{
    if (guid.Data2 == 0x0000 &&
        guid.Data3 == 0x0010 &&
        guid.Data4[0] == 0x80 &&
        guid.Data4[1] == 0x00 &&
        guid.Data4[2] == 0x00 &&
        guid.Data4[3] == 0xaa &&
        guid.Data4[4] == 0x00 &&
        guid.Data4[5] == 0x38 &&
        guid.Data4[6] == 0x9b &&
        guid.Data4[7] == 0x71)
    {
        char text[5]{};
        text[0] = static_cast<char>(guid.Data1 & 0xff);
        text[1] = static_cast<char>((guid.Data1 >> 8) & 0xff);
        text[2] = static_cast<char>((guid.Data1 >> 16) & 0xff);
        text[3] = static_cast<char>((guid.Data1 >> 24) & 0xff);
        return text;
    }

    return guidText(guid);
}

bool setState(HANDLE pin, KSSTATE state)
{
    KSPROPERTY property{};
    property.Set = KSPROPSETID_Connection;
    property.Id = KSPROPERTY_CONNECTION_STATE;
    property.Flags = KSPROPERTY_TYPE_SET;
    DWORD returned = 0;
    return DeviceIoControl(
               pin,
               IOCTL_KS_PROPERTY,
               &property,
               sizeof(property),
               &state,
               sizeof(state),
               &returned,
               nullptr) != FALSE;
}

std::vector<std::string> enumerateCapturePaths()
{
    std::vector<std::string> paths;
    DeviceInfoSet info(SetupDiGetClassDevsA(
        &KSCATEGORY_CAPTURE,
        nullptr,
        nullptr,
        DIGCF_PRESENT | DIGCF_DEVICEINTERFACE));
    if (info.value == INVALID_HANDLE_VALUE)
    {
        std::printf("SetupDiGetClassDevs(KSCATEGORY_CAPTURE) failed: %s\n", lastErrorText().c_str());
        return paths;
    }

    for (DWORD index = 0;; ++index)
    {
        SP_DEVICE_INTERFACE_DATA interfaceData{};
        interfaceData.cbSize = sizeof(interfaceData);
        if (!SetupDiEnumDeviceInterfaces(info.value, nullptr, &KSCATEGORY_CAPTURE, index, &interfaceData))
        {
            if (GetLastError() != ERROR_NO_MORE_ITEMS)
            {
                std::printf("SetupDiEnumDeviceInterfaces failed: %s\n", lastErrorText().c_str());
            }
            break;
        }

        DWORD required = 0;
        SetupDiGetDeviceInterfaceDetailW(info.value, &interfaceData, nullptr, 0, &required, nullptr);
        if (required == 0)
        {
            continue;
        }

        std::vector<std::uint8_t> storage(required);
        auto* detail = reinterpret_cast<SP_DEVICE_INTERFACE_DETAIL_DATA_W*>(storage.data());
        detail->cbSize = sizeof(*detail);
        if (!SetupDiGetDeviceInterfaceDetailW(info.value, &interfaceData, detail, required, nullptr, nullptr))
        {
            std::printf("SetupDiGetDeviceInterfaceDetail failed: %s\n", lastErrorText().c_str());
            continue;
        }

        paths.push_back(wideToUtf8(detail->DevicePath));
    }

    return paths;
}

std::vector<PinCandidate> inspectPins(HANDLE filter)
{
    ULONG pinCount = 0;
    KSPROPERTY countProperty{};
    countProperty.Set = KSPROPSETID_Pin;
    countProperty.Id = KSPROPERTY_PIN_CTYPES;
    countProperty.Flags = KSPROPERTY_TYPE_GET;
    if (!ksProperty(filter, &countProperty, sizeof(countProperty), &pinCount, sizeof(pinCount)))
    {
        std::printf("KSPROPERTY_PIN_CTYPES failed: %s\n", lastErrorText().c_str());
        return {};
    }

    std::vector<PinCandidate> candidates;
    std::printf("pin count: %lu\n", static_cast<unsigned long>(pinCount));

    for (ULONG pinId = 0; pinId < pinCount; ++pinId)
    {
        KSPIN_DATAFLOW dataflow{};
        KSPIN_COMMUNICATION communication{};
        const bool haveDataflow = getPinProperty(filter, pinId, KSPROPERTY_PIN_DATAFLOW, dataflow);
        const bool haveCommunication = getPinProperty(filter, pinId, KSPROPERTY_PIN_COMMUNICATION, communication);

        std::printf(
            "pin %lu: dataflow=%ld communication=%ld\n",
            static_cast<unsigned long>(pinId),
            haveDataflow ? static_cast<long>(dataflow) : -1L,
            haveCommunication ? static_cast<long>(communication) : -1L);

        KSP_PIN rangeProperty{};
        rangeProperty.Property.Set = KSPROPSETID_Pin;
        rangeProperty.Property.Id = KSPROPERTY_PIN_DATARANGES;
        rangeProperty.Property.Flags = KSPROPERTY_TYPE_GET;
        rangeProperty.PinId = pinId;

        DWORD rangeBytes = 0;
        ksProperty(filter, &rangeProperty, sizeof(rangeProperty), nullptr, 0, &rangeBytes);
        if (rangeBytes == 0)
        {
            continue;
        }

        std::vector<std::uint8_t> ranges(rangeBytes);
        DWORD returned = 0;
        if (!ksProperty(filter, &rangeProperty, sizeof(rangeProperty), ranges.data(), static_cast<DWORD>(ranges.size()), &returned))
        {
            std::printf("  dataranges failed: %s\n", lastErrorText().c_str());
            continue;
        }

        auto* multiple = reinterpret_cast<KSMULTIPLE_ITEM*>(ranges.data());
        auto* cursor = ranges.data() + sizeof(KSMULTIPLE_ITEM);
        for (ULONG i = 0; i < multiple->Count; ++i)
        {
            auto* dataRange = reinterpret_cast<KSDATARANGE*>(cursor);
            const bool videoInfo =
                IsEqualGUID(dataRange->MajorFormat, KSDATAFORMAT_TYPE_VIDEO) &&
                IsEqualGUID(dataRange->Specifier, KSDATAFORMAT_SPECIFIER_VIDEOINFO) &&
                dataRange->FormatSize >= sizeof(KS_DATARANGE_VIDEO);

            if (videoInfo)
            {
                auto* video = reinterpret_cast<KS_DATARANGE_VIDEO*>(cursor);
                const auto& bmi = video->VideoInfoHeader.bmiHeader;
                const double fps = video->VideoInfoHeader.AvgTimePerFrame > 0
                    ? 10000000.0 / static_cast<double>(video->VideoInfoHeader.AvgTimePerFrame)
                    : 0.0;

                std::printf(
                    "  range %lu: %ldx%ld %ld-bit %s sample=%lu image=%ld interval=%lld fps=%.2f\n",
                    static_cast<unsigned long>(i),
                    bmi.biWidth,
                    bmi.biHeight,
                    bmi.biBitCount,
                    fourccOrGuid(dataRange->SubFormat).c_str(),
                    static_cast<unsigned long>(dataRange->SampleSize),
                    bmi.biSizeImage,
                    video->VideoInfoHeader.AvgTimePerFrame,
                    fps);

                if (dataflow == KSPIN_DATAFLOW_OUT)
                {
                    PinCandidate candidate{};
                    candidate.pinId = pinId;
                    candidate.width = bmi.biWidth;
                    candidate.height = bmi.biHeight;
                    candidate.bitCount = bmi.biBitCount;
                    candidate.imageBytes = bmi.biSizeImage > 0 ? bmi.biSizeImage : static_cast<LONG>(dataRange->SampleSize);
                    candidate.interval100ns = video->VideoInfoHeader.AvgTimePerFrame;
                    candidate.subtype = dataRange->SubFormat;
                    candidate.format.resize(sizeof(KS_DATAFORMAT_VIDEOINFOHEADER));
                    auto* format = reinterpret_cast<KS_DATAFORMAT_VIDEOINFOHEADER*>(candidate.format.data());
                    format->DataFormat.FormatSize = sizeof(KS_DATAFORMAT_VIDEOINFOHEADER);
                    format->DataFormat.Flags = 0;
                    format->DataFormat.SampleSize = candidate.imageBytes;
                    format->DataFormat.Reserved = 0;
                    format->DataFormat.MajorFormat = KSDATAFORMAT_TYPE_VIDEO;
                    format->DataFormat.SubFormat = dataRange->SubFormat;
                    format->DataFormat.Specifier = KSDATAFORMAT_SPECIFIER_VIDEOINFO;
                    format->VideoInfoHeader = video->VideoInfoHeader;
                    format->VideoInfoHeader.bmiHeader.biSizeImage = candidate.imageBytes;
                    candidates.push_back(std::move(candidate));
                }
            }
            else
            {
                std::printf(
                    "  range %lu: non-video major=%s subtype=%s specifier=%s size=%lu\n",
                    static_cast<unsigned long>(i),
                    guidText(dataRange->MajorFormat).c_str(),
                    fourccOrGuid(dataRange->SubFormat).c_str(),
                    guidText(dataRange->Specifier).c_str(),
                    static_cast<unsigned long>(dataRange->FormatSize));
            }

            cursor += dataRange->FormatSize;
        }
    }

    return candidates;
}

bool measureCandidate(HANDLE filter, const PinCandidate& candidate, int readerCount)
{
    const ULONG connectBytes = sizeof(KSPIN_CONNECT) + static_cast<ULONG>(candidate.format.size());
    std::vector<std::uint8_t> connectStorage(connectBytes);
    auto* connect = reinterpret_cast<KSPIN_CONNECT*>(connectStorage.data());
    connect->Interface.Set = KSINTERFACESETID_Standard;
    connect->Interface.Id = KSINTERFACE_STANDARD_STREAMING;
    connect->Interface.Flags = 0;
    connect->Medium.Set = KSMEDIUMSETID_Standard;
    connect->Medium.Id = KSMEDIUM_TYPE_ANYINSTANCE;
    connect->Medium.Flags = 0;
    connect->PinId = candidate.pinId;
    connect->PinToHandle = nullptr;
    connect->Priority.PriorityClass = KSPRIORITY_NORMAL;
    connect->Priority.PrioritySubClass = 1;
    std::memcpy(connectStorage.data() + sizeof(KSPIN_CONNECT), candidate.format.data(), candidate.format.size());

    HANDLE rawPin = INVALID_HANDLE_VALUE;
    const HRESULT hr = KsCreatePin(filter, connect, GENERIC_READ, &rawPin);
    if (FAILED(hr))
    {
        std::printf("KsCreatePin failed: 0x%08lx\n", static_cast<unsigned long>(hr));
        return false;
    }
    Handle pin(rawPin);

    if (!setState(pin.value, KSSTATE_ACQUIRE))
    {
        std::printf("KSSTATE_ACQUIRE failed: %s\n", lastErrorText().c_str());
        return false;
    }
    if (!setState(pin.value, KSSTATE_PAUSE))
    {
        std::printf("KSSTATE_PAUSE failed: %s\n", lastErrorText().c_str());
        return false;
    }
    if (!setState(pin.value, KSSTATE_RUN))
    {
        std::printf("KSSTATE_RUN failed: %s\n", lastErrorText().c_str());
        return false;
    }

    const DWORD frameBytes = static_cast<DWORD>(candidate.imageBytes > 0 ? candidate.imageBytes : candidate.width * candidate.height * 2);
    const auto start = std::chrono::steady_clock::now();
    const auto deadline = start + std::chrono::seconds(5);
    std::atomic<std::uint64_t> frames = 0;
    std::atomic<bool> failed = false;
    std::vector<std::thread> readers;

    for (int reader = 0; reader < readerCount; ++reader)
    {
        readers.emplace_back([&, reader]()
        {
            std::vector<std::uint8_t> frame(frameBytes);
            KSSTREAM_HEADER header{};
            header.Size = sizeof(header);
            header.FrameExtent = frameBytes;
            header.Data = frame.data();
            header.PresentationTime.Numerator = 1;
            header.PresentationTime.Denominator = 1;

            while (!failed.load(std::memory_order_relaxed) && std::chrono::steady_clock::now() < deadline)
            {
                header.DataUsed = 0;
                header.OptionsFlags = 0;
                DWORD returned = 0;
                const BOOL ok = DeviceIoControl(
                    pin.value,
                    IOCTL_KS_READ_STREAM,
                    &header,
                    sizeof(header),
                    &header,
                    sizeof(header),
                    &returned,
                    nullptr);
                if (!ok || header.DataUsed == 0)
                {
                    failed.store(true, std::memory_order_relaxed);
                    std::printf(
                        "reader %d failed after %llu frames: ok=%d dataUsed=%lu bytesReturned=%lu error=%s\n",
                        reader,
                        static_cast<unsigned long long>(frames.load()),
                        ok != FALSE,
                        static_cast<unsigned long>(header.DataUsed),
                        static_cast<unsigned long>(returned),
                        lastErrorText().c_str());
                    break;
                }

                frames.fetch_add(1, std::memory_order_relaxed);
            }
        });
    }

    for (auto& reader : readers)
    {
        reader.join();
    }

    const auto end = std::chrono::steady_clock::now();
    setState(pin.value, KSSTATE_STOP);

    const double elapsed = std::chrono::duration<double>(end - start).count();
    const auto frameCount = frames.load(std::memory_order_relaxed);
    const double fps = static_cast<double>(frameCount) / elapsed;
    std::printf(
        "measured: %llu frames in %.3fs = %.2f fps, readers=%d, bytes/frame=%lu\n",
        static_cast<unsigned long long>(frameCount),
        elapsed,
        fps,
        readerCount,
        static_cast<unsigned long>(frameBytes));
    return true;
}
}

int main()
{
    const auto paths = enumerateCapturePaths();
    std::printf("capture interfaces: %zu\n", paths.size());

    for (const auto& path : paths)
    {
        if (path.find("vid_f182") == std::string::npos && path.find("VID_F182") == std::string::npos)
        {
            continue;
        }

        std::printf("Leap KS interface: %s\n", path.c_str());
        Handle filter(CreateFileA(
            path.c_str(),
            GENERIC_READ | GENERIC_WRITE,
            FILE_SHARE_READ | FILE_SHARE_WRITE,
            nullptr,
            OPEN_EXISTING,
            FILE_ATTRIBUTE_NORMAL,
            nullptr));
        if (!filter)
        {
            std::printf("CreateFile failed: %s\n", lastErrorText().c_str());
            return 2;
        }

        auto candidates = inspectPins(filter.value);
        if (candidates.empty())
        {
            std::printf("No capture pin candidate found.\n");
            return 3;
        }

        bool measuredAny = false;
        for (const auto& candidate : candidates)
        {
            std::printf(
                "measuring pin %lu %ldx%ld %ld-bit %s target %.2f fps\n",
                static_cast<unsigned long>(candidate.pinId),
                candidate.width,
                candidate.height,
                candidate.bitCount,
                fourccOrGuid(candidate.subtype).c_str(),
                candidate.interval100ns > 0 ? 10000000.0 / static_cast<double>(candidate.interval100ns) : 0.0);
            measuredAny = measureCandidate(filter.value, candidate, 1) || measuredAny;
        }

        return measuredAny ? 0 : 4;
    }

    std::printf("Leap VID_F182 capture interface not found.\n");
    return 1;
}
