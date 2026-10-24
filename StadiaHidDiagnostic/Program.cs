using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace StadiaHidDiagnostic;

internal static class Program
{
    private const ushort StadiaVid = 0x18D1;
    private const ushort StadiaPid = 0x9400;
    private const string UsbPathToken = "VID_18D1&PID_9400";
    private const string BluetoothPathToken = "vid&0218d1_pid&9400";
    private const ushort GenericDesktopUsagePage = 0x01;
    private const ushort GamePadUsage = 0x05;

    private static StreamWriter? _log;
    private static readonly object LogLock = new();

    private sealed record HidCandidate(
        string Path,
        ushort Vid,
        ushort Pid,
        ushort Version,
        ushort UsagePage,
        ushort Usage,
        ushort InputReportLength,
        ushort OutputReportLength,
        ushort FeatureReportLength,
        bool IsStadiaPath)
    {
        public bool IsStadia => IsStadiaPath || (Vid == StadiaVid && Pid == StadiaPid);
        public string Label => IsStadia ? "STADIA CANDIDATE" : "OTHER GAMEPAD";
    }

    private static async Task<int> Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine("Stadia HID Diagnostic 0.1.0");
        Console.WriteLine("Read-only diagnostic: no driver installation, no device disabling, no ViGEm output.");
        Console.WriteLine("This tool does not intentionally open the device exclusively.");
        Console.WriteLine();

        string logDirectory = Path.Combine(AppContext.BaseDirectory, "logs");
        Directory.CreateDirectory(logDirectory);
        string logPath = Path.Combine(logDirectory, $"StadiaHidDiagnostic-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        using var log = new StreamWriter(new FileStream(logPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false))
        {
            AutoFlush = true
        };
        _log = log;
        Log($"Started: {DateTimeOffset.Now:O}");
        Log($"OS: {Environment.OSVersion}; Process: {(Environment.Is64BitProcess ? "x64" : "x86")}; .NET: {Environment.Version}");
        Log($"Expected path filters: '{UsbPathToken}' or '{BluetoothPathToken}' (case-insensitive)");

        List<string> paths;
        try
        {
            paths = EnumerateHidPaths();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"HID enumeration failed: {ex.Message}");
            Log("HID enumeration failed: " + ex);
            return 2;
        }

        Console.WriteLine($"HID interface paths enumerated: {paths.Count}");
        Log($"HID interface paths enumerated: {paths.Count}");

        var candidates = new List<HidCandidate>();
        for (int i = 0; i < paths.Count; i++)
        {
            string path = paths[i];
            bool pathMatch = IsStadiaPath(path);
            HidCandidate? candidate = TryInspect(path, pathMatch);
            if (candidate is not null && (candidate.IsStadia ||
                (candidate.UsagePage == GenericDesktopUsagePage && candidate.Usage == GamePadUsage)))
            {
                candidates.Add(candidate);
            }
        }

        foreach (HidCandidate c in candidates)
        {
            string line = $"[{c.Label}] VID:PID={c.Vid:X4}:{c.Pid:X4} Version={c.Version:X4} " +
                $"UsagePage={c.UsagePage:X4} Usage={c.Usage:X4} Reports(in/out/feature)=" +
                $"{c.InputReportLength}/{c.OutputReportLength}/{c.FeatureReportLength}\n  Path: {c.Path}";
            Console.WriteLine(line);
            Log(line.Replace("\n", Environment.NewLine));
        }

        List<HidCandidate> stadiaCandidates = candidates.Where(c => c.IsStadia).ToList();
        if (stadiaCandidates.Count == 0)
        {
            Console.WriteLine();
            Console.WriteLine("No Stadia HID path matched the reference project's VID/PID path filters.");
            Console.WriteLine("The log lists other HID gamepads (if Windows allowed their metadata to be queried).");
            Console.WriteLine("Please confirm the controller is paired and awake, then send the log for review.");
            Log("No Stadia candidate found. No non-Stadia device was opened for input reading.");
            Console.WriteLine($"Log: {logPath}");
            return 3;
        }

        int selectedIndex = 0;
        if (stadiaCandidates.Count > 1)
        {
            Console.WriteLine();
            Console.WriteLine("More than one Stadia candidate was found:");
            for (int i = 0; i < stadiaCandidates.Count; i++)
                Console.WriteLine($"  {i + 1}. {stadiaCandidates[i].Path}");
            Console.Write($"Select device number (1-{stadiaCandidates.Count}, default 1): ");
            string? selection = Console.ReadLine();
            if (int.TryParse(selection, out int number) && number >= 1 && number <= stadiaCandidates.Count)
                selectedIndex = number - 1;
        }

        HidCandidate selected = stadiaCandidates[selectedIndex];
        Console.WriteLine();
        Console.WriteLine("Reading input reports. Press buttons/move sticks/triggers one at a time.");
        Console.WriteLine("Press Ctrl+C to stop. Raw reports are saved to the log file.");
        Console.WriteLine("For this test, close Steam and other controller-remapping tools if possible; do not change adapter settings.");
        Console.WriteLine($"Selected: VID:PID={selected.Vid:X4}:{selected.Pid:X4}, input report length={selected.InputReportLength}");
        Console.WriteLine($"Log: {logPath}");
        Log($"Selected device path: {selected.Path}");

        using var cts = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;

        try
        {
            await ReadReportsAsync(selected, cts.Token);
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("Stopping...");
            Log("Read loop cancelled by user.");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Read failed: {ex.GetType().Name}: {ex.Message}");
            Log("Read failed: " + ex);
            Console.WriteLine("This can happen if the controller disconnects or another program holds the device.");
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
            Log($"Finished: {DateTimeOffset.Now:O}");
            Console.WriteLine($"Finished. Log saved to: {logPath}");
        }

        return 0;
    }

    private static bool IsStadiaPath(string path) =>
        path.Contains(UsbPathToken, StringComparison.OrdinalIgnoreCase) ||
        path.Contains(BluetoothPathToken, StringComparison.OrdinalIgnoreCase);

    private static List<string> EnumerateHidPaths()
    {
        Native.HidD_GetHidGuid(out Guid hidGuid);
        IntPtr infoSet = Native.SetupDiGetClassDevs(ref hidGuid, IntPtr.Zero, IntPtr.Zero,
            Native.DIGCF_PRESENT | Native.DIGCF_DEVICEINTERFACE);
        if (infoSet == Native.INVALID_HANDLE_VALUE)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "SetupDiGetClassDevs failed");

        var paths = new List<string>();
        try
        {
            for (uint index = 0; ; index++)
            {
                var interfaceData = new Native.SP_DEVICE_INTERFACE_DATA
                {
                    cbSize = Marshal.SizeOf<Native.SP_DEVICE_INTERFACE_DATA>()
                };
                if (!Native.SetupDiEnumDeviceInterfaces(infoSet, IntPtr.Zero, ref hidGuid, index, ref interfaceData))
                {
                    int error = Marshal.GetLastWin32Error();
                    if (error == Native.ERROR_NO_MORE_ITEMS)
                        break;
                    throw new Win32Exception(error, "SetupDiEnumDeviceInterfaces failed");
                }

                _ = Native.SetupDiGetDeviceInterfaceDetail(infoSet, ref interfaceData, IntPtr.Zero, 0,
                    out uint requiredSize, IntPtr.Zero);
                int detailError = Marshal.GetLastWin32Error();
                if (requiredSize == 0 || (detailError != Native.ERROR_INSUFFICIENT_BUFFER && detailError != 0))
                    continue;

                IntPtr detail = Marshal.AllocHGlobal(checked((int)requiredSize));
                try
                {
                    // SP_DEVICE_INTERFACE_DETAIL_DATA_W: cbSize is 8 on x64 and 6 on x86;
                    // the UTF-16 DevicePath begins at byte offset 4 on both architectures.
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                    if (Native.SetupDiGetDeviceInterfaceDetail(infoSet, ref interfaceData, detail,
                        requiredSize, out _, IntPtr.Zero))
                    {
                        string? path = Marshal.PtrToStringUni(IntPtr.Add(detail, 4));
                        if (!string.IsNullOrWhiteSpace(path))
                            paths.Add(path);
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(detail);
                }
            }
        }
        finally
        {
            Native.SetupDiDestroyDeviceInfoList(infoSet);
        }

        return paths;
    }

    private static HidCandidate? TryInspect(string path, bool pathMatch)
    {
        // Metadata-only handle: no input read is started during enumeration.
        using SafeFileHandle handle = Native.CreateFile(path, Native.GENERIC_READ,
            Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE, IntPtr.Zero,
            Native.OPEN_EXISTING, Native.FILE_ATTRIBUTE_NORMAL, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            if (pathMatch)
                Log($"Matched path but could not open metadata handle. Win32={Marshal.GetLastWin32Error()}; Path={path}");
            return null;
        }

        var attributes = new Native.HIDD_ATTRIBUTES { Size = Marshal.SizeOf<Native.HIDD_ATTRIBUTES>() };
        if (!Native.HidD_GetAttributes(handle, ref attributes))
        {
            if (pathMatch)
                Log($"Matched path but HidD_GetAttributes failed. Win32={Marshal.GetLastWin32Error()}; Path={path}");
            return null;
        }

        if (!Native.HidD_GetPreparsedData(handle, out IntPtr preparsed))
        {
            if (pathMatch)
                Log($"Matched path but HidD_GetPreparsedData failed. Win32={Marshal.GetLastWin32Error()}; Path={path}");
            return null;
        }

        try
        {
            var caps = new Native.HIDP_CAPS { Reserved = new ushort[17] };
            int status = Native.HidP_GetCaps(preparsed, ref caps);
            if (status != Native.HIDP_STATUS_SUCCESS)
            {
                if (pathMatch)
                    Log($"Matched path but HidP_GetCaps failed. NTSTATUS=0x{status:X8}; Path={path}");
                return null;
            }

            return new HidCandidate(path, attributes.VendorID, attributes.ProductID,
                attributes.VersionNumber, caps.UsagePage, caps.Usage,
                caps.InputReportByteLength, caps.OutputReportByteLength,
                caps.FeatureReportByteLength, pathMatch);
        }
        finally
        {
            Native.HidD_FreePreparsedData(preparsed);
        }
    }

    private static async Task ReadReportsAsync(HidCandidate candidate, CancellationToken cancellationToken)
    {
        if (candidate.InputReportLength < 1)
            throw new InvalidOperationException("The HID descriptor reports an invalid input-report length.");

        using SafeFileHandle handle = Native.CreateFile(candidate.Path, Native.GENERIC_READ,
            Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE, IntPtr.Zero,
            Native.OPEN_EXISTING, Native.FILE_FLAG_OVERLAPPED, IntPtr.Zero);
        if (handle.IsInvalid)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not open Stadia HID device for shared reading");

        await using var stream = new FileStream(handle, FileAccess.Read,
            candidate.InputReportLength, isAsync: true);

        byte[] buffer = new byte[candidate.InputReportLength];
        byte[]? lastControlBytes = null;
        long reportCount = 0;
        var stopwatch = Stopwatch.StartNew();
        Log($"Begin read. InputReportByteLength={candidate.InputReportLength}; shared access; overlapped I/O.");

        while (!cancellationToken.IsCancellationRequested)
        {
            int count = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
            if (count <= 0)
                throw new IOException("HID read returned zero bytes; device may have disconnected.");

            reportCount++;
            DateTimeOffset now = DateTimeOffset.Now;
            string hex = Convert.ToHexString(buffer.AsSpan(0, count));
            Log($"REPORT #{reportCount} {now:HH:mm:ss.fff} len={count} hex={hex}");

            // Console output is limited to changes in the first 10 bytes to avoid flooding
            // if a device/firmware adds unrelated trailing fields that change continuously.
            int controlLength = Math.Min(count, 10);
            bool controlChanged = lastControlBytes is null || lastControlBytes.Length != controlLength ||
                !buffer.AsSpan(0, controlLength).SequenceEqual(lastControlBytes);
            if (controlChanged)
            {
                Console.WriteLine($"[{now:HH:mm:ss.fff}] len={count} raw={hex}");
                if (count >= 10 && buffer[0] == 0x03)
                    Console.WriteLine("  tentative decode: " + DecodeReferenceLayout(buffer));
                else
                    Console.WriteLine($"  reportId={(count > 0 ? $"0x{buffer[0]:X2}" : "n/a")} (no tentative decode; expected ID 0x03 and at least 10 bytes)");
                lastControlBytes = buffer.AsSpan(0, controlLength).ToArray();
            }

            if (reportCount % 500 == 0)
            {
                Console.WriteLine($"  ...{reportCount} reports read over {stopwatch.Elapsed.TotalSeconds:F1}s (full report stream is in the log)");
            }
        }
    }

    private static string DecodeReferenceLayout(byte[] b)
    {
        string dpad = b[1] switch
        {
            0 => "Up", 1 => "Up+Right", 2 => "Right", 3 => "Right+Down",
            4 => "Down", 5 => "Down+Left", 6 => "Left", 7 => "Left+Up",
            _ => "Neutral"
        };
        var buttons = new List<string>();
        if ((b[2] & 0x80) != 0) buttons.Add("RS");
        if ((b[2] & 0x40) != 0) buttons.Add("Options");
        if ((b[2] & 0x20) != 0) buttons.Add("Menu");
        if ((b[2] & 0x10) != 0) buttons.Add("Stadia");
        if ((b[3] & 0x40) != 0) buttons.Add("A");
        if ((b[3] & 0x20) != 0) buttons.Add("B");
        if ((b[3] & 0x10) != 0) buttons.Add("X");
        if ((b[3] & 0x08) != 0) buttons.Add("Y");
        if ((b[3] & 0x04) != 0) buttons.Add("LB");
        if ((b[3] & 0x02) != 0) buttons.Add("RB");
        if ((b[3] & 0x01) != 0) buttons.Add("LS");
        return $"DPad={dpad}; Buttons={(buttons.Count == 0 ? "none" : string.Join(',', buttons))}; " +
            $"LX={b[4]} LY={b[5]} RX={b[6]} RY={b[7]} LT={b[8]} RT={b[9]}";
    }

    private static void Log(string message)
    {
        lock (LogLock)
        {
            _log?.WriteLine($"[{DateTimeOffset.Now:O}] {message}");
        }
    }

    private static class Native
    {
        internal const uint DIGCF_PRESENT = 0x00000002;
        internal const uint DIGCF_DEVICEINTERFACE = 0x00000010;
        internal const int ERROR_INSUFFICIENT_BUFFER = 122;
        internal const int ERROR_NO_MORE_ITEMS = 259;
        internal const int HIDP_STATUS_SUCCESS = 0x00110000;
        internal const uint GENERIC_READ = 0x80000000;
        internal const uint FILE_SHARE_READ = 0x00000001;
        internal const uint FILE_SHARE_WRITE = 0x00000002;
        internal const uint OPEN_EXISTING = 3;
        internal const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;
        internal const uint FILE_FLAG_OVERLAPPED = 0x40000000;
        internal static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

        [StructLayout(LayoutKind.Sequential)]
        internal struct SP_DEVICE_INTERFACE_DATA
        {
            public int cbSize;
            public Guid InterfaceClassGuid;
            public int Flags;
            public UIntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct HIDD_ATTRIBUTES
        {
            public int Size;
            public ushort VendorID;
            public ushort ProductID;
            public ushort VersionNumber;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct HIDP_CAPS
        {
            public ushort Usage;
            public ushort UsagePage;
            public ushort InputReportByteLength;
            public ushort OutputReportByteLength;
            public ushort FeatureReportByteLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
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

        [DllImport("hid.dll")]
        internal static extern void HidD_GetHidGuid(out Guid HidGuid);

        [DllImport("hid.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool HidD_GetAttributes(SafeFileHandle HidDeviceObject, ref HIDD_ATTRIBUTES Attributes);

        [DllImport("hid.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool HidD_GetPreparsedData(SafeFileHandle HidDeviceObject, out IntPtr PreparsedData);

        [DllImport("hid.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool HidD_FreePreparsedData(IntPtr PreparsedData);

        [DllImport("hid.dll")]
        internal static extern int HidP_GetCaps(IntPtr PreparsedData, ref HIDP_CAPS Capabilities);

        [DllImport("setupapi.dll", SetLastError = true)]
        internal static extern IntPtr SetupDiGetClassDevs(ref Guid ClassGuid, IntPtr Enumerator, IntPtr hwndParent, uint Flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetupDiEnumDeviceInterfaces(IntPtr DeviceInfoSet, IntPtr DeviceInfoData,
            ref Guid InterfaceClassGuid, uint MemberIndex, ref SP_DEVICE_INTERFACE_DATA DeviceInterfaceData);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "SetupDiGetDeviceInterfaceDetailW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr DeviceInfoSet,
            ref SP_DEVICE_INTERFACE_DATA DeviceInterfaceData, IntPtr DeviceInterfaceDetailData,
            uint DeviceInterfaceDetailDataSize, out uint RequiredSize, IntPtr DeviceInfoData);

        [DllImport("setupapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetupDiDestroyDeviceInfoList(IntPtr DeviceInfoSet);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
        internal static extern SafeFileHandle CreateFile(string FileName, uint DesiredAccess, uint ShareMode,
            IntPtr SecurityAttributes, uint CreationDisposition, uint FlagsAndAttributes, IntPtr TemplateFile);
    }
}
