using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

const ushort SonyVid = 0x054C;
const ushort PsMovePid = 0x03D5;

var readReports = args.Any(arg => string.Equals(arg, "--read", StringComparison.OrdinalIgnoreCase));
var pairHost = TryGetOption(args, "--pair-host");
var showAll = args.Any(arg => string.Equals(arg, "--all", StringComparison.OrdinalIgnoreCase));
if (showAll)
{
    foreach (var path in WindowsHid.EnumeratePaths())
    {
        Console.WriteLine(path);
    }

    return 0;
}

var devices = WindowsHid.Enumerate()
    .Where(device => device.VendorId == SonyVid && device.ProductId == PsMovePid)
    .OrderBy(device => device.Path, StringComparer.OrdinalIgnoreCase)
    .ToList();

Console.WriteLine("Mimir PS Move USB probe");
Console.WriteLine($"target vid=0x{SonyVid:X4} pid=0x{PsMovePid:X4}");
Console.WriteLine($"collections={devices.Count}");
Console.WriteLine();

if (devices.Count == 0)
{
    Console.WriteLine("No PS Move HID collections are visible. Check the USB cable and Windows Device Manager.");
    return 1;
}

foreach (var device in devices)
{
    PrintDevice(device);

    if (!string.IsNullOrWhiteSpace(pairHost))
    {
        var result = WindowsHid.TryPairHost(device, pairHost);
        Console.WriteLine($"  pairHost={result}");
    }

    if (readReports)
    {
        var result = await WindowsHid.TryReadInputReportAsync(device, TimeSpan.FromMilliseconds(350));
        Console.WriteLine($"  read={result}");
    }

    Console.WriteLine();
}

return 0;

static void PrintDevice(HidDeviceInfo device)
{
    Console.WriteLine(device.Path);
    Console.WriteLine($"  vid=0x{device.VendorId:X4} pid=0x{device.ProductId:X4}");
    Console.WriteLine($"  manufacturer={device.Manufacturer ?? "(unreported)"}");
    Console.WriteLine($"  product={device.Product ?? "(unreported)"}");
    Console.WriteLine($"  serial={device.SerialNumber ?? "(unreported)"}");
    Console.WriteLine($"  usagePage=0x{device.Caps.UsagePage:X4} usage=0x{device.Caps.Usage:X4}");
    Console.WriteLine($"  reports input={device.Caps.InputReportByteLength} output={device.Caps.OutputReportByteLength} feature={device.Caps.FeatureReportByteLength}");
    Console.WriteLine($"  buttons in/out/feature={device.Caps.NumberInputButtonCaps}/{device.Caps.NumberOutputButtonCaps}/{device.Caps.NumberFeatureButtonCaps}");
    Console.WriteLine($"  values in/out/feature={device.Caps.NumberInputValueCaps}/{device.Caps.NumberOutputValueCaps}/{device.Caps.NumberFeatureValueCaps}");
}

static string? TryGetOption(string[] values, string name)
{
    for (var index = 0; index < values.Length; index++)
    {
        if (string.Equals(values[index], name, StringComparison.OrdinalIgnoreCase) && index + 1 < values.Length)
        {
            return values[index + 1];
        }

        if (values[index].StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
        {
            return values[index][(name.Length + 1)..];
        }
    }

    return null;
}

internal sealed record HidDeviceInfo(
    string Path,
    ushort VendorId,
    ushort ProductId,
    string? Manufacturer,
    string? Product,
    string? SerialNumber,
    HidpCaps Caps);

internal static class WindowsHid
{
    private const int DigcfPresent = 0x00000002;
    private const int DigcfDeviceinterface = 0x00000010;
    private const uint GenericRead = 0x80000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const uint FileFlagOverlapped = 0x40000000;
    private const uint ErrorIoPending = 997;
    private const uint WaitObject0 = 0;
    private const uint WaitTimeout = 258;
    private const int HidpStatusSuccess = 0x00110000;
    private const byte GetBtAddressReport = 0x04;
    private const byte SetBtAddressReport = 0x05;
    private const int BtAddressGetSize = 16;
    private const int BtAddressSetSize = 23;

    public static IEnumerable<HidDeviceInfo> Enumerate()
    {
        foreach (var path in EnumeratePaths())
        {
            if (TryProbe(path, out var device))
            {
                yield return device;
            }
        }
    }

    public static IEnumerable<string> EnumeratePaths()
    {
        HidD_GetHidGuid(out var hidGuid);

        var deviceInfoSet = SetupDiGetClassDevs(ref hidGuid, null, IntPtr.Zero, DigcfPresent | DigcfDeviceinterface);
        if (deviceInfoSet == IntPtr.Zero || deviceInfoSet.ToInt64() == -1)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "SetupDiGetClassDevs failed.");
        }

        try
        {
            var interfaceData = new SpDeviceInterfaceData
            {
                CbSize = Marshal.SizeOf<SpDeviceInterfaceData>()
            };

            for (uint index = 0; SetupDiEnumDeviceInterfaces(deviceInfoSet, IntPtr.Zero, ref hidGuid, index, ref interfaceData); index++)
            {
                yield return GetDevicePath(deviceInfoSet, interfaceData);
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(deviceInfoSet);
        }
    }

    public static async Task<string> TryReadInputReportAsync(HidDeviceInfo device, TimeSpan timeout)
    {
        if (device.Caps.InputReportByteLength == 0)
        {
            return "skipped: no input report";
        }

        using var handle = CreateFile(
            device.Path,
            GenericRead,
            FileShareRead | FileShareWrite,
            IntPtr.Zero,
            OpenExisting,
            FileFlagOverlapped,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            return $"open failed: {new Win32Exception(Marshal.GetLastWin32Error()).Message}";
        }

        await Task.Yield();
        var buffer = new byte[device.Caps.InputReportByteLength];
        var readEvent = CreateEvent(IntPtr.Zero, true, false, null);

        if (readEvent == IntPtr.Zero)
        {
            return $"CreateEvent failed: {new Win32Exception(Marshal.GetLastWin32Error()).Message}";
        }

        var overlapped = new NativeOverlapped
        {
            EventHandle = readEvent
        };

        try
        {
            var started = ReadFile(handle, buffer, (uint)buffer.Length, IntPtr.Zero, ref overlapped);
            if (!started)
            {
                var error = (uint)Marshal.GetLastWin32Error();
                if (error != ErrorIoPending)
                {
                    return $"ReadFile failed: {new Win32Exception((int)error).Message}";
                }
            }

            var wait = WaitForSingleObject(readEvent, (uint)Math.Ceiling(timeout.TotalMilliseconds));
            if (wait == WaitTimeout)
            {
                CancelIoEx(handle, ref overlapped);
                return $"timeout after {timeout.TotalMilliseconds:0}ms";
            }

            if (wait != WaitObject0)
            {
                CancelIoEx(handle, ref overlapped);
                return $"wait failed: 0x{wait:X8}";
            }

            if (!GetOverlappedResult(handle, ref overlapped, out var read, false))
            {
                return $"GetOverlappedResult failed: {new Win32Exception(Marshal.GetLastWin32Error()).Message}";
            }

            var shown = Convert.ToHexString(buffer.AsSpan(0, (int)Math.Min(read, 32)));
            return $"{read} bytes {shown}{(read > 32 ? "..." : string.Empty)}";
        }
        finally
        {
            CloseHandle(readEvent);
        }
    }

    public static string TryPairHost(HidDeviceInfo device, string hostAddress)
    {
        if (device.Caps.FeatureReportByteLength < BtAddressSetSize || device.Path.IndexOf("&col02#", StringComparison.OrdinalIgnoreCase) < 0)
        {
            return "skipped: pairing uses the PS Move col02 feature-report collection";
        }

        if (!TryParseBluetoothAddress(hostAddress, out var host))
        {
            return $"invalid host address: {hostAddress}";
        }

        using var handle = CreateFile(
            device.Path,
            0,
            FileShareRead | FileShareWrite,
            IntPtr.Zero,
            OpenExisting,
            0,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            return $"open failed: {new Win32Exception(Marshal.GetLastWin32Error()).Message}";
        }

        var before = ReadBluetoothAddresses(handle);
        var report = new byte[BtAddressSetSize];
        report[0] = SetBtAddressReport;
        host.CopyTo(report.AsSpan(1, 6));

        if (!HidD_SetFeature(handle, report, report.Length))
        {
            return $"HidD_SetFeature failed: {new Win32Exception(Marshal.GetLastWin32Error()).Message}";
        }

        var after = ReadBluetoothAddresses(handle);
        var controller = after.Controller ?? before.Controller ?? "(unknown)";
        var assigned = after.Host ?? FormatBluetoothAddress(host);
        return $"ok controller={controller} host={assigned}";
    }

    private static (string? Controller, string? Host) ReadBluetoothAddresses(SafeFileHandle handle)
    {
        var report = new byte[BtAddressGetSize];
        report[0] = GetBtAddressReport;
        if (!HidD_GetFeature(handle, report, report.Length))
        {
            return (null, null);
        }

        return (FormatBluetoothAddress(report.AsSpan(1, 6)), FormatBluetoothAddress(report.AsSpan(10, 6)));
    }

    private static bool TryParseBluetoothAddress(string value, out byte[] bytes)
    {
        bytes = new byte[6];
        var parts = value.Split(':', '-');
        if (parts.Length != 6)
        {
            return false;
        }

        for (var index = 0; index < parts.Length; index++)
        {
            if (!byte.TryParse(parts[index], System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
            {
                return false;
            }

            bytes[5 - index] = parsed;
        }

        return true;
    }

    private static string FormatBluetoothAddress(ReadOnlySpan<byte> littleEndianAddress)
    {
        return string.Join(
            ":",
            littleEndianAddress.ToArray().Reverse().Select(value => value.ToString("x2", System.Globalization.CultureInfo.InvariantCulture)));
    }

    private static string GetDevicePath(IntPtr deviceInfoSet, SpDeviceInterfaceData interfaceData)
    {
        SetupDiGetDeviceInterfaceDetail(deviceInfoSet, ref interfaceData, IntPtr.Zero, 0, out var requiredSize, IntPtr.Zero);
        if (requiredSize == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "SetupDiGetDeviceInterfaceDetail size query failed.");
        }

        var detailData = Marshal.AllocHGlobal((int)requiredSize);
        try
        {
            Marshal.WriteInt32(detailData, IntPtr.Size == 8 ? 8 : 6);
            if (!SetupDiGetDeviceInterfaceDetail(deviceInfoSet, ref interfaceData, detailData, requiredSize, out _, IntPtr.Zero))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "SetupDiGetDeviceInterfaceDetail failed.");
            }

            return Marshal.PtrToStringUni(IntPtr.Add(detailData, 4))
                ?? throw new InvalidOperationException("HID device path was empty.");
        }
        finally
        {
            Marshal.FreeHGlobal(detailData);
        }
    }

    private static bool TryProbe(string path, out HidDeviceInfo device)
    {
        device = default!;
        if (!TryExtractVidPid(path, out var vid, out var pid))
        {
            return false;
        }

        using var handle = CreateFile(
            path,
            0,
            FileShareRead | FileShareWrite,
            IntPtr.Zero,
            OpenExisting,
            0,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            return false;
        }

        if (!HidD_GetPreparsedData(handle, out var preparsedData))
        {
            return false;
        }

        try
        {
            var status = HidP_GetCaps(preparsedData, out var caps);
            if (status != HidpStatusSuccess)
            {
                return false;
            }

            device = new HidDeviceInfo(
                path,
                vid,
                pid,
                GetHidString(handle, HidD_GetManufacturerString),
                GetHidString(handle, HidD_GetProductString),
                GetHidString(handle, HidD_GetSerialNumberString),
                caps);
            return true;
        }
        finally
        {
            HidD_FreePreparsedData(preparsedData);
        }
    }

    private static bool TryExtractVidPid(string path, out ushort vid, out ushort pid)
    {
        vid = 0;
        pid = 0;
        var lower = path.ToLowerInvariant();
        return TryExtractHexAfter(lower, "vid_", out vid) && TryExtractHexAfter(lower, "pid_", out pid);
    }

    private static bool TryExtractHexAfter(string value, string marker, out ushort result)
    {
        result = 0;
        var index = value.IndexOf(marker, StringComparison.Ordinal);
        if (index < 0 || index + marker.Length + 4 > value.Length)
        {
            return false;
        }

        return ushort.TryParse(
            value.AsSpan(index + marker.Length, 4),
            System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture,
            out result);
    }

    private delegate bool HidStringGetter(SafeFileHandle hidDeviceObject, byte[] buffer, int bufferLength);

    private static string? GetHidString(SafeFileHandle handle, HidStringGetter getter)
    {
        var buffer = new byte[256];
        return getter(handle, buffer, buffer.Length)
            ? Marshal.PtrToStringUni(Marshal.UnsafeAddrOfPinnedArrayElement(buffer, 0))?.TrimEnd('\0')
            : null;
    }

    [DllImport("hid.dll")]
    private static extern void HidD_GetHidGuid(out Guid hidGuid);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_GetPreparsedData(SafeFileHandle hidDeviceObject, out IntPtr preparsedData);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_FreePreparsedData(IntPtr preparsedData);

    [DllImport("hid.dll")]
    private static extern int HidP_GetCaps(IntPtr preparsedData, out HidpCaps capabilities);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_GetManufacturerString(SafeFileHandle hidDeviceObject, byte[] buffer, int bufferLength);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_GetProductString(SafeFileHandle hidDeviceObject, byte[] buffer, int bufferLength);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_GetSerialNumberString(SafeFileHandle hidDeviceObject, byte[] buffer, int bufferLength);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_GetFeature(SafeFileHandle hidDeviceObject, byte[] reportBuffer, int reportBufferLength);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_SetFeature(SafeFileHandle hidDeviceObject, byte[] reportBuffer, int reportBufferLength);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(
        ref Guid classGuid,
        string? enumerator,
        IntPtr hwndParent,
        int flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(
        IntPtr deviceInfoSet,
        IntPtr deviceInfoData,
        ref Guid interfaceClassGuid,
        uint memberIndex,
        ref SpDeviceInterfaceData deviceInterfaceData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(
        IntPtr deviceInfoSet,
        ref SpDeviceInterfaceData deviceInterfaceData,
        IntPtr deviceInterfaceDetailData,
        uint deviceInterfaceDetailDataSize,
        out uint requiredSize,
        IntPtr deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateEvent(
        IntPtr eventAttributes,
        bool manualReset,
        bool initialState,
        string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadFile(
        SafeFileHandle file,
        byte[] buffer,
        uint numberOfBytesToRead,
        IntPtr numberOfBytesRead,
        ref NativeOverlapped overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetOverlappedResult(
        SafeFileHandle file,
        ref NativeOverlapped overlapped,
        out uint numberOfBytesTransferred,
        bool wait);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CancelIoEx(
        SafeFileHandle file,
        ref NativeOverlapped overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDeviceInterfaceData
    {
        public int CbSize;
        public Guid InterfaceClassGuid;
        public int Flags;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeOverlapped
    {
        public IntPtr InternalLow;
        public IntPtr InternalHigh;
        public uint OffsetLow;
        public uint OffsetHigh;
        public IntPtr EventHandle;
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct HidpCaps
{
    public ushort Usage;
    public ushort UsagePage;
    public ushort InputReportByteLength;
    public ushort OutputReportByteLength;
    public ushort FeatureReportByteLength;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
    public ushort[] Reserved;

    public ushort NumberLinkCollectionNodes;
    public ushort NumberInputButtonCaps;
    public ushort NumberInputValueCaps;
    public ushort NumberInputDataIndices;
    public ushort NumberOutputButtonCaps;
    public ushort NumberOutputValueCaps;
    public ushort NumberOutputDataIndices;
    public ushort NumberFeatureButtonCaps;
    public ushort NumberFeatureValueCaps;
    public ushort NumberFeatureDataIndices;
}
