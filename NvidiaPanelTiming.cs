using System.Runtime.InteropServices;

internal static class NvidiaPanelTiming
{
    public static int Apply(IReadOnlyList<int> rates, IReadOnlyList<byte[]> descriptors)
    {
        if (rates.Count != descriptors.Count || rates.Count == 0)
        {
            ProbeLog.Line("No NVIDIA timings to send.");
            return 1;
        }

        nint lib = Native.LoadLibrary(@"C:\Windows\System32\nvcpl.dll");
        if (lib == 0)
        {
            ProbeLog.Line("NVIDIA control panel library did not load. Nothing was written.");
            return 1;
        }

        var init = Bind<InitFn>(lib, "NvCplApiInit");
        var test = Bind<TestFn>(lib, "NvCplApiCTTest");
        var clear = Bind<AddressFn>(lib, "NvCplApiClearAllUnsupportedResolutionsFromCacheForDisplay");
        var flush = Bind<AddressFn>(lib, "NvCplApiFlushUnsupportedResolutionCacheToStorage");

        int rc = init(0, 0);
        ProbeLog.Line("NvCplApiInit " + rc);
        if (rc != 0)
            return 1;

        var before = CurrentMode();
        ProbeLog.Line("Desktop before NVIDIA timings " + before.Width + "x" + before.Height + " @ " + before.Hz + " Hz, signal " + ActiveSignal());

        var address = new Address { Gpu = 0, Display = 0 };
        int failures = 0;
        int saved = 0;
        for (int i = 0; i < rates.Count; i++)
        {
            Timing timing = FromDescriptor(rates[i], descriptors[i]);
            int tested = test(ref address, 1, ref timing);
            string signal = ActiveSignal();
            int persisted = test(ref address, 6, ref timing);
            var now = CurrentMode();
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
                int restored = Restore(before);
                var after = CurrentMode();
                ProbeLog.Line("Restored desktop " + restored + ", now " + after.Width + "x" + after.Height + " @ " + after.Hz);
            }
        }

        int returned = RestoreRefresh(240);
        ProbeLog.Line("Desktop returned to 240 Hz, result " + returned + ", signal " + ActiveSignal());

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

    private static T Bind<T>(nint module, string name) where T : Delegate
    {
        nint address = Native.GetProcAddress(module, name);
        if (address == 0)
            throw new InvalidOperationException("nvcpl.dll is missing " + name);
        return Marshal.GetDelegateForFunctionPointer<T>(address);
    }

    private static string ActiveSignal()
    {
        if (Signal.GetDisplayConfigBufferSizes(2, out uint pathCount, out uint modeCount) != 0)
            return "unavailable";
        var paths = new byte[pathCount * 72];
        var modes = new byte[modeCount * 64];
        if (Signal.QueryDisplayConfig(2, ref pathCount, paths, ref modeCount, modes, 0) != 0)
            return "unavailable";

        var parts = new List<string>();
        for (int i = 0; i < modeCount; i++)
        {
            if (BitConverter.ToInt32(modes, i * 64) != 2)
                continue;
            ulong pixel = BitConverter.ToUInt64(modes, i * 64 + 16);
            uint totalW = BitConverter.ToUInt32(modes, i * 64 + 48);
            uint totalH = BitConverter.ToUInt32(modes, i * 64 + 52);
            parts.Add(pixel + " Hz, " + totalW + "x" + totalH);
        }
        return parts.Count == 0 ? "no target" : string.Join("; ", parts);
    }

    private static Desktop CurrentMode()
    {
        var mode = new DevMode { dmSize = (short)Marshal.SizeOf<DevMode>() };
        Signal.EnumDisplaySettingsW(null, -1, ref mode);
        return new Desktop(mode.dmPelsWidth, mode.dmPelsHeight, mode.dmDisplayFrequency, mode);
    }

    private static int Restore(Desktop desktop)
    {
        DevMode mode = desktop.Raw;
        return Signal.ChangeDisplaySettingsExW(null, ref mode, 0, 0, 0);
    }

    private static int RestoreRefresh(int hz)
    {
        for (int i = 0; ; i++)
        {
            var mode = new DevMode { dmSize = (short)Marshal.SizeOf<DevMode>() };
            if (!Signal.EnumDisplaySettingsW(null, i, ref mode))
                return -1;
            if (mode.dmPelsWidth == 2560 && mode.dmPelsHeight == 1600 && mode.dmDisplayFrequency == hz)
                return Signal.ChangeDisplaySettingsExW(null, ref mode, 0, 0, 0);
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int InitFn(int a, int b);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int AddressFn(ref Address address);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int TestFn(ref Address address, int op, ref Timing timing);

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

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string? dmDeviceName;
        public short dmSpecVersion;
        public short dmDriverVersion;
        public short dmSize;
        public short dmDriverExtra;
        public int dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public int dmDisplayOrientation;
        public int dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string? dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel;
        public int dmPelsWidth;
        public int dmPelsHeight;
        public int dmDisplayFlags;
        public int dmDisplayFrequency;
        public int dmICMMethod;
        public int dmICMIntent;
        public int dmMediaType;
        public int dmDitherType;
        public int dmReserved1;
        public int dmReserved2;
        public int dmPanningWidth;
        public int dmPanningHeight;
    }

    private readonly record struct Desktop(int Width, int Height, int Hz, DevMode Raw);

    private static class Native
    {
        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern nint LoadLibrary(string path);

        [DllImport("kernel32", CharSet = CharSet.Ansi, SetLastError = true)]
        public static extern nint GetProcAddress(nint module, string name);
    }

    private static class Signal
    {
        [DllImport("user32", CharSet = CharSet.Unicode)]
        public static extern bool EnumDisplaySettingsW(string? device, int mode, ref DevMode devMode);

        [DllImport("user32", CharSet = CharSet.Unicode)]
        public static extern int ChangeDisplaySettingsExW(string? device, ref DevMode devMode, nint hwnd, int flags, nint param);

        [DllImport("user32")]
        public static extern int GetDisplayConfigBufferSizes(uint flags, out uint pathCount, out uint modeCount);

        [DllImport("user32")]
        public static extern int QueryDisplayConfig(uint flags, ref uint pathCount, byte[] paths, ref uint modeCount, byte[] modes, nint topology);
    }
}
