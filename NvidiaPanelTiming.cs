using System.IO;
using System.Runtime.InteropServices;

internal static class NvidiaPanelTiming
{
    private const uint InfoTypeDdsStatus = 0x17;

    private static string? ResolveNvCplPath()
    {
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string[] searchPaths =
        {
            Path.Combine(programFiles, "NVIDIA Corporation", "NVIDIA App", "NvCpl", "nvcpl.dll"),
            Path.Combine(programFiles, "NVIDIA Corporation", "Control Panel Client", "nvcpl.dll"),
            Path.Combine(Environment.SystemDirectory, "nvcpl.dll")
        };

        foreach (string path in searchPaths)
        {
            if (File.Exists(path))
                return path;
        }
        return null;
    }

    /// <summary>
    /// Queries NVIDIA Advanced Optimus (DDS) live MUX state via nvcpl.dll (matching PreySense's NvidiaDdsService).
    /// Returns true if DDS reports a valid active MUX (MuxState 1 = Optimus/Intel, 2 = dGPU/NVIDIA).
    /// </summary>
    public static bool TryGetDdsActiveMux(out bool isNvidia)
    {
        isNvidia = false;
        string? dllPath = ResolveNvCplPath();
        if (dllPath == null)
            return false;

        nint lib = Native.LoadLibrary(dllPath);
        if (lib == 0)
            return false;

        try
        {
            var init = TryBind<InitFn>(lib, "NvCplApiInit");
            var close = TryBind<CloseFn>(lib, "NvCplApiClose");
            var getInfo = TryBind<GetInfoFn>(lib, "NvCplApiGetInfo");
            if (init == null || getInfo == null)
                return false;

            if (init() != 0)
                return false;

            try
            {
                ulong size = 16;
                nint pStatus = Marshal.AllocHGlobal(16);
                try
                {
                    for (int i = 0; i < 16; i++)
                        Marshal.WriteByte(pStatus, i, 0);

                    if (getInfo(InfoTypeDdsStatus, 0, pStatus, ref size) != 0)
                        return false;

                    var info = Marshal.PtrToStructure<NativeDdsStatusInfo>(pStatus);
                    if (info.IsSupported == 0 || info.KmdResponse != 0)
                        return false;

                    if (info.MuxState == 2)
                    {
                        isNvidia = true;
                        return true;
                    }
                    if (info.MuxState == 1)
                    {
                        isNvidia = false;
                        return true;
                    }
                    return false;
                }
                finally
                {
                    Marshal.FreeHGlobal(pStatus);
                }
            }
            finally
            {
                try { close?.Invoke(); } catch { }
            }
        }
        finally
        {
            Native.FreeLibrary(lib);
        }
    }

    public static int Apply(IReadOnlyList<int> rates, IReadOnlyList<byte[]> descriptors, string? deviceName = null)
    {
        if (rates.Count != descriptors.Count || rates.Count == 0)
        {
            ProbeLog.Line("No NVIDIA timings to send.");
            return 1;
        }

        string? dllPath = ResolveNvCplPath();
        nint lib = dllPath != null ? Native.LoadLibrary(dllPath) : 0;
        if (lib == 0)
        {
            ProbeLog.Line("NVIDIA control panel library did not load. Nothing was written.");
            return 1;
        }

        string? targetDevice = string.IsNullOrWhiteSpace(deviceName) ? null : deviceName;

        try
        {
            var init = Bind<InitFn>(lib, "NvCplApiInit");
            var close = TryBind<CloseFn>(lib, "NvCplApiClose");
            var test = Bind<TestFn>(lib, "NvCplApiCTTest");
            var clear = Bind<AddressFn>(lib, "NvCplApiClearAllUnsupportedResolutionsFromCacheForDisplay");
            var flush = Bind<AddressFn>(lib, "NvCplApiFlushUnsupportedResolutionCacheToStorage");

            int rc = init();
            ProbeLog.Line("NvCplApiInit " + rc);
            if (rc != 0)
                return 1;

            try
            {
                Desktop before = CurrentMode(targetDevice);
                ProbeLog.Line("Desktop before NVIDIA timings " + before.Width + "x" + before.Height + " @ " + before.Hz + " Hz, signal " + CcdSignal.DescribeAllTargets());

                var address = new Address { Gpu = 0, Display = 0 };
                int failures = 0;
                int saved = 0;
                for (int i = 0; i < rates.Count; i++)
                {
                    Timing timing = FromDescriptor(rates[i], descriptors[i]);
                    int tested = test(ref address, 1, ref timing);
                    string signal = CcdSignal.DescribeAllTargets();
                    int persisted = test(ref address, 6, ref timing);
                    Desktop now = CurrentMode(targetDevice);
                    ProbeLog.Line(rates[i] + " Hz  test " + tested + ", save " + persisted
                        + ", clock " + timing.PixelClock.ToString("0.000") + " MHz, total "
                        + timing.HTotal + "x" + timing.VTotal + ", signal " + signal
                        + ", desktop " + now.Width + "x" + now.Height + " @ " + now.Hz);
                    if (tested != 0 || persisted != 0)
                        failures++;
                    else
                        saved++;

                    if (now.Hz != before.Hz || now.Width != before.Width || now.Height != before.Height)
                    {
                        int restored = Restore(targetDevice, before);
                        Desktop after = CurrentMode(targetDevice);
                        ProbeLog.Line("Restored desktop " + restored + ", now " + after.Width + "x" + after.Height + " @ " + after.Hz);
                    }
                }

                int returned = RestoreRefresh(targetDevice, before, 240);
                ProbeLog.Line("Desktop returned to 240 Hz, result " + returned + ", signal " + CcdSignal.DescribeAllTargets());

                if (saved > 0)
                {
                    int cleared = clear(ref address);
                    int flushed = flush(ref address);
                    ProbeLog.Line("Cleared NVIDIA unsupported-resolution cache " + cleared + ", flush " + flushed + ". That cache is what turns VRR off.");
                }

                if (failures != 0)
                {
                    ProbeLog.Line(failures + " NVIDIA timing(s) were not accepted. Leave the NVIDIA GPU driving the panel and stage again.");
                    return 1;
                }

                ProbeLog.Line("NVIDIA accepted " + string.Join(", ", rates) + " Hz at the panel pixel clock.");
                return 0;
            }
            finally
            {
                try { close?.Invoke(); } catch { }
            }
        }
        finally
        {
            Native.FreeLibrary(lib);
        }
    }

    private static Timing FromDescriptor(int hz, byte[] descriptor)
    {
        int clockKhz = 1 + descriptor[0] + (descriptor[1] << 8) + (descriptor[2] << 16);
        int hActive = 1 + descriptor[4] + (descriptor[5] << 8);
        int hBlank = 1 + descriptor[6] + (descriptor[7] << 8);
        int hFront = 1 + descriptor[8] + ((descriptor[9] & 0x7F) << 8);
        int hSync = 1 + descriptor[10] + (descriptor[11] << 8);
        bool hPositive = (descriptor[9] & 0x80) != 0;
        int vActive = 1 + descriptor[12] + (descriptor[13] << 8);
        int vBlank = 1 + descriptor[14] + (descriptor[15] << 8);
        int vFront = 1 + descriptor[16] + ((descriptor[17] & 0x7F) << 8);
        int vSync = 1 + descriptor[18] + (descriptor[19] << 8);
        bool vPositive = (descriptor[17] & 0x80) != 0;

        return new Timing
        {
            CbSize = 0x64,
            Width = (uint)hActive,
            Height = (uint)vActive,
            Flags = 0x20,
            RefreshInt = (uint)hz,
            FrontX = (uint)hActive,
            FrontY = (uint)vActive,
            Scaling = 0,
            Formula = 5,
            BackX = (uint)hActive,
            BackY = (uint)vActive,
            Interlaced = 0,
            RequestedRefresh = hz,
            ActualRefresh = hz,
            Hfp = (uint)hFront,
            HTotal = (uint)(hActive + hBlank),
            HSync = (uint)hSync,
            HPol = hPositive ? 0u : 1u,
            Vfp = (uint)vFront,
            VTotal = (uint)(vActive + vBlank),
            VSync = (uint)vSync,
            VPol = vPositive ? 0u : 1u,
            PixelClock = clockKhz / 1000f,
            Ce = 0
        };
    }

    private static T? TryBind<T>(nint module, string name) where T : Delegate
    {
        nint address = Native.GetProcAddress(module, name);
        return address == 0 ? null : Marshal.GetDelegateForFunctionPointer<T>(address);
    }

    private static T Bind<T>(nint module, string name) where T : Delegate
        => TryBind<T>(module, name) ?? throw new InvalidOperationException("nvcpl.dll is missing " + name);

    private static Desktop CurrentMode(string? deviceName)
    {
        var mode = new Native.DevMode { dmSize = (short)Marshal.SizeOf<Native.DevMode>() };
        Native.EnumDisplaySettings(deviceName, Native.EnumCurrentSettings, ref mode);
        return new Desktop(mode.dmPelsWidth, mode.dmPelsHeight, mode.dmDisplayFrequency, mode);
    }

    private static int Restore(string? deviceName, Desktop desktop)
    {
        Native.DevMode mode = desktop.Raw;
        return Native.ChangeDisplaySettingsEx(deviceName, ref mode, 0, 0, 0);
    }

    private static int RestoreRefresh(string? deviceName, Desktop before, int hz)
    {
        int targetW = before.Width > 0 ? before.Width : 2560;
        int targetH = before.Height > 0 ? before.Height : 1600;
        Native.DevMode? fallback = null;

        for (int i = 0; ; i++)
        {
            var mode = new Native.DevMode { dmSize = (short)Marshal.SizeOf<Native.DevMode>() };
            if (!Native.EnumDisplaySettings(deviceName, i, ref mode))
                break;
            if (mode.dmPelsWidth == targetW && mode.dmPelsHeight == targetH && mode.dmDisplayFrequency == hz)
            {
                fallback ??= mode;
                if (mode.dmBitsPerPel == before.Raw.dmBitsPerPel
                    && mode.dmDisplayOrientation == before.Raw.dmDisplayOrientation
                    && mode.dmDisplayFixedOutput == before.Raw.dmDisplayFixedOutput)
                {
                    return Native.ChangeDisplaySettingsEx(deviceName, ref mode, 0, 0, 0);
                }
            }
        }

        if (fallback is Native.DevMode fb)
            return Native.ChangeDisplaySettingsEx(deviceName, ref fb, 0, 0, 0);
        return Restore(deviceName, before);
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int InitFn();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int CloseFn();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetInfoFn(uint infoType, uint param, nint pData, ref ulong pSize);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int AddressFn(ref Address address);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int TestFn(ref Address address, int op, ref Timing timing);

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct NativeDdsStatusInfo
    {
        public byte IsSupported;
        public byte IsAutomatic;
        public byte Reserved1;
        public byte Reserved2;
        public uint MuxState;
        public uint KmdResponse;
        public uint SrType;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Address
    {
        public uint Gpu;
        public uint Display;
    }

    [StructLayout(LayoutKind.Explicit, Size = 0x64)]
    private struct Timing
    {
        [FieldOffset(0x00)] public uint CbSize;
        [FieldOffset(0x04)] public uint Width;
        [FieldOffset(0x08)] public uint Height;
        [FieldOffset(0x0C)] public uint Flags;
        [FieldOffset(0x10)] public uint RefreshInt;
        [FieldOffset(0x14)] public uint FrontX;
        [FieldOffset(0x18)] public uint FrontY;
        [FieldOffset(0x1C)] public uint Scaling;
        [FieldOffset(0x20)] public uint Formula;
        [FieldOffset(0x24)] public uint BackX;
        [FieldOffset(0x28)] public uint BackY;
        [FieldOffset(0x2C)] public uint Interlaced;
        [FieldOffset(0x30)] public float RequestedRefresh;
        [FieldOffset(0x34)] public float ActualRefresh;
        [FieldOffset(0x38)] public uint Hfp;
        [FieldOffset(0x3C)] public uint HTotal;
        [FieldOffset(0x40)] public uint HSync;
        [FieldOffset(0x44)] public uint HPol;
        [FieldOffset(0x48)] public uint Vfp;
        [FieldOffset(0x4C)] public uint VTotal;
        [FieldOffset(0x50)] public uint VSync;
        [FieldOffset(0x54)] public uint VPol;
        [FieldOffset(0x5C)] public float PixelClock;
        [FieldOffset(0x60)] public uint Ce;
    }

    private readonly record struct Desktop(int Width, int Height, int Hz, Native.DevMode Raw);
}
