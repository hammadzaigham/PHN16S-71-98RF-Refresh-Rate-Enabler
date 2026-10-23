using System.Runtime.InteropServices;

internal static class DriverRestart
{
    private const uint DigcfPresent = 0x02;
    private const uint DifPropertyChange = 0x12;
    private const int DicsPropChange = 0x3;
    private const int DicsFlagConfigSpecific = 0x2;
    private const uint SpdrpDeviceDesc = 0;
    private const int WhKeyboardLl = 13;
    private const int WmKeyDown = 0x0100;
    private const int VkF8 = 0x77;
    private const uint WmQuit = 0x0012;

    private static LowLevelProc? _hookProc;
    private static IntPtr _hook;
    private static uint _hookThreadId;
    private static ManualResetEvent? _f8;

    public static string Restart(MonitorInfo monitor)
    {
        _f8 = new ManualResetEvent(false);
        var hookReady = new ManualResetEvent(false);
        var thread = new Thread(() => HookLoop(hookReady))
        {
            IsBackground = true,
            Name = "F8 recovery"
        };
        thread.Start();
        hookReady.WaitOne(TimeSpan.FromSeconds(2));

        try
        {
            string first = RestartDisplayAdapters();
            bool pressed = _f8.WaitOne(TimeSpan.FromSeconds(15));
            if (!pressed)
                return first;

            Dictionary<string, Dictionary<string, byte[]>> saved = Program.ReadOverrides(monitor);
            if (saved.Count == 0)
                return first + " F8 was pressed. No override was installed, so nothing was unloaded.";

            Program.DeleteOverrides(monitor);
            string second = RestartDisplayAdapters();
            Program.RestoreOverrides(saved);
            return second + " F8 unloaded the override and brought the native panel timing back. The override is still saved; restart the driver again to load it.";
        }
        finally
        {
            if (_hookThreadId != 0)
                PostThreadMessage(_hookThreadId, WmQuit, IntPtr.Zero, IntPtr.Zero);
            _f8.Dispose();
            _f8 = null;
        }
    }

    private static void HookLoop(ManualResetEvent ready)
    {
        _hookThreadId = GetCurrentThreadId();
        _hookProc = HookCallback;
        _hook = SetWindowsHookEx(WhKeyboardLl, _hookProc, GetModuleHandle(null), 0);
        ready.Set();
        while (GetMessage(out Msg msg, IntPtr.Zero, 0, 0))
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
        if (_hook != IntPtr.Zero)
            UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    private static IntPtr HookCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && wParam == (IntPtr)WmKeyDown && Marshal.ReadInt32(lParam) == VkF8)
            _f8?.Set();
        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    private static string RestartDisplayAdapters()
    {
        Guid displayClass = new("4d36e968-e325-11ce-bfc1-08002be10318");
        IntPtr set = SetupDiGetClassDevs(ref displayClass, null, IntPtr.Zero, DigcfPresent);
        if (set == IntPtr.Zero || set == new IntPtr(-1))
            return "Could not list display adapters.";

        var restarted = new List<string>();
        var skipped = new List<string>();
        try
        {
            var info = new SpDevInfoData { cbSize = Marshal.SizeOf<SpDevInfoData>() };
            for (uint index = 0; SetupDiEnumDeviceInfo(set, index, ref info); index++)
            {
                string name = ReadDescription(set, ref info);
                if (ShouldSkip(name))
                {
                    skipped.Add(name);
                    info = new SpDevInfoData { cbSize = Marshal.SizeOf<SpDevInfoData>() };
                    continue;
                }

                var change = new SpPropChangeParams
                {
                    ClassInstallHeader = new SpClassInstallHeader
                    {
                        cbSize = Marshal.SizeOf<SpClassInstallHeader>(),
                        InstallFunction = DifPropertyChange
                    },
                    StateChange = DicsPropChange,
                    Scope = DicsFlagConfigSpecific,
                    HwProfile = 0
                };

                bool queued = SetupDiSetClassInstallParams(set, ref info, ref change, Marshal.SizeOf<SpPropChangeParams>());
                bool called = queued && SetupDiCallClassInstaller(DifPropertyChange, set, ref info);
                if (called)
                    restarted.Add(name);
                else
                    skipped.Add(name + " (error " + Marshal.GetLastWin32Error() + ")");

                info = new SpDevInfoData { cbSize = Marshal.SizeOf<SpDevInfoData>() };
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }

        if (restarted.Count == 0)
            return "No display adapter was restarted. " + string.Join("; ", skipped);
        return "Restarted " + string.Join(", ", restarted) + ".";
    }

    private static bool ShouldSkip(string name)
    {
        string[] blocked = { "Remote", "Indirect", "Virtual", "Parsec", "Sunshine", "Miracast" };
        return blocked.Any(word => name.Contains(word, StringComparison.OrdinalIgnoreCase));
    }

    private static string ReadDescription(IntPtr set, ref SpDevInfoData info)
    {
        var buffer = new byte[512];
        if (!SetupDiGetDeviceRegistryProperty(set, ref info, SpdrpDeviceDesc, out _, buffer, (uint)buffer.Length, out uint needed))
            return "Display adapter";
        int chars = (int)(needed / 2);
        if (chars <= 0)
            chars = buffer.Length / 2;
        string text = System.Text.Encoding.Unicode.GetString(buffer, 0, Math.Min(buffer.Length, chars * 2));
        int end = text.IndexOf('\0');
        return end >= 0 ? text[..end] : text;
    }

    private delegate IntPtr LowLevelProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SpClassInstallHeader
    {
        public int cbSize;
        public uint InstallFunction;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SpPropChangeParams
    {
        public SpClassInstallHeader ClassInstallHeader;
        public int StateChange;
        public int Scope;
        public int HwProfile;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDevInfoData
    {
        public int cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, string? enumerator, IntPtr hwndParent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr deviceInfoSet, uint memberIndex, ref SpDevInfoData deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiSetClassInstallParams(IntPtr deviceInfoSet, ref SpDevInfoData deviceInfoData, ref SpPropChangeParams classInstallParams, int classInstallParamsSize);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiCallClassInstaller(uint installFunction, IntPtr deviceInfoSet, ref SpDevInfoData deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceRegistryProperty(IntPtr deviceInfoSet, ref SpDevInfoData deviceInfoData, uint property, out uint propertyRegDataType, byte[] propertyBuffer, uint propertyBufferSize, out uint requiredSize);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool GetMessage(out Msg msg, IntPtr hwnd, uint filterMin, uint filterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref Msg msg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref Msg msg);

    [DllImport("user32.dll")]
    private static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);
}
