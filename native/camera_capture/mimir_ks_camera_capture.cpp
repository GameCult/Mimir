#include <windows.h>
#include <winioctl.h>
#include <setupapi.h>
#include <ks.h>
#include <ksmedia.h>
#include <winternl.h>

#include <algorithm>
#include <atomic>
#include <cctype>
#include <chrono>
#include <condition_variable>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <deque>
#include <mutex>
#include <string>
#include <thread>
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
        if (value != INVALID_HANDLE_VALUE && value != nullptr)
        {
            CloseHandle(value);
            value = INVALID_HANDLE_VALUE;
        }
    }

    explicit operator bool() const { return value != INVALID_HANDLE_VALUE && value != nullptr; }
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

using NtDeviceIoControlFileFn = NTSTATUS(NTAPI*)(
    HANDLE FileHandle,
    HANDLE Event,
    PIO_APC_ROUTINE ApcRoutine,
    PVOID ApcContext,
    PIO_STATUS_BLOCK IoStatusBlock,
    ULONG IoControlCode,
    PVOID InputBuffer,
    ULONG InputBufferLength,
    PVOID OutputBuffer,
    ULONG OutputBufferLength);

constexpr NTSTATUS StatusPending = static_cast<NTSTATUS>(0x00000103L);

struct PinCandidate
{
    ULONG pinId = 0;
    std::vector<std::uint8_t> format;
    LONG width = 0;
    LONG height = 0;
    LONG imageBytes = 0;
    LONGLONG interval100ns = 0;
    GUID subtype{};
    std::string fourcc;
};

struct MimirKsCameraFrame
{
    std::vector<std::uint8_t> data;
    std::int64_t timestampNs = 0;
    std::uint64_t sequence = 0;
    int byteLength = 0;
};

struct MimirKsCameraCapture
{
    Handle filter;
    Handle pin;
    PinCandidate candidate;
    std::thread worker;
    std::mutex mutex;
    std::condition_variable condition;
    std::deque<MimirKsCameraFrame> queue;
    std::atomic<bool> running = false;
    int queueDepth = 8;
    std::uint64_t sequence = 0;
};

long long nowNs()
{
    return std::chrono::duration_cast<std::chrono::nanoseconds>(
        std::chrono::steady_clock::now().time_since_epoch()).count();
}

std::string lowercase(std::string value)
{
    std::transform(value.begin(), value.end(), value.begin(), [](unsigned char c) {
        return static_cast<char>(std::tolower(c));
    });
    return value;
}

std::string wideToUtf8(const wchar_t* value)
{
    if (!value)
    {
        return {};
    }

    const int bytes = WideCharToMultiByte(CP_UTF8, 0, value, -1, nullptr, 0, nullptr, nullptr);
    if (bytes <= 1)
    {
        return {};
    }

    std::string text(static_cast<std::size_t>(bytes - 1), '\0');
    WideCharToMultiByte(CP_UTF8, 0, value, -1, text.data(), bytes, nullptr, nullptr);
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

    return {};
}

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
    DWORD returned = 0;
    return ksProperty(filter, &property, sizeof(property), &value, sizeof(value), &returned);
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
        return paths;
    }

    for (DWORD index = 0;; ++index)
    {
        SP_DEVICE_INTERFACE_DATA interfaceData{};
        interfaceData.cbSize = sizeof(interfaceData);
        if (!SetupDiEnumDeviceInterfaces(info.value, nullptr, &KSCATEGORY_CAPTURE, index, &interfaceData))
        {
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
        if (SetupDiGetDeviceInterfaceDetailW(info.value, &interfaceData, detail, required, nullptr, nullptr))
        {
            paths.push_back(wideToUtf8(detail->DevicePath));
        }
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
        return {};
    }

    std::vector<PinCandidate> candidates;
    for (ULONG pinId = 0; pinId < pinCount; ++pinId)
    {
        KSPIN_DATAFLOW dataflow{};
        if (!getPinProperty(filter, pinId, KSPROPERTY_PIN_DATAFLOW, dataflow) ||
            dataflow != KSPIN_DATAFLOW_OUT)
        {
            continue;
        }

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
                PinCandidate candidate{};
                candidate.pinId = pinId;
                candidate.width = bmi.biWidth;
                candidate.height = bmi.biHeight;
                candidate.imageBytes = bmi.biSizeImage > 0 ? bmi.biSizeImage : static_cast<LONG>(dataRange->SampleSize);
                candidate.interval100ns = video->VideoInfoHeader.AvgTimePerFrame;
                candidate.subtype = dataRange->SubFormat;
                candidate.fourcc = fourccOrGuid(candidate.subtype);
                candidate.format.resize(sizeof(KS_DATAFORMAT_VIDEOINFOHEADER));
                auto* format = reinterpret_cast<KS_DATAFORMAT_VIDEOINFOHEADER*>(candidate.format.data());
                format->DataFormat.FormatSize = sizeof(KS_DATAFORMAT_VIDEOINFOHEADER);
                format->DataFormat.SampleSize = candidate.imageBytes;
                format->DataFormat.MajorFormat = KSDATAFORMAT_TYPE_VIDEO;
                format->DataFormat.SubFormat = dataRange->SubFormat;
                format->DataFormat.Specifier = KSDATAFORMAT_SPECIFIER_VIDEOINFO;
                format->VideoInfoHeader = video->VideoInfoHeader;
                format->VideoInfoHeader.bmiHeader.biSizeImage = candidate.imageBytes;
                candidates.push_back(std::move(candidate));
            }

            cursor += dataRange->FormatSize;
        }
    }

    return candidates;
}

const PinCandidate* findCandidate(
    const std::vector<PinCandidate>& candidates,
    int width,
    int height,
    const char* subtype,
    double minFps)
{
    const std::string target = subtype ? subtype : "";
    for (const auto& candidate : candidates)
    {
        const double fps = candidate.interval100ns > 0
            ? 10000000.0 / static_cast<double>(candidate.interval100ns)
            : 0.0;
        if (candidate.width == width &&
            candidate.height == height &&
            fps >= minFps &&
            candidate.fourcc == target)
        {
            return &candidate;
        }
    }

    return nullptr;
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

int strideBytes(const PinCandidate& candidate)
{
    return candidate.height > 0 && candidate.imageBytes > 0
        ? candidate.imageBytes / candidate.height
        : 0;
}

void pushFrame(MimirKsCameraCapture& capture, const std::uint8_t* data, ULONG byteLength)
{
    MimirKsCameraFrame frame;
    frame.byteLength = static_cast<int>(byteLength);
    frame.timestampNs = nowNs();
    frame.sequence = capture.sequence++;
    frame.data.resize(byteLength);
    std::memcpy(frame.data.data(), data, byteLength);

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

void captureThread(MimirKsCameraCapture* capture)
{
    auto* ntDeviceIoControlFile = reinterpret_cast<NtDeviceIoControlFileFn>(
        GetProcAddress(GetModuleHandleA("ntdll.dll"), "NtDeviceIoControlFile"));
    if (!ntDeviceIoControlFile)
    {
        capture->running.store(false);
        return;
    }

    const ULONG connectBytes = sizeof(KSPIN_CONNECT) + static_cast<ULONG>(capture->candidate.format.size());
    std::vector<std::uint8_t> connectStorage(connectBytes);
    auto* connect = reinterpret_cast<KSPIN_CONNECT*>(connectStorage.data());
    connect->Interface.Set = KSINTERFACESETID_Standard;
    connect->Interface.Id = KSINTERFACE_STANDARD_STREAMING;
    connect->Medium.Set = KSMEDIUMSETID_Standard;
    connect->Medium.Id = KSMEDIUM_TYPE_ANYINSTANCE;
    connect->PinId = capture->candidate.pinId;
    connect->Priority.PriorityClass = KSPRIORITY_NORMAL;
    connect->Priority.PrioritySubClass = 1;
    std::memcpy(connectStorage.data() + sizeof(KSPIN_CONNECT), capture->candidate.format.data(), capture->candidate.format.size());

    HANDLE rawPin = INVALID_HANDLE_VALUE;
    const HRESULT hr = KsCreatePin(capture->filter.value, connect, GENERIC_READ, &rawPin);
    if (FAILED(hr))
    {
        capture->running.store(false);
        return;
    }

    capture->pin = Handle(rawPin);
    if (!setState(capture->pin.value, KSSTATE_ACQUIRE) ||
        !setState(capture->pin.value, KSSTATE_PAUSE) ||
        !setState(capture->pin.value, KSSTATE_RUN))
    {
        capture->running.store(false);
        return;
    }

    const DWORD frameBytes = static_cast<DWORD>(capture->candidate.imageBytes);
    struct ReadSlot
    {
        Handle event;
        IO_STATUS_BLOCK status{};
        KSSTREAM_HEADER header{};
        std::vector<std::uint8_t> frame;
        bool pending = false;
    };

    std::vector<ReadSlot> slots(static_cast<std::size_t>(std::max(1, capture->queueDepth)));
    std::vector<HANDLE> waitEvents(slots.size());
    for (auto& slot : slots)
    {
        slot.event = Handle(CreateEventW(nullptr, TRUE, FALSE, nullptr));
        if (!slot.event)
        {
            capture->running.store(false);
            return;
        }

        slot.frame.resize(frameBytes);
        slot.header.Size = sizeof(KSSTREAM_HEADER);
        slot.header.FrameExtent = frameBytes;
        slot.header.Data = slot.frame.data();
        slot.header.PresentationTime.Numerator = 1;
        slot.header.PresentationTime.Denominator = 1;
        waitEvents[&slot - slots.data()] = slot.event.value;
    }

    auto submit = [&](ReadSlot& slot) -> bool {
        ResetEvent(slot.event.value);
        slot.status = {};
        slot.header.DataUsed = 0;
        slot.header.OptionsFlags = 0;
        const NTSTATUS status = ntDeviceIoControlFile(
            capture->pin.value,
            slot.event.value,
            nullptr,
            nullptr,
            &slot.status,
            IOCTL_KS_READ_STREAM,
            &slot.header,
            sizeof(slot.header),
            &slot.header,
            sizeof(slot.header));

        if (status == StatusPending)
        {
            slot.pending = true;
            return true;
        }

        slot.pending = false;
        if (status >= 0 && slot.header.DataUsed > 0)
        {
            pushFrame(*capture, slot.frame.data(), slot.header.DataUsed);
            return true;
        }

        return false;
    };

    for (auto& slot : slots)
    {
        if (!submit(slot))
        {
            capture->running.store(false);
            return;
        }
    }

    while (capture->running.load())
    {
        const DWORD waitResult = WaitForMultipleObjects(
            static_cast<DWORD>(waitEvents.size()),
            waitEvents.data(),
            FALSE,
            100);
        if (waitResult == WAIT_TIMEOUT)
        {
            continue;
        }

        if (waitResult < WAIT_OBJECT_0 || waitResult >= WAIT_OBJECT_0 + waitEvents.size())
        {
            break;
        }

        auto& slot = slots[static_cast<std::size_t>(waitResult - WAIT_OBJECT_0)];
        slot.pending = false;
        if (slot.status.Status < 0 || slot.header.DataUsed == 0)
        {
            break;
        }

        pushFrame(*capture, slot.frame.data(), slot.header.DataUsed);
        if (!submit(slot))
        {
            break;
        }
    }

    setState(capture->pin.value, KSSTATE_STOP);
    CancelIoEx(capture->pin.value, nullptr);
    for (auto& slot : slots)
    {
        if (slot.pending)
        {
            WaitForSingleObject(slot.event.value, 100);
        }
    }
    capture->running.store(false);
}

} // namespace

extern "C"
{
__declspec(dllexport) MimirKsCameraCapture* mimir_ks_create(
    const char* pathNeedle,
    int width,
    int height,
    const char* subtype,
    double minFps,
    int queueDepth)
{
    const std::string needle = lowercase(pathNeedle ? pathNeedle : "");
    for (const auto& path : enumerateCapturePaths())
    {
        if (!needle.empty() && lowercase(path).find(needle) == std::string::npos)
        {
            continue;
        }

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
            continue;
        }

        auto candidates = inspectPins(filter.value);
        const auto* candidate = findCandidate(candidates, width, height, subtype, minFps);
        if (!candidate)
        {
            continue;
        }

        auto* capture = new MimirKsCameraCapture();
        capture->filter = std::move(filter);
        capture->candidate = *candidate;
        capture->queueDepth = std::max(1, std::min(queueDepth, 32));
        return capture;
    }

    return nullptr;
}

__declspec(dllexport) int mimir_ks_start(MimirKsCameraCapture* capture)
{
    if (!capture || capture->running.exchange(true))
    {
        return 0;
    }

    capture->worker = std::thread(captureThread, capture);
    return 1;
}

__declspec(dllexport) int mimir_ks_read(
    MimirKsCameraCapture* capture,
    int* width,
    int* height,
    int* stride,
    char* fourcc,
    int fourccCapacity,
    std::int64_t* timestampNs,
    std::uint64_t* sequence,
    std::uint8_t* destination,
    int destinationBytes)
{
    if (!capture || !destination || destinationBytes <= 0)
    {
        return 0;
    }

    MimirKsCameraFrame frame;
    {
        std::lock_guard<std::mutex> lock(capture->mutex);
        if (capture->queue.empty())
        {
            return 0;
        }

        frame = std::move(capture->queue.front());
        capture->queue.pop_front();
    }

    if (frame.byteLength > destinationBytes)
    {
        return -frame.byteLength;
    }

    std::memcpy(destination, frame.data.data(), static_cast<std::size_t>(frame.byteLength));
    if (width)
    {
        *width = static_cast<int>(capture->candidate.width);
    }
    if (height)
    {
        *height = static_cast<int>(capture->candidate.height);
    }
    if (stride)
    {
        *stride = strideBytes(capture->candidate);
    }
    if (fourcc && fourccCapacity > 0)
    {
        std::snprintf(fourcc, static_cast<std::size_t>(fourccCapacity), "%s", capture->candidate.fourcc.c_str());
    }
    if (timestampNs)
    {
        *timestampNs = frame.timestampNs;
    }
    if (sequence)
    {
        *sequence = frame.sequence;
    }

    return frame.byteLength;
}

__declspec(dllexport) void mimir_ks_destroy(MimirKsCameraCapture* capture)
{
    if (!capture)
    {
        return;
    }

    capture->running.store(false);
    if (capture->pin)
    {
        CancelIoEx(capture->pin.value, nullptr);
    }
    if (capture->worker.joinable())
    {
        capture->worker.join();
    }

    delete capture;
}
}
