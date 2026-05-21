#include <windows.h>
#include <winioctl.h>
#include <setupapi.h>
#include <usbioctl.h>
#include <ks.h>
#include <ksmedia.h>
#include <winternl.h>

#include <chrono>
#include <cstring>
#include <cstdint>
#include <cstdio>
#include <string>
#include <vector>
#include <thread>
#include <utility>

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

enum class ControlSet
{
    Camera,
    VideoProcAmp,
    ExtendedVideoHdr
};

struct SavedControl
{
    ControlSet set;
    ULONG id = 0;
    LONG value = 0;
    ULONG flags = 0;
    bool valid = false;
};

struct ControlWrite
{
    const char* name = "";
    ControlSet set;
    ULONG id = 0;
    LONG value = 0;
    ULONG flags = 0;
};

struct ControlScenario
{
    const char* name = "";
    std::vector<ControlWrite> writes;
};

std::string lastErrorText();
std::string wideToUtf8(const wchar_t* value);
std::string guidText(const GUID& guid);

constexpr GUID UsbHubInterfaceGuid =
    {0xf18a0e88, 0xc30c, 0x11d0, {0x88, 0x15, 0x00, 0xa0, 0xc9, 0x06, 0xbe, 0xd8}};

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

std::vector<std::string> enumerateInterfacePaths(const GUID& interfaceGuid, const char* label)
{
    std::vector<std::string> paths;
    DeviceInfoSet info(SetupDiGetClassDevsA(
        &interfaceGuid,
        nullptr,
        nullptr,
        DIGCF_PRESENT | DIGCF_DEVICEINTERFACE));
    if (info.value == INVALID_HANDLE_VALUE)
    {
        std::printf("SetupDiGetClassDevs(%s) failed: %s\n", label, lastErrorText().c_str());
        return paths;
    }

    for (DWORD index = 0;; ++index)
    {
        SP_DEVICE_INTERFACE_DATA interfaceData{};
        interfaceData.cbSize = sizeof(interfaceData);
        if (!SetupDiEnumDeviceInterfaces(info.value, nullptr, &interfaceGuid, index, &interfaceData))
        {
            if (GetLastError() != ERROR_NO_MORE_ITEMS)
            {
                std::printf("SetupDiEnumDeviceInterfaces(%s) failed: %s\n", label, lastErrorText().c_str());
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
            std::printf("SetupDiGetDeviceInterfaceDetail(%s) failed: %s\n", label, lastErrorText().c_str());
            continue;
        }

        paths.push_back(wideToUtf8(detail->DevicePath));
    }

    return paths;
}

bool getVideoProcAmp(HANDLE filter, ULONG id, LONG& value, ULONG& flags)
{
    KSPROPERTY_VIDEOPROCAMP_S property{};
    property.Property.Set = PROPSETID_VIDCAP_VIDEOPROCAMP;
    property.Property.Id = id;
    property.Property.Flags = KSPROPERTY_TYPE_GET;
    if (!ksProperty(filter, &property, sizeof(property), &property, sizeof(property)))
    {
        return false;
    }

    value = property.Value;
    flags = property.Flags;
    return true;
}

bool setVideoProcAmp(HANDLE filter, ULONG id, LONG value, ULONG flags)
{
    KSPROPERTY_VIDEOPROCAMP_S property{};
    property.Property.Set = PROPSETID_VIDCAP_VIDEOPROCAMP;
    property.Property.Id = id;
    property.Property.Flags = KSPROPERTY_TYPE_SET;
    property.Value = value;
    property.Flags = flags;
    return ksProperty(filter, &property, sizeof(property), &property, sizeof(property));
}

bool getCameraControl(HANDLE filter, ULONG id, LONG& value, ULONG& flags)
{
    KSPROPERTY_CAMERACONTROL_S property{};
    property.Property.Set = PROPSETID_VIDCAP_CAMERACONTROL;
    property.Property.Id = id;
    property.Property.Flags = KSPROPERTY_TYPE_GET;
    if (!ksProperty(filter, &property, sizeof(property), &property, sizeof(property)))
    {
        return false;
    }

    value = property.Value;
    flags = property.Flags;
    return true;
}

bool setCameraControl(HANDLE filter, ULONG id, LONG value, ULONG flags)
{
    KSPROPERTY_CAMERACONTROL_S property{};
    property.Property.Set = PROPSETID_VIDCAP_CAMERACONTROL;
    property.Property.Id = id;
    property.Property.Flags = KSPROPERTY_TYPE_SET;
    property.Value = value;
    property.Flags = flags;
    return ksProperty(filter, &property, sizeof(property), &property, sizeof(property));
}

bool getControl(HANDLE filter, ControlSet set, ULONG id, LONG& value, ULONG& flags)
{
    if (set == ControlSet::Camera)
    {
        return getCameraControl(filter, id, value, flags);
    }

    if (set == ControlSet::VideoProcAmp)
    {
        return getVideoProcAmp(filter, id, value, flags);
    }

    struct ExtendedVideoHdrProperty
    {
        KSCAMERA_EXTENDEDPROP_HEADER header;
        KSCAMERA_EXTENDEDPROP_VALUE value;
    };

    KSPROPERTY property{};
    property.Set = KSPROPERTYSETID_ExtendedCameraControl;
    property.Id = KSPROPERTY_CAMERACONTROL_EXTENDED_VIDEOHDR;
    property.Flags = KSPROPERTY_TYPE_GET;

    ExtendedVideoHdrProperty data{};
    bool ok = false;
    const ULONG extendedPinIds[] = {KSCAMERA_EXTENDEDPROP_FILTERSCOPE, 0UL};
    for (const ULONG pinId : extendedPinIds)
    {
        data = {};
        data.header.Version = 1;
        data.header.PinId = pinId;
        data.header.Size = sizeof(data);
        if (ksProperty(filter, &property, sizeof(property), &data, sizeof(data)))
        {
            ok = true;
            break;
        }
    }

    if (!ok)
    {
        return false;
    }

    value = static_cast<LONG>(data.header.Flags);
    flags = static_cast<ULONG>(data.header.Capability);
    return true;
}

bool setControl(HANDLE filter, ControlSet set, ULONG id, LONG value, ULONG flags)
{
    if (set == ControlSet::Camera)
    {
        return setCameraControl(filter, id, value, flags);
    }

    if (set == ControlSet::VideoProcAmp)
    {
        return setVideoProcAmp(filter, id, value, flags);
    }

    struct ExtendedVideoHdrProperty
    {
        KSCAMERA_EXTENDEDPROP_HEADER header;
        KSCAMERA_EXTENDEDPROP_VALUE value;
    };

    KSPROPERTY property{};
    property.Set = KSPROPERTYSETID_ExtendedCameraControl;
    property.Id = KSPROPERTY_CAMERACONTROL_EXTENDED_VIDEOHDR;
    property.Flags = KSPROPERTY_TYPE_SET;

    ExtendedVideoHdrProperty data{};
    const ULONG extendedPinIds[] = {KSCAMERA_EXTENDEDPROP_FILTERSCOPE, 0UL};
    for (const ULONG pinId : extendedPinIds)
    {
        data = {};
        data.header.Version = 1;
        data.header.PinId = pinId;
        data.header.Size = sizeof(data);
        data.header.Flags = static_cast<ULONGLONG>(value);
        if (ksProperty(filter, &property, sizeof(property), &data, sizeof(data)))
        {
            return true;
        }
    }

    return false;
}

void printKnownControls(HANDLE filter)
{
    const ControlWrite controls[] = {
        {"camera.zoom/exposure", ControlSet::Camera, KSPROPERTY_CAMERACONTROL_ZOOM, 0, KSPROPERTY_CAMERACONTROL_FLAGS_MANUAL},
        {"camera.exposure", ControlSet::Camera, KSPROPERTY_CAMERACONTROL_EXPOSURE, 0, KSPROPERTY_CAMERACONTROL_FLAGS_MANUAL},
        {"camera.auto-exposure-priority/low-light", ControlSet::Camera, KSPROPERTY_CAMERACONTROL_AUTO_EXPOSURE_PRIORITY, 0, KSPROPERTY_CAMERACONTROL_FLAGS_MANUAL},
        {"extended.video-hdr", ControlSet::ExtendedVideoHdr, KSPROPERTY_CAMERACONTROL_EXTENDED_VIDEOHDR, 0, 0},
        {"procamp.contrast/hdr-led", ControlSet::VideoProcAmp, KSPROPERTY_VIDEOPROCAMP_CONTRAST, 0, KSPROPERTY_VIDEOPROCAMP_FLAGS_MANUAL},
        {"procamp.gamma", ControlSet::VideoProcAmp, KSPROPERTY_VIDEOPROCAMP_GAMMA, 0, KSPROPERTY_VIDEOPROCAMP_FLAGS_MANUAL},
        {"procamp.brightness/digital-gain", ControlSet::VideoProcAmp, KSPROPERTY_VIDEOPROCAMP_BRIGHTNESS, 0, KSPROPERTY_VIDEOPROCAMP_FLAGS_MANUAL},
        {"procamp.gain", ControlSet::VideoProcAmp, KSPROPERTY_VIDEOPROCAMP_GAIN, 0, KSPROPERTY_VIDEOPROCAMP_FLAGS_MANUAL},
        {"procamp.whitebalance/dark-frame", ControlSet::VideoProcAmp, KSPROPERTY_VIDEOPROCAMP_WHITEBALANCE, 0, KSPROPERTY_VIDEOPROCAMP_FLAGS_MANUAL},
        {"procamp.powerline-frequency", ControlSet::VideoProcAmp, KSPROPERTY_VIDEOPROCAMP_POWERLINE_FREQUENCY, 0, KSPROPERTY_VIDEOPROCAMP_FLAGS_MANUAL},
    };

    std::printf("known LeapUVC-ish controls:\n");
    for (const auto& control : controls)
    {
        LONG value = 0;
        ULONG flags = 0;
        if (getControl(filter, control.set, control.id, value, flags))
        {
            std::printf("  %s: value=%ld flags=0x%lx\n", control.name, value, static_cast<unsigned long>(flags));
        }
        else
        {
            std::printf("  %s: unavailable (%s)\n", control.name, lastErrorText().c_str());
        }
    }
}

std::string accessText(ULONG accessFlags)
{
    std::string text;
    if ((accessFlags & KSPROPERTY_TYPE_GET) != 0)
    {
        text += "GET";
    }
    if ((accessFlags & KSPROPERTY_TYPE_SET) != 0)
    {
        if (!text.empty())
        {
            text += "|";
        }
        text += "SET";
    }
    return text.empty() ? "none" : text;
}

const char* usbSpeedText(UCHAR speed)
{
    switch (speed)
    {
    case UsbLowSpeed:
        return "UsbLowSpeed";
    case UsbFullSpeed:
        return "UsbFullSpeed";
    case UsbHighSpeed:
        return "UsbHighSpeed";
    case UsbSuperSpeed:
        return "UsbSuperSpeed";
    default:
        return "unknown";
    }
}

bool getTopologyNodes(HANDLE filter, std::vector<GUID>& nodes)
{
    KSPROPERTY property{};
    property.Set = KSPROPSETID_Topology;
    property.Id = KSPROPERTY_TOPOLOGY_NODES;
    property.Flags = KSPROPERTY_TYPE_GET;

    DWORD bytes = 0;
    ksProperty(filter, &property, sizeof(property), nullptr, 0, &bytes);
    if (bytes < sizeof(KSMULTIPLE_ITEM))
    {
        std::printf("topology nodes unavailable: %s\n", lastErrorText().c_str());
        return false;
    }

    std::vector<std::uint8_t> storage(bytes);
    if (!ksProperty(filter, &property, sizeof(property), storage.data(), static_cast<DWORD>(storage.size()), &bytes))
    {
        std::printf("topology nodes query failed: %s\n", lastErrorText().c_str());
        return false;
    }

    auto* multiple = reinterpret_cast<KSMULTIPLE_ITEM*>(storage.data());
    auto* guidData = reinterpret_cast<GUID*>(storage.data() + sizeof(KSMULTIPLE_ITEM));
    nodes.assign(guidData, guidData + multiple->Count);
    return true;
}

bool getTopologyName(HANDLE filter, ULONG nodeId, std::string& name)
{
    KSP_NODE property{};
    property.Property.Set = KSPROPSETID_Topology;
    property.Property.Id = KSPROPERTY_TOPOLOGY_NAME;
    property.Property.Flags = KSPROPERTY_TYPE_GET;
    property.NodeId = nodeId;

    DWORD bytes = 0;
    ksProperty(filter, &property, sizeof(property), nullptr, 0, &bytes);
    if (bytes == 0)
    {
        return false;
    }

    std::vector<wchar_t> storage((bytes / sizeof(wchar_t)) + 1);
    if (!ksProperty(filter, &property, sizeof(property), storage.data(), static_cast<DWORD>(storage.size() * sizeof(wchar_t)), &bytes))
    {
        return false;
    }

    name = wideToUtf8(storage.data());
    return true;
}

void printExtensionUnits(HANDLE filter)
{
    const GUID extensionUnitPropertySet =
        {0x749e15f1, 0x12f6, 0x4d27, {0x97, 0x9f, 0x9a, 0xad, 0x9b, 0xde, 0xd6, 0xa5}};
    const GUID selectorPropertySet =
        {0x1abdaeca, 0x68b6, 0x4f83, {0x93, 0x71, 0xb4, 0x13, 0x90, 0x7c, 0x7b, 0x9f}};
    const GUID kiyoProExtensionUnit2 =
        {0x2c49d16a, 0x32b8, 0x4485, {0x3e, 0xa8, 0x64, 0x3a, 0x15, 0x23, 0x62, 0xf2}};
    const GUID kiyoProExtensionUnit6 =
        {0x23e49ed0, 0x1178, 0x4f31, {0xae, 0x52, 0xd2, 0xfb, 0x8a, 0x8d, 0x3b, 0x48}};
    const std::pair<const char*, GUID> propertySets[] = {
        {"node-type", KSNODETYPE_DEV_SPECIFIC},
        {"registered.extension-unit", extensionUnitPropertySet},
        {"registered.selector", selectorPropertySet},
        {"kiyo-pro.xu2", kiyoProExtensionUnit2},
        {"kiyo-pro.xu6", kiyoProExtensionUnit6},
    };

    std::vector<GUID> nodes;
    if (!getTopologyNodes(filter, nodes))
    {
        return;
    }

    std::printf("topology nodes: %zu\n", nodes.size());
    for (std::size_t nodeIndex = 0; nodeIndex < nodes.size(); ++nodeIndex)
    {
        std::string name;
        const bool hasName = getTopologyName(filter, static_cast<ULONG>(nodeIndex), name);
        const bool devSpecific = IsEqualGUID(nodes[nodeIndex], KSNODETYPE_DEV_SPECIFIC);
        std::printf(
            "  node %zu: type=%s%s",
            nodeIndex,
            guidText(nodes[nodeIndex]).c_str(),
            devSpecific ? " KSNODETYPE_DEV_SPECIFIC" : "");
        if (hasName && !name.empty())
        {
            std::printf(" name=%s", name.c_str());
        }
        std::printf("\n");

        if (!devSpecific)
        {
            continue;
        }

        for (const auto& propertySet : propertySets)
        {
            bool printedSetHeader = false;
            for (ULONG controlId = 1; controlId <= 64; ++controlId)
            {
                KSP_NODE property{};
                property.Property.Set = propertySet.second;
                property.Property.Id = controlId;
                property.Property.Flags = KSPROPERTY_TYPE_BASICSUPPORT | KSPROPERTY_TYPE_TOPOLOGY;
                property.NodeId = static_cast<ULONG>(nodeIndex);

                std::uint8_t supportStorage[512]{};
                DWORD returned = 0;
                if (!ksProperty(filter, &property, sizeof(property), supportStorage, sizeof(supportStorage), &returned))
                {
                    continue;
                }

                if (!printedSetHeader)
                {
                    std::printf("    property-set %s %s\n", propertySet.first, guidText(propertySet.second).c_str());
                    printedSetHeader = true;
                }

                ULONG accessFlags = 0;
                ULONG descriptionSize = 0;
                ULONG membersSize = 0;
                ULONG membersCount = 0;
                ULONG membersFlags = 0;
                if (returned >= sizeof(KSPROPERTY_DESCRIPTION))
                {
                    auto* description = reinterpret_cast<KSPROPERTY_DESCRIPTION*>(supportStorage);
                    accessFlags = description->AccessFlags;
                    descriptionSize = description->DescriptionSize;
                    if (description->MembersListCount > 0 &&
                        returned >= sizeof(KSPROPERTY_DESCRIPTION) + sizeof(KSPROPERTY_MEMBERSHEADER))
                    {
                        auto* members = reinterpret_cast<KSPROPERTY_MEMBERSHEADER*>(
                            supportStorage + sizeof(KSPROPERTY_DESCRIPTION));
                        membersFlags = members->MembersFlags;
                        membersSize = members->MembersSize;
                        membersCount = members->MembersCount;
                    }
                }
                else if (returned >= sizeof(ULONG))
                {
                    accessFlags = *reinterpret_cast<ULONG*>(supportStorage);
                }

                std::printf(
                    "      selector %lu: support=%s returned=%lu descSize=%lu members=0x%lx/%lu/%lu",
                    static_cast<unsigned long>(controlId),
                    accessText(accessFlags).c_str(),
                    static_cast<unsigned long>(returned),
                    static_cast<unsigned long>(descriptionSize),
                    static_cast<unsigned long>(membersFlags),
                    static_cast<unsigned long>(membersSize),
                    static_cast<unsigned long>(membersCount));

                bool printedValue = false;
                std::vector<DWORD> valueSizes;
                if (membersSize > 0 && membersSize <= 64)
                {
                    valueSizes.push_back(membersSize);
                }
                const DWORD fallbackValueSizes[] = {1, 2, 4, 8, 16, 32, 64};
                for (const DWORD valueBytes : fallbackValueSizes)
                {
                    bool alreadyListed = false;
                    for (const DWORD listed : valueSizes)
                    {
                        alreadyListed = alreadyListed || listed == valueBytes;
                    }
                    if (!alreadyListed)
                    {
                        valueSizes.push_back(valueBytes);
                    }
                }

                for (const DWORD valueBytes : valueSizes)
                {
                    std::uint8_t valueStorage[64]{};
                    KSP_NODE getProperty{};
                    getProperty.Property.Set = propertySet.second;
                    getProperty.Property.Id = controlId;
                    getProperty.Property.Flags = KSPROPERTY_TYPE_GET | KSPROPERTY_TYPE_TOPOLOGY;
                    getProperty.NodeId = static_cast<ULONG>(nodeIndex);
                    DWORD getReturned = 0;
                    if (!ksProperty(filter, &getProperty, sizeof(getProperty), valueStorage, valueBytes, &getReturned))
                    {
                        continue;
                    }

                    std::printf(" get[%lu]=", static_cast<unsigned long>(valueBytes));
                    for (DWORD byte = 0; byte < valueBytes; ++byte)
                    {
                        std::printf("%02x", valueStorage[byte]);
                    }
                    std::printf(" returned=%lu", static_cast<unsigned long>(getReturned));
                    printedValue = true;
                    break;
                }

                if (!printedValue)
                {
                    std::printf(" get=unavailable");
                }
                std::printf("\n");
            }
        }
    }
}

std::vector<std::uint8_t> getUsbConfigurationDescriptor(HANDLE hub, ULONG port)
{
    const auto requestBytes = sizeof(USB_DESCRIPTOR_REQUEST) + sizeof(USB_CONFIGURATION_DESCRIPTOR);
    std::vector<std::uint8_t> headerStorage(requestBytes);
    auto* headerRequest = reinterpret_cast<USB_DESCRIPTOR_REQUEST*>(headerStorage.data());
    headerRequest->ConnectionIndex = port;
    headerRequest->SetupPacket.wValue = USB_CONFIGURATION_DESCRIPTOR_TYPE << 8;
    headerRequest->SetupPacket.wLength = sizeof(USB_CONFIGURATION_DESCRIPTOR);

    DWORD returned = 0;
    if (!DeviceIoControl(
            hub,
            IOCTL_USB_GET_DESCRIPTOR_FROM_NODE_CONNECTION,
            headerRequest,
            static_cast<DWORD>(headerStorage.size()),
            headerRequest,
            static_cast<DWORD>(headerStorage.size()),
            &returned,
            nullptr))
    {
        return {};
    }

    const auto* config = reinterpret_cast<const USB_CONFIGURATION_DESCRIPTOR*>(headerRequest->Data);
    if (config->wTotalLength < sizeof(USB_CONFIGURATION_DESCRIPTOR))
    {
        return {};
    }

    std::vector<std::uint8_t> storage(sizeof(USB_DESCRIPTOR_REQUEST) + config->wTotalLength);
    auto* request = reinterpret_cast<USB_DESCRIPTOR_REQUEST*>(storage.data());
    request->ConnectionIndex = port;
    request->SetupPacket.wValue = USB_CONFIGURATION_DESCRIPTOR_TYPE << 8;
    request->SetupPacket.wLength = config->wTotalLength;

    if (!DeviceIoControl(
            hub,
            IOCTL_USB_GET_DESCRIPTOR_FROM_NODE_CONNECTION,
            request,
            static_cast<DWORD>(storage.size()),
            request,
            static_cast<DWORD>(storage.size()),
            &returned,
            nullptr))
    {
        return {};
    }

    std::vector<std::uint8_t> descriptor(config->wTotalLength);
    std::memcpy(descriptor.data(), request->Data, descriptor.size());
    return descriptor;
}

void printUvcVideoControlDescriptors(const std::vector<std::uint8_t>& descriptor)
{
    std::printf("  configuration descriptor bytes: %zu\n", descriptor.size());
    std::size_t offset = 0;
    std::uint8_t currentInterface = 0xff;
    while (offset + 2 <= descriptor.size())
    {
        const std::uint8_t length = descriptor[offset];
        const std::uint8_t type = descriptor[offset + 1];
        if (length < 2 || offset + length > descriptor.size())
        {
            std::printf("  descriptor parse stopped at offset %zu length=%u type=0x%02x\n", offset, length, type);
            break;
        }

        const auto* data = descriptor.data() + offset;
        if (type == USB_INTERFACE_DESCRIPTOR_TYPE && length >= sizeof(USB_INTERFACE_DESCRIPTOR))
        {
            const auto* iface = reinterpret_cast<const USB_INTERFACE_DESCRIPTOR*>(data);
            currentInterface = iface->bInterfaceNumber;
            std::printf(
                "  interface %u alt=%u class=0x%02x subclass=0x%02x protocol=0x%02x endpoints=%u\n",
                iface->bInterfaceNumber,
                iface->bAlternateSetting,
                iface->bInterfaceClass,
                iface->bInterfaceSubClass,
                iface->bInterfaceProtocol,
                iface->bNumEndpoints);
        }
        else if (type == 0x24 && length >= 3)
        {
            const std::uint8_t subtype = data[2];
            if (currentInterface == 0 && subtype == 0x05 && length >= 8)
            {
                const std::uint8_t unitId = data[3];
                const std::uint8_t controlSize = data[7];
                std::printf("    processing-unit id=%u controls=", unitId);
                for (std::uint8_t i = 0; i < controlSize && 8 + i < length; ++i)
                {
                    std::printf("%02x", data[8 + i]);
                }
                std::printf(" interface=%u\n", currentInterface);
            }
            else if (currentInterface == 0 && subtype == 0x06 && length >= 24)
            {
                const std::uint8_t unitId = data[3];
                GUID extensionGuid{};
                std::memcpy(&extensionGuid, data + 4, sizeof(extensionGuid));
                const std::uint8_t numControls = data[20];
                const std::uint8_t numPins = data[21];
                const std::size_t controlSizeOffset = 22 + numPins;
                std::printf(
                    "    extension-unit id=%u guid=%s controls=%u pins=%u",
                    unitId,
                    guidText(extensionGuid).c_str(),
                    numControls,
                    numPins);
                if (controlSizeOffset < length)
                {
                    const std::uint8_t controlSize = data[controlSizeOffset];
                    std::printf(" bmControls=");
                    for (std::uint8_t i = 0; i < controlSize && controlSizeOffset + 1 + i < length; ++i)
                    {
                        std::printf("%02x", data[controlSizeOffset + 1 + i]);
                    }
                }
                std::printf(" interface=%u\n", currentInterface);
            }
            else if (currentInterface == 0 && subtype == 0x04 && length >= 6)
            {
                std::printf("    selector-unit id=%u pins=%u interface=%u\n", data[3], data[4], currentInterface);
            }
        }

        offset += length;
    }
}

void printUsbVideoDescriptors(USHORT vendorId, USHORT productId)
{
    const auto hubPaths = enumerateInterfacePaths(UsbHubInterfaceGuid, "GUID_DEVINTERFACE_USB_HUB");
    std::printf("USB hubs: %zu\n", hubPaths.size());
    for (const auto& hubPath : hubPaths)
    {
        Handle hub(CreateFileA(
            hubPath.c_str(),
            GENERIC_WRITE,
            FILE_SHARE_WRITE,
            nullptr,
            OPEN_EXISTING,
            FILE_ATTRIBUTE_NORMAL,
            nullptr));
        if (!hub)
        {
            continue;
        }

        USB_NODE_INFORMATION nodeInfo{};
        nodeInfo.NodeType = UsbHub;
        DWORD returned = 0;
        if (!DeviceIoControl(
                hub.value,
                IOCTL_USB_GET_NODE_INFORMATION,
                &nodeInfo,
                sizeof(nodeInfo),
                &nodeInfo,
                sizeof(nodeInfo),
                &returned,
                nullptr))
        {
            continue;
        }

        const ULONG ports = nodeInfo.u.HubInformation.HubDescriptor.bNumberOfPorts;
        for (ULONG port = 1; port <= ports; ++port)
        {
            std::vector<std::uint8_t> connectionStorage(sizeof(USB_NODE_CONNECTION_INFORMATION_EX));
            auto* connection = reinterpret_cast<USB_NODE_CONNECTION_INFORMATION_EX*>(connectionStorage.data());
            connection->ConnectionIndex = port;
            if (!DeviceIoControl(
                    hub.value,
                    IOCTL_USB_GET_NODE_CONNECTION_INFORMATION_EX,
                    connection,
                    static_cast<DWORD>(connectionStorage.size()),
                    connection,
                    static_cast<DWORD>(connectionStorage.size()),
                    &returned,
                    nullptr))
            {
                continue;
            }

            if (connection->DeviceDescriptor.idVendor != vendorId ||
                connection->DeviceDescriptor.idProduct != productId)
            {
                continue;
            }

            std::printf(
                "USB descriptor match: hub=%s port=%lu speed=%s(%d) configs=%u bcdUSB=0x%04x bcdDevice=0x%04x\n",
                hubPath.c_str(),
                static_cast<unsigned long>(port),
                usbSpeedText(connection->Speed),
                static_cast<int>(connection->Speed),
                connection->DeviceDescriptor.bNumConfigurations,
                connection->DeviceDescriptor.bcdUSB,
                connection->DeviceDescriptor.bcdDevice);
            const auto descriptor = getUsbConfigurationDescriptor(hub.value, port);
            if (descriptor.empty())
            {
                std::printf("  configuration descriptor unavailable: %s\n", lastErrorText().c_str());
                continue;
            }
            printUvcVideoControlDescriptors(descriptor);
        }
    }
}

std::vector<SavedControl> saveControls(HANDLE filter, const ControlScenario& scenario)
{
    std::vector<SavedControl> saved;
    for (const auto& write : scenario.writes)
    {
        bool alreadySaved = false;
        for (const auto& entry : saved)
        {
            if (entry.set == write.set && entry.id == write.id)
            {
                alreadySaved = true;
                break;
            }
        }
        if (alreadySaved)
        {
            continue;
        }

        SavedControl entry{};
        entry.set = write.set;
        entry.id = write.id;
        entry.valid = getControl(filter, write.set, write.id, entry.value, entry.flags);
        saved.push_back(entry);
    }

    return saved;
}

void restoreControls(HANDLE filter, const std::vector<SavedControl>& saved)
{
    for (const auto& entry : saved)
    {
        if (entry.valid)
        {
            setControl(filter, entry.set, entry.id, entry.value, entry.flags);
        }
    }
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
    return enumerateInterfacePaths(KSCATEGORY_CAPTURE, "KSCATEGORY_CAPTURE");
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

bool measureCandidate(HANDLE filter, const PinCandidate& candidate, int queueDepth)
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

    auto* ntDeviceIoControlFile = reinterpret_cast<NtDeviceIoControlFileFn>(
        GetProcAddress(GetModuleHandleA("ntdll.dll"), "NtDeviceIoControlFile"));
    if (!ntDeviceIoControlFile)
    {
        std::printf("NtDeviceIoControlFile lookup failed.\n");
        return false;
    }

    const DWORD frameBytes = static_cast<DWORD>(candidate.imageBytes > 0 ? candidate.imageBytes : candidate.width * candidate.height * 2);

    struct ReadSlot
    {
        Handle event;
        IO_STATUS_BLOCK status{};
        KSSTREAM_HEADER header{};
        std::vector<std::uint8_t> frame;
        bool pending = false;
    };

    std::vector<ReadSlot> slots(static_cast<std::size_t>(queueDepth));
    std::vector<HANDLE> waitEvents(static_cast<std::size_t>(queueDepth));
    for (int index = 0; index < queueDepth; ++index)
    {
        auto& slot = slots[static_cast<std::size_t>(index)];
        slot.event = Handle(CreateEventW(nullptr, TRUE, FALSE, nullptr));
        if (!slot.event)
        {
            std::printf("CreateEvent failed: %s\n", lastErrorText().c_str());
            return false;
        }

        slot.frame.resize(frameBytes);
        slot.header.Size = sizeof(KSSTREAM_HEADER);
        slot.header.FrameExtent = frameBytes;
        slot.header.Data = slot.frame.data();
        slot.header.PresentationTime.Numerator = 1;
        slot.header.PresentationTime.Denominator = 1;
        waitEvents[static_cast<std::size_t>(index)] = slot.event.value;
    }

    auto submit = [&](ReadSlot& slot, std::uint64_t frameCount) -> bool
    {
        ResetEvent(slot.event.value);
        slot.status = {};
        slot.header.DataUsed = 0;
        slot.header.OptionsFlags = 0;
        const NTSTATUS status = ntDeviceIoControlFile(
            pin.value,
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

        if (status >= 0)
        {
            slot.pending = false;
            if (slot.header.DataUsed == 0)
            {
                std::printf(
                    "NtDeviceIoControlFile returned no payload after %llu frames; status=0x%08lx information=%llu\n",
                    static_cast<unsigned long long>(frameCount),
                    static_cast<unsigned long>(status),
                    static_cast<unsigned long long>(slot.status.Information));
                return false;
            }
            return true;
        }

        slot.pending = false;
        std::printf(
            "NtDeviceIoControlFile failed after %llu frames: status=0x%08lx\n",
            static_cast<unsigned long long>(frameCount),
            static_cast<unsigned long>(status));
        return false;
    };

    const auto start = std::chrono::steady_clock::now();
    const auto measureStart = start + std::chrono::seconds(2);
    const auto deadline = measureStart + std::chrono::seconds(5);
    std::uint64_t frames = 0;
    std::uint64_t warmupFrames = 0;
    bool usingPendingReads = false;

    for (auto& slot : slots)
    {
        if (!submit(slot, frames))
        {
            setState(pin.value, KSSTATE_STOP);
            return frames > 0;
        }
        if (slot.pending)
        {
            usingPendingReads = true;
        }
        else
        {
            if (std::chrono::steady_clock::now() >= measureStart)
            {
                ++frames;
            }
            else
            {
                ++warmupFrames;
            }
        }
    }

    if (!usingPendingReads)
    {
        while (std::chrono::steady_clock::now() < deadline)
        {
            if (!submit(slots.front(), frames))
            {
                break;
            }
            if (slots.front().pending)
            {
                usingPendingReads = true;
                break;
            }
            if (std::chrono::steady_clock::now() >= measureStart)
            {
                ++frames;
            }
            else
            {
                ++warmupFrames;
            }
        }
    }

    while (usingPendingReads && std::chrono::steady_clock::now() < deadline)
    {
        const DWORD waitResult = WaitForMultipleObjects(
            static_cast<DWORD>(waitEvents.size()),
            waitEvents.data(),
            FALSE,
            1000);
        if (waitResult < WAIT_OBJECT_0 || waitResult >= WAIT_OBJECT_0 + waitEvents.size())
        {
            std::printf("WaitForMultipleObjects failed or timed out after %llu frames: result=0x%08lx error=%s\n", static_cast<unsigned long long>(frames), static_cast<unsigned long>(waitResult), lastErrorText().c_str());
            break;
        }

        auto& slot = slots[static_cast<std::size_t>(waitResult - WAIT_OBJECT_0)];
        slot.pending = false;
        if (slot.status.Status < 0 || slot.header.DataUsed == 0)
        {
            std::printf(
                "queued read failed after %llu frames: status=0x%08lx dataUsed=%lu information=%llu\n",
                static_cast<unsigned long long>(frames),
                static_cast<unsigned long>(slot.status.Status),
                static_cast<unsigned long>(slot.header.DataUsed),
                static_cast<unsigned long long>(slot.status.Information));
            break;
        }

        if (std::chrono::steady_clock::now() >= measureStart)
        {
            ++frames;
        }
        else
        {
            ++warmupFrames;
        }
        if (std::chrono::steady_clock::now() < deadline)
        {
            if (!submit(slot, frames))
            {
                break;
            }
        }
    }

    const auto end = std::chrono::steady_clock::now();
    setState(pin.value, KSSTATE_STOP);
    for (auto& slot : slots)
    {
        if (slot.pending)
        {
            WaitForSingleObject(slot.event.value, 100);
            slot.pending = false;
        }
    }

    const double elapsed = std::chrono::duration<double>(end - measureStart).count();
    const double fps = static_cast<double>(frames) / elapsed;
    std::printf(
        "measured: %llu frames in %.3fs = %.2f fps, warmup=%llu, queueDepth=%d, async=%s, bytes/frame=%lu\n",
        static_cast<unsigned long long>(frames),
        elapsed,
        fps,
        static_cast<unsigned long long>(warmupFrames),
        queueDepth,
        usingPendingReads ? "yes" : "no",
        static_cast<unsigned long>(frameBytes));
    return true;
}

bool measureScenario(HANDLE filter, const PinCandidate& candidate, const ControlScenario& scenario)
{
    std::printf("scenario: %s\n", scenario.name);
    auto saved = saveControls(filter, scenario);
    for (const auto& write : scenario.writes)
    {
        const bool ok = setControl(filter, write.set, write.id, write.value, write.flags);
        std::printf(
            "  set %s=%ld flags=0x%lx -> %s\n",
            write.name,
            write.value,
            static_cast<unsigned long>(write.flags),
            ok ? "ok" : lastErrorText().c_str());
    }

    std::this_thread::sleep_for(std::chrono::milliseconds(250));
    const bool measured = measureCandidate(filter, candidate, 8);
    restoreControls(filter, saved);
    std::this_thread::sleep_for(std::chrono::milliseconds(100));
    return measured;
}
}

int main(int argc, char** argv)
{
    std::setvbuf(stdout, nullptr, _IONBF, 0);
    const std::string targetVid = argc > 1 ? argv[1] : "vid_f182";
    bool controlsOnly = false;
    bool baselineOnly = false;
    bool allWebcamFormats = false;
    for (int arg = 2; arg < argc; ++arg)
    {
        const std::string option = argv[arg];
        controlsOnly = controlsOnly || option == "--controls-only";
        baselineOnly = baselineOnly || option == "--baseline-only";
        allWebcamFormats = allWebcamFormats || option == "--all-webcam-formats";
    }
    const bool leapMode = targetVid == "vid_f182" || targetVid == "VID_F182";
    const auto paths = enumerateCapturePaths();
    std::printf("capture interfaces: %zu\n", paths.size());

    for (const auto& path : paths)
    {
        if (path.find(targetVid) == std::string::npos)
        {
            continue;
        }

        std::printf("KS interface: %s\n", path.c_str());
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
            continue;
        }

        if (!leapMode)
        {
            printKnownControls(filter.value);
            printExtensionUnits(filter.value);
            printUsbVideoDescriptors(0x1532, 0x0e05);
            if (controlsOnly)
            {
                return 0;
            }

            const ULONG camManual = KSPROPERTY_CAMERACONTROL_FLAGS_MANUAL;
            const ULONG procManual = KSPROPERTY_VIDEOPROCAMP_FLAGS_MANUAL;
            const std::vector<ControlScenario> webcamScenarios = {
                {"baseline", {}},
                {"extended video HDR off", {{"extended.video-hdr", ControlSet::ExtendedVideoHdr, KSPROPERTY_CAMERACONTROL_EXTENDED_VIDEOHDR, static_cast<LONG>(KSCAMERA_EXTENDEDPROP_VIDEOHDR_OFF), 0}}},
                {"low-light compensation off", {{"camera.auto-exposure-priority/low-light", ControlSet::Camera, KSPROPERTY_CAMERACONTROL_AUTO_EXPOSURE_PRIORITY, 0, camManual}}},
                {"powerline disabled", {{"procamp.powerline-frequency", ControlSet::VideoProcAmp, KSPROPERTY_VIDEOPROCAMP_POWERLINE_FREQUENCY, 0, procManual}}},
                {"powerline 50hz", {{"procamp.powerline-frequency", ControlSet::VideoProcAmp, KSPROPERTY_VIDEOPROCAMP_POWERLINE_FREQUENCY, 1, procManual}}},
                {"powerline 60hz", {{"procamp.powerline-frequency", ControlSet::VideoProcAmp, KSPROPERTY_VIDEOPROCAMP_POWERLINE_FREQUENCY, 2, procManual}}},
                {"manual exposure -5", {{"camera.exposure", ControlSet::Camera, KSPROPERTY_CAMERACONTROL_EXPOSURE, -5, camManual}}},
                {"manual exposure -6", {{"camera.exposure", ControlSet::Camera, KSPROPERTY_CAMERACONTROL_EXPOSURE, -6, camManual}}},
                {"manual exposure -7", {{"camera.exposure", ControlSet::Camera, KSPROPERTY_CAMERACONTROL_EXPOSURE, -7, camManual}}},
                {"manual exposure -8", {{"camera.exposure", ControlSet::Camera, KSPROPERTY_CAMERACONTROL_EXPOSURE, -8, camManual}}},
                {"manual exposure -9", {{"camera.exposure", ControlSet::Camera, KSPROPERTY_CAMERACONTROL_EXPOSURE, -9, camManual}}},
                {"manual exposure -10", {{"camera.exposure", ControlSet::Camera, KSPROPERTY_CAMERACONTROL_EXPOSURE, -10, camManual}}},
                {"low-light off + exposure -8", {
                    {"camera.auto-exposure-priority/low-light", ControlSet::Camera, KSPROPERTY_CAMERACONTROL_AUTO_EXPOSURE_PRIORITY, 0, camManual},
                    {"camera.exposure", ControlSet::Camera, KSPROPERTY_CAMERACONTROL_EXPOSURE, -8, camManual}}},
                {"gain minimum", {{"procamp.gain", ControlSet::VideoProcAmp, KSPROPERTY_VIDEOPROCAMP_GAIN, 0, procManual}}},
            };
            const std::vector<ControlScenario> baselineScenarios = {
                {"baseline", {}},
            };
            const auto& scenarios = baselineOnly ? baselineScenarios : webcamScenarios;
            bool measuredAny = false;
            for (const auto& candidate : candidates)
            {
                const double targetFps = candidate.interval100ns > 0
                    ? 10000000.0 / static_cast<double>(candidate.interval100ns)
                    : 0.0;
                const auto subtype = fourccOrGuid(candidate.subtype);
                const bool interesting =
                    ((candidate.width == 1920 && candidate.height == 1080) ||
                     (candidate.width == 1280 && candidate.height == 720)) &&
                    targetFps >= 25.0 &&
                    (subtype == "MJPG" ||
                     (allWebcamFormats && (subtype == "YUY2" || subtype == "NV12" || subtype == "H264")));
                if (!interesting)
                {
                    continue;
                }

                std::printf(
                    "measuring pin %lu %ldx%ld %ld-bit %s target %.2f fps\n",
                    static_cast<unsigned long>(candidate.pinId),
                    candidate.width,
                    candidate.height,
                    candidate.bitCount,
                    subtype.c_str(),
                    targetFps);
                for (const auto& scenario : scenarios)
                {
                    measuredAny = measureScenario(filter.value, candidate, scenario) || measuredAny;
                }
            }
            return measuredAny ? 0 : 5;
        }

        printKnownControls(filter.value);

        const PinCandidate* target = nullptr;
        const PinCandidate* fastTarget = nullptr;
        for (const auto& candidate : candidates)
        {
            if (candidate.width == 640 && candidate.height == 240)
            {
                target = &candidate;
            }
            else if (candidate.width == 640 && candidate.height == 120)
            {
                fastTarget = &candidate;
            }
        }

        if (!target)
        {
            std::printf("No 640x240 Leap stereo candidate found.\n");
            return 4;
        }

        std::printf(
            "targeting pin %lu %ldx%ld %ld-bit %s target %.2f fps\n",
            static_cast<unsigned long>(target->pinId),
            target->width,
            target->height,
            target->bitCount,
            fourccOrGuid(target->subtype).c_str(),
            target->interval100ns > 0 ? 10000000.0 / static_cast<double>(target->interval100ns) : 0.0);

        const ULONG camManual = KSPROPERTY_CAMERACONTROL_FLAGS_MANUAL;
        const ULONG procManual = KSPROPERTY_VIDEOPROCAMP_FLAGS_MANUAL;
        const std::vector<ControlScenario> scenarios = {
            {"baseline", {}},
            {"zoom/exposure 10us", {{"camera.zoom/exposure", ControlSet::Camera, KSPROPERTY_CAMERACONTROL_ZOOM, 10, camManual}}},
            {"gamma off", {{"procamp.gamma", ControlSet::VideoProcAmp, KSPROPERTY_VIDEOPROCAMP_GAMMA, 0, procManual}}},
            {"hdr off", {{"procamp.contrast/hdr", ControlSet::VideoProcAmp, KSPROPERTY_VIDEOPROCAMP_CONTRAST, 0, procManual}}},
            {"leds on", {
                {"procamp.contrast/left-led", ControlSet::VideoProcAmp, KSPROPERTY_VIDEOPROCAMP_CONTRAST, 66, procManual},
                {"procamp.contrast/center-led", ControlSet::VideoProcAmp, KSPROPERTY_VIDEOPROCAMP_CONTRAST, 67, procManual},
                {"procamp.contrast/right-led", ControlSet::VideoProcAmp, KSPROPERTY_VIDEOPROCAMP_CONTRAST, 68, procManual}}},
            {"leds off", {
                {"procamp.contrast/left-led", ControlSet::VideoProcAmp, KSPROPERTY_VIDEOPROCAMP_CONTRAST, 2, procManual},
                {"procamp.contrast/center-led", ControlSet::VideoProcAmp, KSPROPERTY_VIDEOPROCAMP_CONTRAST, 3, procManual},
                {"procamp.contrast/right-led", ControlSet::VideoProcAmp, KSPROPERTY_VIDEOPROCAMP_CONTRAST, 4, procManual}}},
            {"gain minimum", {{"procamp.gain", ControlSet::VideoProcAmp, KSPROPERTY_VIDEOPROCAMP_GAIN, 16, procManual}}},
            {"digital gain minimum", {{"procamp.brightness/digital-gain", ControlSet::VideoProcAmp, KSPROPERTY_VIDEOPROCAMP_BRIGHTNESS, 0, procManual}}},
            {"old fps-ratio selector 1000", {{"procamp.gain/fps-ratio", ControlSet::VideoProcAmp, KSPROPERTY_VIDEOPROCAMP_GAIN, 0x8000 | 1000, procManual}}},
            {"dark-frame interval 0", {{"procamp.whitebalance/dark-frame", ControlSet::VideoProcAmp, KSPROPERTY_VIDEOPROCAMP_WHITEBALANCE, 0, procManual}}},
            {"fast combined", {
                {"camera.zoom/exposure", ControlSet::Camera, KSPROPERTY_CAMERACONTROL_ZOOM, 10, camManual},
                {"procamp.gamma", ControlSet::VideoProcAmp, KSPROPERTY_VIDEOPROCAMP_GAMMA, 0, procManual},
                {"procamp.contrast/hdr", ControlSet::VideoProcAmp, KSPROPERTY_VIDEOPROCAMP_CONTRAST, 0, procManual},
                {"procamp.brightness/digital-gain", ControlSet::VideoProcAmp, KSPROPERTY_VIDEOPROCAMP_BRIGHTNESS, 0, procManual},
                {"procamp.gain", ControlSet::VideoProcAmp, KSPROPERTY_VIDEOPROCAMP_GAIN, 16, procManual},
                {"procamp.whitebalance/dark-frame", ControlSet::VideoProcAmp, KSPROPERTY_VIDEOPROCAMP_WHITEBALANCE, 0, procManual}}},
        };

        bool measuredAny = false;
        for (const auto& scenario : scenarios)
        {
            measuredAny = measureScenario(filter.value, *target, scenario) || measuredAny;
        }

        if (fastTarget)
        {
            std::printf(
                "fast-mode check pin %lu %ldx%ld %ld-bit %s target %.2f fps\n",
                static_cast<unsigned long>(fastTarget->pinId),
                fastTarget->width,
                fastTarget->height,
                fastTarget->bitCount,
                fourccOrGuid(fastTarget->subtype).c_str(),
                fastTarget->interval100ns > 0 ? 10000000.0 / static_cast<double>(fastTarget->interval100ns) : 0.0);
            measuredAny = measureScenario(filter.value, *fastTarget, scenarios.front()) || measuredAny;
        }

        return measuredAny ? 0 : 5;
    }

    std::printf("target capture interface not found for %s.\n", targetVid.c_str());
    return 1;
}
