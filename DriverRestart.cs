using System.Runtime.InteropServices;

internal static class DriverRestart
{
    private const uint DigcfPresent = 0x02;
    private const uint DifPropertyChange = 0x12;
    private const int DicsPropChange = 0x3;
    private const int DicsFlagConfigSpecific = 0x2;
    private const uint SpdrpDeviceDesc = 0;
    private const uint SpdrpHardwareId = 1;
    private const uint DnStarted = 0x00000008;
    private const int WhKeyboardLl = 13;
    private const int WmKeyDown = 0x0100;
    private const int VkF8 = 0x77;
    private const uint WmQuit = 0x0012;
    private const uint PmNoRemove = 0x0000;

    private static LowLevelProc? _hookProc;
    private static IntPtr _hook;
    private static uint _hookThreadId;
    private static ManualResetEvent? _f8;

    public static string Restart(MonitorInfo monitor)
    {
        var f8 = new ManualResetEvent(false);
        var hookReady = new ManualResetEvent(false);
        _f8 = f8;
        _hookThreadId = 0;

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
            bool pressed = f8.WaitOne(TimeSpan.FromSeconds(15));
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
            uint threadId = _hookThreadId;
            if (threadId != 0)
                PostThreadMessage(threadId, WmQuit, IntPtr.Zero, IntPtr.Zero);
            if (thread.IsAlive)
                thread.Join(TimeSpan.FromSeconds(2));
            _hookThreadId = 0;
            _f8 = null;
            f8.Dispose();
            hookReady.Dispose();
        }
    }

    private static void HookLoop(ManualResetEvent ready)
    {
        _hookThreadId = GetCurrentThreadId();
        // Force creation of the thread message queue before signaling ready so PostThreadMessage cannot fail.
        PeekMessage(out _, IntPtr.Zero, 0, 0, PmNoRemove);
        LowLevelProc proc = HookCallback;
        _hookProc = proc;
        _hook = SetWindowsHookEx(WhKeyboardLl, proc, GetModuleHandle(null), 0);
        ready.Set();
        while (GetMessage(out Msg msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
        if (_hook != IntPtr.Zero)
            UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
        _hookProc = null;
        GC.KeepAlive(proc);
    }

    private static IntPtr HookCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && wParam == (IntPtr)WmKeyDown && Marshal.ReadInt32(lParam) == VkF8)
        {
            ManualResetEvent? f8 = _f8;
            if (f8 != null)
            {
                try { f8.Set(); }
                catch (ObjectDisposedException) { }
            }
        }
        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    /// <summary>
    /// True when a discrete NVIDIA display adapter is running and no integrated Intel/AMD adapter is running,
    /// matching PreySense's DeviceHelper.IsDiscreteGpuOnlyDisplay() without requiring WMI.
    /// </summary>
    internal static bool IsDiscreteGpuOnlyDisplay()
    {
        Guid displayClass = new("4d36e968-e325-11ce-bfc1-08002be10318");
        IntPtr set = SetupDiGetClassDevs(ref displayClass, null, IntPtr.Zero, DigcfPresent);
        if (set == IntPtr.Zero || set == new IntPtr(-1))
            return false;

        bool hasIntegrated = false;
        bool hasDiscrete = false;
        bool integratedRunning = false;
        bool discreteRunning = false;
        try
        {
            var info = new SpDevInfoData { cbSize = Marshal.SizeOf<SpDevInfoData>() };
            for (uint index = 0; SetupDiEnumDeviceInfo(set, index, ref info); index++)
            {
                string hwIds = ReadUnicodeProperty(set, ref info, SpdrpHardwareId);
                bool isIntegrated = hwIds.Contains("VEN_8086", StringComparison.OrdinalIgnoreCase)
                    || hwIds.Contains("VEN_1002", StringComparison.OrdinalIgnoreCase);
                bool isDiscrete = hwIds.Contains("VEN_10DE", StringComparison.OrdinalIgnoreCase);

                if (isIntegrated || isDiscrete)
                {
                    bool running = CM_Get_DevNode_Status(out uint status, out uint problem, info.DevInst, 0) == 0
                        && (status & DnStarted) != 0 && problem == 0;
                    if (isIntegrated)
                    {
                        hasIntegrated = true;
                        integratedRunning |= running;
                    }
                    if (isDiscrete)
                    {
                        hasDiscrete = true;
                        discreteRunning |= running;
                    }
                }

                info = new SpDevInfoData { cbSize = Marshal.SizeOf<SpDevInfoData>() };
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }

        if (discreteRunning || integratedRunning)
            return discreteRunning && !integratedRunning;
        return hasDiscrete && !hasIntegrated;
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
        string text = ReadUnicodeProperty(set, ref info, SpdrpDeviceDesc);
        if (text.Length == 0)
            return "Display adapter";
        int end = text.IndexOf('\0');
        return end >= 0 ? text[..end] : text;
    }

    private static string ReadUnicodeProperty(IntPtr set, ref SpDevInfoData info, uint property)
    {
        var buffer = new byte[1024];
        if (!SetupDiGetDeviceRegistryProperty(set, ref info, property, out _, buffer, (uint)buffer.Length, out uint needed))
            return "";
        int bytes = (int)Math.Min((uint)buffer.Length, needed > 0 ? needed : (uint)buffer.Length);
        return System.Text.Encoding.Unicode.GetString(buffer, 0, bytes);
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

    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Get_DevNode_Status(out uint pulStatus, out uint pulProblemNumber, uint dnDevInst, uint ulFlags);

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
    private static extern int GetMessage(out Msg msg, IntPtr hwnd, uint filterMin, uint filterMax);

    [DllImport("user32.dll")]
    private static extern bool PeekMessage(out Msg msg, IntPtr hwnd, uint filterMin, uint filterMax, uint removeMsg);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref Msg msg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref Msg msg);

    [DllImport("user32.dll")]
    private static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);
}
