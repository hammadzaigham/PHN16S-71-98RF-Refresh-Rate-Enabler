// PHN16S-71-98RF Refresh Rate Enabler.
// Double-click "PHN16S-71-98RF Refresh Rate Enabler.exe" for the window.
//
//   "PHN16S-71-98RF Refresh Rate Enabler.exe" --stage --force
//   "PHN16S-71-98RF Refresh Rate Enabler.exe" --stage --force --restart
//   "PHN16S-71-98RF Refresh Rate Enabler.exe" --revert --restart
//
// --stage writes 60, 75, 100, 120, 144, and 165 Hz on the GPU driving the panel.
// Intel gets an EDID override. NVIDIA gets NvCplApi timings. 240 Hz stays preferred.
// --restart reloads the display driver. Press F8 within 15 seconds if the panel stays black.

using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Windows;
using Microsoft.Win32;

public static class Program
{
    internal static readonly int[] StagedRates = { 60, 75, 100, 120, 144, 165 };

    [STAThread]
    public static int Main(string[] args)
    {
        bool revert = Has(args, "--revert");
        bool force = Has(args, "--force");
        bool stage = Has(args, "--stage");
        bool restart = Has(args, "--restart");

        if (Has(args, "--help") || Has(args, "-h"))
        {
            EnsureConsole();
            WriteHelp();
            return 0;
        }

        if (args.Length == 0)
        {
            if (!IsAdmin() && RelaunchElevated(args, wait: false) != null)
                return 0;
            var app = new Application();
            app.Run(new MainWindow());
            return 0;
        }

        EnsureConsole();
        if ((stage || revert || restart) && !IsAdmin())
        {
            ProbeLog.Line("Administrator approval is required.");
            int? elevated = RelaunchElevated(args, wait: true);
            if (elevated == null)
            {
                ProbeLog.Line("The approval was declined. Nothing was changed.");
                return 1;
            }
            return elevated.Value;
        }

        if (revert && stage)
        {
            ProbeLog.Line("Use --revert or --stage, not both.");
            return 1;
        }

        var monitor = MonitorLocator.FindInternal();
        if (monitor == null)
        {
            ProbeLog.Line("No active internal monitor with an EDID was found.");
            return 1;
        }

        ProbeLog.Line(monitor.DeviceString);
        ProbeLog.Line("  device    " + monitor.DeviceName);
        ProbeLog.Line("  id        " + monitor.DeviceId);
        ProbeLog.Line("  registry  " + monitor.RegistryPath);
        ProbeLog.Line("  current   " + monitor.CurrentWidth + "x" + monitor.CurrentHeight + " @ " + monitor.CurrentHz + " Hz");
        ProbeLog.Line("  modes     " + (monitor.Modes.Count == 0 ? "(none listed)" : string.Join(", ", monitor.Modes.Select(m => m + " Hz"))));

        var signal = CcdSignal.ReadActive();
        if (signal != null)
        {
            ProbeLog.Line("  signal    " + signal.Describe());
            ProbeLog.Line("  output    " + signal.Output);
        }

        byte[] edid = monitor.ReadEdid();
        var parsed = PanelEdid.Parse(edid);
        if (parsed.Error != null)
        {
            ProbeLog.Line(parsed.Error);
            return 1;
        }

        ProbeLog.Line("");
        ProbeLog.Line(parsed.Identity);
        foreach (string line in parsed.Report)
            ProbeLog.Line(line);

        if (signal != null)
            ProbeLog.Line(parsed.Compare(signal));

        ProbeLog.Line("");
        ProbeLog.Line("Candidates at " + parsed.PixelClockKhz + " kHz, horizontal total " + parsed.HorizontalTotal);
        var built = new List<byte[]>();
        foreach (int hz in StagedRates)
        {
            if (!parsed.TryBuild(hz, out byte[] descriptor, out string detail))
            {
                ProbeLog.Line("  " + hz + " Hz  " + detail);
                return 1;
            }
            built.Add(descriptor);
            ProbeLog.Line("  " + hz + " Hz  " + detail);
        }

        if (!PanelEdid.ExtensionChecksumsValid(built))
        {
            ProbeLog.Line("Combined extension checksum failed.");
            return 1;
        }

        ProbeLog.Line("");
        ProbeLog.Line(parsed.MethodAWarning(60));
        ProbeLog.Line("");
        ProbeLog.Line("The adaptive-sync floor is " + parsed.AdaptiveMinHz + " Hz and the static range is "
            + parsed.RangeMinHz + "–" + parsed.RangeMaxHz + " Hz. 40 Hz is below both, and the active mode is "
            + monitor.CurrentHz + " Hz.");
        ProbeLog.Line("60, 75, 100, 120, 144, and 165 Hz keep this pixel clock and lengthen the frame with vertical blanking.");
        ProbeLog.Line("The GPU driving the panel is " + ActiveGpu(monitor) + ". Only that GPU is changed.");

        if (revert)
        {
            int reverted = Revert(monitor);
            if (reverted != 0 || !restart)
                return reverted;
            return Restart(monitor);
        }

        if (stage)
        {
            foreach (int hz in StagedRates)
            {
                if (!force && (hz < parsed.AdaptiveMinHz || hz > parsed.RangeMaxHz))
                {
                    ProbeLog.Line("");
                    ProbeLog.Line(hz + " Hz is outside " + parsed.AdaptiveMinHz + "–" + parsed.RangeMaxHz + " Hz. Re-run with --force to stage it anyway.");
                    return 1;
                }
            }

            int staged = Stage(monitor, edid, StagedRates, built, force);
            if (staged != 0 || !restart)
                return staged;
            return Restart(monitor);
        }

        if (restart)
            return Restart(monitor);

        ProbeLog.Line("");
        ProbeLog.Line("No override written.");
        ProbeLog.Line("Double-click \"PHN16S-71-98RF Refresh Rate Enabler.exe\", or run it with --stage --force --restart");
        ProbeLog.Line("If the panel stays black after a restart, press F8.");
        return 0;
    }

    internal static int Stage(MonitorInfo monitor, byte[] edid, IReadOnlyList<int> rates, IReadOnlyList<byte[]> descriptors, bool force)
    {
        ProbeLog.Line("");
        if (!IsAdmin())
        {
            ProbeLog.Line("Staging needs an elevated process. Nothing was written.");
            return 1;
        }

        if (rates.Count != descriptors.Count || rates.Count == 0)
        {
            ProbeLog.Line("No timings to stage.");
            return 1;
        }

        bool nvidia = IsNvidiaActive(monitor);
        if (nvidia)
        {
            ProbeLog.Line("NVIDIA is driving the panel. Staging 60, 75, 100, 120, 144, and 165 Hz through the NVIDIA control panel API. The EDID stays original.");
            int applied = NvidiaPanelTiming.Apply(rates, descriptors);
            if (applied != 0)
                return applied;
            ProbeLog.Line("The 240 Hz timing is still the preferred mode.");
            return 0;
        }

        ProbeLog.Line("Intel is driving the panel. Staging the EDID override after the original 240 Hz block. NVIDIA is left unchanged.");
        IReadOnlyList<string> paths = PanelPaths(monitor);
        if (!force && paths.Where(path => AdapterLabel(path) != "NVIDIA").Any(path => OverrideExists(path)))
        {
            ProbeLog.Line("EDID_OVERRIDE already exists. Nothing was written. Re-run with --force to replace it, or remove it first.");
            return 1;
        }

        bool wroteIntel = false;
        foreach (string path in paths)
        {
            if (string.Equals(AdapterLabel(path), "NVIDIA", StringComparison.Ordinal))
                continue;

            byte[] instanceEdid = ReadEdidAt(path);
            if (instanceEdid.Length < 256)
            {
                ProbeLog.Line(AdapterLabel(path) + " EDID is too short. Skipped.");
                continue;
            }

            try
            {
                PanelEdid.BuildOverride(instanceEdid, descriptors, out byte[] block0, out byte[] block1, out byte[][] extras);
                WriteBlocks(path, block0, block1, extras);
                wroteIntel = true;
                ProbeLog.Line("Staged " + AdapterLabel(path) + " (" + path + ").");
            }
            catch (Exception ex)
            {
                ProbeLog.Line(AdapterLabel(path) + " was not staged.");
                ProbeLog.Line(ex.ToString());
                return 1;
            }
        }

        if (!wroteIntel)
        {
            ProbeLog.Line("No Intel panel EDID was found, so no override was written.");
            return 1;
        }

        ProbeLog.Line("Staged " + string.Join(", ", rates) + " Hz for Intel after the original 240 Hz block.");
        for (int i = 0; i < rates.Count; i++)
            ProbeLog.Line("  " + rates[i] + " Hz vertical total " + PanelEdid.ReadVerticalTotal(descriptors[i]));
        ProbeLog.Line("The 240 Hz timing is still the preferred mode. Restart the display driver to load these modes.");
        return 0;
    }

    internal static int Revert(MonitorInfo monitor)
    {
        ProbeLog.Line("");
        if (!IsAdmin())
        {
            ProbeLog.Line("Removing the override needs an elevated process. Nothing was changed.");
            return 1;
        }

        ProbeLog.Line("Remove override only removes the Intel EDID override.");
        bool removed = false;
        foreach (string path in PanelPaths(monitor))
        {
            if (string.Equals(AdapterLabel(path), "NVIDIA", StringComparison.Ordinal))
                continue;
            if (!OverrideExists(path))
                continue;
            DeleteOverride(path);
            removed = true;
            ProbeLog.Line("Removed the Intel override.");
        }
        if (!removed)
            ProbeLog.Line("No Intel EDID_OVERRIDE key.");

        if (IsNvidiaActive(monitor))
        {
            ProbeLog.Line("NVIDIA custom timings stay in the driver.");
            ProbeLog.Line("Clean install the driver from the NVIDIA app to remove them.");
            return 0;
        }

        ProbeLog.Line("Restart the display driver to load the original 240 Hz panel timing.");
        return 0;
    }

    internal static string ActiveGpu(MonitorInfo monitor)
        => IsNvidiaActive(monitor) ? "NVIDIA" : "Intel";

    private static bool IsNvidiaActive(MonitorInfo monitor)
    {
        CcdSignal? signal = CcdSignal.ReadActive();
        if (signal != null)
        {
            string marker = "UID" + signal.TargetId;
            foreach (string path in PanelPaths(monitor))
            {
                if (path.Contains(marker, StringComparison.OrdinalIgnoreCase))
                    return string.Equals(AdapterLabel(path), "NVIDIA", StringComparison.Ordinal);
            }
        }
        return monitor.Adapter.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase);
    }

    internal static int Restart(MonitorInfo monitor)
    {
        ProbeLog.Line("");
        if (!IsAdmin())
        {
            ProbeLog.Line("Restarting the driver needs an elevated process.");
            return 1;
        }

        ProbeLog.Line("Restarting the display driver. The screen will go black for a few seconds.");
        ProbeLog.Line("Press F8 within 15 seconds if the panel stays black.");
        string outcome = DriverRestart.Restart(monitor);
        ProbeLog.Line(outcome);
        return outcome.StartsWith("Restarted", StringComparison.Ordinal) ? 0 : 1;
    }

    internal static bool OverrideExists(MonitorInfo monitor)
        => PanelPaths(monitor).Any(OverrideExists);

    internal static Dictionary<string, Dictionary<string, byte[]>> ReadOverrides(MonitorInfo monitor)
    {
        var saved = new Dictionary<string, Dictionary<string, byte[]>>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in PanelPaths(monitor))
        {
            Dictionary<string, byte[]>? values = ReadOverride(path);
            if (values != null)
                saved[path] = values;
        }
        return saved;
    }

    internal static void RestoreOverrides(Dictionary<string, Dictionary<string, byte[]>> saved)
    {
        foreach (var entry in saved)
            WriteOverride(entry.Key, entry.Value);
    }

    internal static void DeleteOverrides(MonitorInfo monitor)
    {
        foreach (string path in PanelPaths(monitor))
            DeleteOverride(path);
    }

    private static bool OverrideExists(string path)
    {
        using RegistryKey key = OpenParameters(path, writable: false);
        return key.GetSubKeyNames().Contains("EDID_OVERRIDE", StringComparer.OrdinalIgnoreCase);
    }

    private static Dictionary<string, byte[]>? ReadOverride(string path)
    {
        if (!OverrideExists(path))
            return null;
        using RegistryKey key = OpenParameters(path, writable: false);
        using RegistryKey over = key.OpenSubKey("EDID_OVERRIDE")!;
        var values = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (string name in over.GetValueNames())
        {
            if (over.GetValue(name) is byte[] data)
                values[name] = data;
        }
        return values;
    }

    private static void WriteOverride(string path, IReadOnlyDictionary<string, byte[]> values)
    {
        using RegistryKey key = OpenParameters(path, writable: true);
        using RegistryKey over = key.CreateSubKey("EDID_OVERRIDE");
        foreach (var pair in values)
            over.SetValue(pair.Key, pair.Value, RegistryValueKind.Binary);
    }

    private static void DeleteOverride(string path)
    {
        using RegistryKey key = OpenParameters(path, writable: true);
        if (key.GetSubKeyNames().Contains("EDID_OVERRIDE", StringComparer.OrdinalIgnoreCase))
            key.DeleteSubKeyTree("EDID_OVERRIDE");
    }

    private static void WriteBlocks(string path, byte[] block0, byte[] block1, byte[][] extras)
    {
        using RegistryKey key = OpenParameters(path, writable: true);
        if (key.OpenSubKey("EDID_OVERRIDE") != null)
            key.DeleteSubKeyTree("EDID_OVERRIDE");
        using RegistryKey over = key.CreateSubKey("EDID_OVERRIDE");
        over.SetValue("0", block0, RegistryValueKind.Binary);
        over.SetValue("1", block1, RegistryValueKind.Binary);
        for (int i = 0; i < extras.Length; i++)
            over.SetValue((i + 2).ToString(), extras[i], RegistryValueKind.Binary);
    }

    private static IReadOnlyList<string> PanelPaths(MonitorInfo monitor)
        => monitor.RegistryPaths.Count > 0 ? monitor.RegistryPaths : new[] { monitor.RegistryPath };

    private static byte[] ReadEdidAt(string path)
    {
        using RegistryKey key = OpenParameters(path, writable: false);
        if (key.GetValue("EDID") is byte[] edid && edid.Length >= 128)
            return edid;
        throw new InvalidOperationException("EDID at " + path + " is missing.");
    }

    internal static string AdapterLabel(string parameterPath)
    {
        string[] parts = parameterPath.Split('\\');
        string instance = parts.Length >= 2 ? parts[^2] : parameterPath;
        using RegistryKey? nvidia = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\DISPLAY\NVD0000\" + instance);
        return nvidia != null ? "NVIDIA" : "Intel";
    }

    private static RegistryKey OpenParameters(string path, bool writable)
    {
        const string prefix = @"\Registry\Machine\";
        if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            path = path[prefix.Length..];
        RegistryKey? key = Registry.LocalMachine.OpenSubKey(path, writable);
        if (key == null)
            throw new InvalidOperationException("Could not open " + path);
        return key;
    }

    internal static bool IsAdmin()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static bool Has(string[] args, string flag)
        => args.Any(a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));

    private static void WriteHelp()
    {
        ProbeLog.Line("PHN16S-71-98RF Refresh Rate Enabler");
        ProbeLog.Line("  (no args)     open the window");
        ProbeLog.Line("  --stage       write 60, 75, 100, 120, 144, and 165 Hz");
        ProbeLog.Line("  --force       replace an existing override");
        ProbeLog.Line("  --revert      remove the Intel EDID override");
        ProbeLog.Line("NVIDIA timings are removed by a clean install from the NVIDIA app.");
        ProbeLog.Line("  --restart     restart the display driver");
        ProbeLog.Line("Press F8 within 15 seconds of a restart if the panel stays black.");
    }

    private static void EnsureConsole()
    {
        if (!AttachConsole(AttachParentProcess))
            AllocConsole();
        Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
        Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
    }

    private static int? RelaunchElevated(string[] args, bool wait)
    {
        try
        {
            string arguments = string.Join(" ", args.Select(Quote));
            var start = new ProcessStartInfo
            {
                FileName = Environment.ProcessPath ?? "",
                UseShellExecute = true,
                Verb = "runas",
                Arguments = arguments
            };
            Process? process = Process.Start(start);
            if (process == null)
                return null;
            if (!wait)
                return 0;
            process.WaitForExit();
            return process.ExitCode;
        }
        catch
        {
            return null;
        }
    }

    private static string Quote(string value)
        => value.Contains(' ') ? "\"" + value + "\"" : value;

    private const int AttachParentProcess = -1;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AllocConsole();
}

internal static class ProbeLog
{
    public static Action<string>? Sink;

    public static void Line(string text)
    {
        Console.WriteLine(text);
        Sink?.Invoke(text);
    }
}

internal sealed class MonitorInfo
{
    public string DeviceName { get; init; } = "";
    public string DeviceString { get; init; } = "";
    public string Adapter { get; init; } = "";
    public string DeviceId { get; init; } = "";
    public string RegistryPath { get; init; } = "";
    public List<string> RegistryPaths { get; init; } = new();
    public int CurrentWidth { get; init; }
    public int CurrentHeight { get; init; }
    public int CurrentHz { get; init; }
    public List<int> Modes { get; init; } = new();

    public byte[] ReadEdid()
    {
        using RegistryKey key = Open();
        if (key.GetValue("EDID") is byte[] edid && edid.Length >= 256)
            return edid;
        throw new InvalidOperationException("EDID at " + RegistryPath + " is missing or shorter than 256 bytes.");
    }

    private RegistryKey Open()
    {
        string path = RegistryPath;
        const string prefix = @"\Registry\Machine\";
        if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            path = path[prefix.Length..];
        return Registry.LocalMachine.OpenSubKey(path)
            ?? throw new InvalidOperationException("Could not open " + path);
    }
}

internal static class MonitorLocator
{
    public static MonitorInfo? FindInternal()
    {
        var device = new Native.DisplayDevice { cb = System.Runtime.InteropServices.Marshal.SizeOf<Native.DisplayDevice>() };
        uint index = 0;
        while (Native.EnumDisplayDevices(null, index, ref device, 0))
        {
            bool attached = (device.StateFlags & 0x1) != 0;
            bool primary = (device.StateFlags & 0x4) != 0;
            if (attached && primary)
            {
                var monitor = new Native.DisplayDevice { cb = System.Runtime.InteropServices.Marshal.SizeOf<Native.DisplayDevice>() };
                if (Native.EnumDisplayDevices(device.DeviceName, 0, ref monitor, 0))
                {
                    List<string> registryPaths = FindEdidParameters(monitor.DeviceID);
                    if (registryPaths.Count == 0)
                        return null;
                    string registryPath = registryPaths[0];
                    var mode = new Native.DevMode { dmSize = (short)System.Runtime.InteropServices.Marshal.SizeOf<Native.DevMode>() };
                    Native.EnumDisplaySettings(device.DeviceName, Native.EnumCurrentSettings, ref mode);
                    var rates = new SortedSet<int>();
                    var listed = new Native.DevMode { dmSize = (short)System.Runtime.InteropServices.Marshal.SizeOf<Native.DevMode>() };
                    for (int i = 0; Native.EnumDisplaySettings(device.DeviceName, i, ref listed); i++)
                    {
                        if (listed.dmPelsWidth == mode.dmPelsWidth && listed.dmPelsHeight == mode.dmPelsHeight && listed.dmDisplayFrequency > 0)
                            rates.Add(listed.dmDisplayFrequency);
                    }

                    return new MonitorInfo
                    {
                        DeviceName = device.DeviceName,
                        Adapter = device.DeviceString,
                        DeviceString = string.IsNullOrWhiteSpace(monitor.DeviceString) ? device.DeviceString : monitor.DeviceString,
                        DeviceId = monitor.DeviceID,
                        RegistryPath = registryPath,
                        RegistryPaths = registryPaths,
                        CurrentWidth = mode.dmPelsWidth,
                        CurrentHeight = mode.dmPelsHeight,
                        CurrentHz = mode.dmDisplayFrequency,
                        Modes = rates.ToList()
                    };
                }
            }
            index++;
            device = new Native.DisplayDevice { cb = System.Runtime.InteropServices.Marshal.SizeOf<Native.DisplayDevice>() };
        }
        return null;
    }

    private static List<string> FindEdidParameters(string deviceId)
    {
        var paths = new List<string>();
        string[] parts = deviceId.Split('\\');
        if (parts.Length < 2 || parts[1].Length == 0)
            return paths;

        string model = parts[1];
        using RegistryKey? modelKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\DISPLAY\" + model);
        if (modelKey == null)
            return paths;

        foreach (string instance in modelKey.GetSubKeyNames())
        {
            using RegistryKey? parameters = modelKey.OpenSubKey(instance + @"\Device Parameters");
            if (parameters?.GetValue("EDID") is byte[] edid && edid.Length >= 256)
                paths.Add(@"SYSTEM\CurrentControlSet\Enum\DISPLAY\" + model + @"\" + instance + @"\Device Parameters");
        }
        return paths;
    }
}

internal sealed class CcdSignal
{
    public ulong PixelClockHz { get; init; }
    public uint HActive { get; init; }
    public uint VActive { get; init; }
    public uint HTotal { get; init; }
    public uint VTotal { get; init; }
    public double RefreshHz { get; init; }
    public uint TargetId { get; init; }
    public string Output { get; init; } = "";

    public string Describe()
    {
        double hKhz = HTotal == 0 ? 0 : PixelClockHz / (double)HTotal / 1000.0;
        return PixelClockHz + " Hz pixel clock, " + HActive + "x" + VActive
            + " active, " + HTotal + "x" + VTotal + " total, "
            + hKhz.ToString("0.000") + " kHz horizontal, "
            + RefreshHz.ToString("0.000") + " Hz";
    }

    public static CcdSignal? ReadActive()
    {
        if (Native.GetDisplayConfigBufferSizes(Native.QdcOnlyActivePaths, out uint pathCount, out uint modeCount) != 0)
            return null;
        var paths = new Native.PathInfo[pathCount];
        var modes = new Native.ModeInfo[modeCount];
        if (Native.QueryDisplayConfig(Native.QdcOnlyActivePaths, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero) != 0)
            return null;
        if (pathCount < 1)
            return null;

        var path = paths[0];
        uint modeIndex = path.Target.modeInfoIdx;
        if (modeIndex >= modeCount || modes[modeIndex].infoType != Native.ModeInfoTypeTarget)
            return null;

        var video = modes[modeIndex].mode.targetMode.video;
        double refresh = video.vSyncFreq.Denominator == 0
            ? 0
            : video.vSyncFreq.Numerator / (double)video.vSyncFreq.Denominator;
        string output = path.Target.outputTechnology == 0x80000000 ? "internal panel" : "technology " + path.Target.outputTechnology;
        return new CcdSignal
        {
            PixelClockHz = video.pixelRate,
            HActive = video.activeSize.cx,
            VActive = video.activeSize.cy,
            HTotal = video.totalSize.cx,
            VTotal = video.totalSize.cy,
            RefreshHz = refresh,
            TargetId = path.Target.id,
            Output = output
        };
    }
}

internal sealed class PanelEdid
{
    public string Identity { get; init; } = "";
    public string? Error { get; init; }
    public List<string> Report { get; init; } = new();
    public int PixelClockKhz { get; init; }
    public int HorizontalTotal { get; init; }
    public int VerticalTotal { get; init; }
    public int HActive { get; init; }
    public int VActive { get; init; }
    public int HBlank { get; init; }
    public int HFront { get; init; }
    public int HSync { get; init; }
    public int VFront { get; init; }
    public int VSync { get; init; }
    public int VBack { get; init; }
    public bool HSyncPositive { get; init; }
    public bool VSyncPositive { get; init; }
    public byte Options { get; init; }
    public int RangeMinHz { get; init; }
    public int RangeMaxHz { get; init; }
    public int AdaptiveMinHz { get; init; }
    public int AdaptiveMaxHz { get; init; }
    public byte[] NativeDescriptor { get; init; } = Array.Empty<byte>();

    public string Compare(CcdSignal signal)
    {
        bool clock = (ulong)PixelClockKhz * 1000UL == signal.PixelClockHz;
        bool size = HActive == signal.HActive && VActive == signal.VActive
            && HorizontalTotal == signal.HTotal && VerticalTotal == signal.VTotal;
        return size && clock
            ? "Native Type VII timing matches the active signal."
            : "Native Type VII timing does not match the active signal.";
    }

    public string MethodAWarning(int hz)
    {
        double nativeHz = (PixelClockKhz * 1000.0) / (HorizontalTotal * (double)VerticalTotal);
        double scaledKhz = PixelClockKhz * (hz / nativeHz);
        double hKhz = scaledKhz / HorizontalTotal;
        return "Keeping the native " + HorizontalTotal + "x" + VerticalTotal + " totals at " + hz
            + " Hz would need a " + scaledKhz.ToString("0") + " kHz pixel clock ("
            + hKhz.ToString("0.0") + " kHz horizontal). The range descriptor allows "
            + (PixelClockKhz / HorizontalTotal).ToString("0") + " kHz horizontal, so that candidate stays unused.";
    }

    public static int ReadVerticalTotal(byte[] descriptor)
    {
        int vActive = 1 + descriptor[12] + (descriptor[13] << 8);
        int vBlank = 1 + descriptor[14] + (descriptor[15] << 8);
        return vActive + vBlank;
    }

    public bool TryBuild(int hz, out byte[] descriptor, out string detail)
    {
        descriptor = Array.Empty<byte>();
        if (HActive == 0 || HorizontalTotal == 0 || PixelClockKhz == 0)
        {
            detail = "native timing is missing";
            return false;
        }

        double exact = (PixelClockKhz * 1000.0) / (HorizontalTotal * (double)hz);
        int vTotal = (int)Math.Round(exact);
        double actual = (PixelClockKhz * 1000.0) / (HorizontalTotal * (double)vTotal);
        int vBlank = vTotal - VActive;
        int vFront = vBlank - VSync - VBack;
        if (vFront < 1 || vFront > 32768 || vBlank < 1 || vBlank > 65535)
        {
            detail = "vertical blank " + vBlank + " does not fit in a Type VII descriptor";
            return false;
        }

        bool insideStatic = hz >= RangeMinHz && hz <= RangeMaxHz;
        bool insideAdaptive = hz >= AdaptiveMinHz && hz <= AdaptiveMaxHz;
        string range = insideAdaptive
            ? "inside the adaptive-sync range"
            : insideStatic
                ? "inside the static range, below the adaptive-sync floor"
                : "outside the declared range";

        descriptor = (byte[])NativeDescriptor.Clone();
        WriteClock(descriptor, PixelClockKhz);
        descriptor[3] = (byte)(Options & 0x7F);
        Write16(descriptor, 12, VActive);
        Write16(descriptor, 14, vBlank);
        Write15(descriptor, 16, vFront, VSyncPositive);
        Write16(descriptor, 18, VSync);

        double drawn = 100.0 * VActive / vTotal;
        detail = "vertical total " + vTotal + ", blank " + vBlank
            + ", front porch " + vFront + ", sync " + VSync + ", back porch " + VBack
            + ", actual " + actual.ToString("0.0000") + " Hz, image drawn for " + drawn.ToString("0.0")
            + "% of the frame, " + range;
        return true;
    }

    public static PanelEdid Parse(byte[] edid)
    {
        var report = new List<string>();
        if (edid.Length < 256 || edid[0] != 0x00 || edid[1] != 0xFF)
            return new PanelEdid { Error = "EDID header is not valid." };

        if (BlockChecksum(edid, 0) != edid[127])
            report.Add("  base checksum does not match");
        if (BlockChecksum(edid, 128) != edid[255])
            report.Add("  extension checksum does not match");

        int mfg = (edid[8] << 8) | edid[9];
        char c1 = (char)('A' + ((mfg >> 10) & 0x1F) - 1);
        char c2 = (char)('A' + ((mfg >> 5) & 0x1F) - 1);
        char c3 = (char)('A' + (mfg & 0x1F) - 1);
        int product = edid[10] | (edid[11] << 8);
        string name = ReadDescriptorString(edid, 0xFE) ?? ReadDescriptorString(edid, 0xFC) ?? "";
        string identity = $"{c1}{c2}{c3} {product:X4}  {name}".Trim();

        int rangeMin = 0, rangeMax = 0, hMin = 0, hMax = 0, maxClockMhz = 0;
        for (int offset = 54; offset <= 108; offset += 18)
        {
            if (edid[offset] != 0 || edid[offset + 1] != 0 || edid[offset + 3] != 0xFD)
                continue;
            int flags = edid[offset + 4];
            rangeMin = edid[offset + 5] + ((flags & 0x01) != 0 ? 255 : 0);
            rangeMax = edid[offset + 6] + ((flags & 0x02) != 0 ? 255 : 0);
            hMin = edid[offset + 7] + ((flags & 0x04) != 0 ? 255 : 0);
            hMax = edid[offset + 8] + ((flags & 0x08) != 0 ? 255 : 0);
            maxClockMhz = edid[offset + 9] * 10;
        }

        report.Add("  range     " + rangeMin + "–" + rangeMax + " Hz, horizontal " + hMin + "–" + hMax
            + " kHz, max pixel clock " + maxClockMhz + " MHz");

        if (edid[128] != 0x70)
            return Fail(identity, report, "Extension is not DisplayID.");

        int length = edid[128 + 2];
        int checksumLength = length + 5;
        int sum = 0;
        for (int i = 0; i < checksumLength; i++)
            sum += edid[128 + 1 + i];
        if ((sum & 0xFF) != 0)
            report.Add("  DisplayID checksum does not match");

        int adaptiveMin = 0, adaptiveMax = 0;
        byte[]? native = null;
        int offsetBlock = 128 + 5;
        int remaining = length;
        while (remaining >= 3)
        {
            int tag = edid[offsetBlock];
            int payload = edid[offsetBlock + 2];
            if (tag == 0 && payload == 0)
                break;
            if (payload + 3 > remaining)
                break;

            if (tag == 0x22 && payload >= 20 && native == null)
            {
                native = new byte[20];
                Buffer.BlockCopy(edid, offsetBlock + 3, native, 0, 20);
                report.Add("  type VII  " + Describe(native));
            }
            else if (tag == 0x2B && payload >= 6)
            {
                byte[] p = edid;
                int o = offsetBlock + 3;
                adaptiveMin = p[o + 2];
                adaptiveMax = p[o + 3] | ((p[o + 4] & 0x03) << 8);
                report.Add("  adaptive  " + adaptiveMin + "–" + adaptiveMax + " Hz");
            }
            else
            {
                report.Add("  block     0x" + tag.ToString("X2") + " length " + payload);
            }

            offsetBlock += payload + 3;
            remaining -= payload + 3;
        }

        if (native == null)
            return Fail(identity, report, "No Type VII detailed timing in the EDID.");

        Decode(native, out int clock, out int hAct, out int hBl, out int hFp, out int hSw, out bool hPos,
            out int vAct, out int vBl, out int vFp, out int vSw, out bool vPos, out byte options);
        int vBp = vBl - vFp - vSw;

        return new PanelEdid
        {
            Identity = identity,
            Report = report,
            PixelClockKhz = clock,
            HorizontalTotal = hAct + hBl,
            VerticalTotal = vAct + vBl,
            HActive = hAct,
            VActive = vAct,
            HBlank = hBl,
            HFront = hFp,
            HSync = hSw,
            VFront = vFp,
            VSync = vSw,
            VBack = vBp,
            HSyncPositive = hPos,
            VSyncPositive = vPos,
            Options = options,
            RangeMinHz = rangeMin,
            RangeMaxHz = rangeMax,
            AdaptiveMinHz = adaptiveMin,
            AdaptiveMaxHz = adaptiveMax,
            NativeDescriptor = native
        };
    }

    public static bool ExtensionChecksumsValid(IReadOnlyList<byte[]> descriptors)
    {
        if (descriptors.Count == 0)
            return false;
        for (int i = 0; i < descriptors.Count; i += 5)
        {
            byte[][] chunk = descriptors.Skip(i).Take(5).ToArray();
            byte[] block = BuildExtension(chunk);
            int checksumAt = block[2] + 5;
            int displayIdSum = 0;
            for (int n = 1; n <= checksumAt; n++)
                displayIdSum += block[n];
            int edidSum = 0;
            for (int n = 0; n < 128; n++)
                edidSum += block[n];
            if ((displayIdSum & 0xFF) != 0 || (edidSum & 0xFF) != 0 || block[5] != 0x22 || block[7] != 20 * chunk.Length)
                return false;
        }
        return true;
    }

    public static void BuildOverride(byte[] sourceEdid, IReadOnlyList<byte[]> added, out byte[] block0, out byte[] block1, out byte[][] extras)
    {
        if (sourceEdid.Length < 256)
            throw new InvalidOperationException("EDID is shorter than 256 bytes.");

        var preferred = new List<byte[]>();
        var others = new List<byte[]>();
        CollectDisplayId(sourceEdid, preferred, others);
        if (preferred.Count == 0)
            throw new InvalidOperationException("The panel EDID has no preferred Type VII timing.");

        var addedTimings = new List<byte[]>();
        foreach (byte[] descriptor in added)
        {
            if (descriptor.Length != 20)
                throw new ArgumentException("Type VII descriptor must be 20 bytes.");
            if (preferred.Any(existing => existing.SequenceEqual(descriptor)))
                continue;
            if (addedTimings.Any(existing => existing.SequenceEqual(descriptor)))
                continue;
            addedTimings.Add(descriptor);
        }

        // NVIDIA's GPU-only switch asks the dGPU for a mode list before the panel is moved.
        // Replacing the first DisplayID block makes that list empty, SetDisplayConfig returns
        // ERROR_BAD_CONFIGURATION, and the NVIDIA app snaps back to Auto. Keep the original
        // 240 Hz block first. The extra rates stay in the second block for the Intel driver.
        var primary = new List<byte[]>();
        void Take(byte tag)
        {
            byte[]? block = others.FirstOrDefault(item => item[0] == tag);
            if (block != null)
                primary.Add(block);
        }
        Take(0x20);
        Take(0x21);
        Take(0x26);
        primary.Add(TypeViiBlock(preferred));
        Take(0x81);
        Take(0x2B);
        foreach (byte[] block in others)
        {
            if (block[0] is not (0x20 or 0x21 or 0x26 or 0x81 or 0x2B))
                primary.Add(block);
        }

        var chunks = new List<byte[][]>();
        for (int i = 0; i < addedTimings.Count; i += 5)
            chunks.Add(addedTimings.Skip(i).Take(5).ToArray());

        byte productType = sourceEdid.Length > 131 ? sourceEdid[131] : (byte)0;
        block1 = FinishSection(primary, productType, chunks.Count);
        extras = new byte[chunks.Count][];
        for (int i = 0; i < chunks.Count; i++)
            extras[i] = BuildExtension(chunks[i], chunks.Count - 1 - i);
        block0 = new byte[128];
        Buffer.BlockCopy(sourceEdid, 0, block0, 0, 128);
        block0[126] = (byte)(1 + chunks.Count);
        block0[127] = BlockChecksum(block0, 0);
        if (!EdidChecksumValid(block0) || !EdidChecksumValid(block1) || !DisplayIdChecksumValid(block1))
            throw new InvalidOperationException("EDID override checksum failed.");
        foreach (byte[] extra in extras)
        {
            if (!EdidChecksumValid(extra) || !DisplayIdChecksumValid(extra))
                throw new InvalidOperationException("EDID override checksum failed.");
        }
    }

    private static void CollectDisplayId(byte[] edid, List<byte[]> timings, List<byte[]> others)
    {
        int extensions = Math.Min(edid[126], (edid.Length / 128) - 1);
        for (int extension = 1; extension <= extensions; extension++)
        {
            int origin = extension * 128;
            if (origin + 128 > edid.Length || edid[origin] != 0x70)
                continue;

            int length = edid[origin + 2];
            int offset = origin + 5;
            int end = Math.Min(offset + length, origin + 127);
            while (offset + 3 <= end)
            {
                int payload = edid[offset + 2];
                if (edid[offset] == 0 && payload == 0)
                    break;
                if (offset + 3 + payload > end)
                    break;

                if (edid[offset] == 0x22)
                {
                    int count = payload / 20;
                    for (int i = 0; i < count; i++)
                    {
                        var descriptor = new byte[20];
                        Buffer.BlockCopy(edid, offset + 3 + (i * 20), descriptor, 0, 20);
                        if ((descriptor[3] & 0x80) != 0 && timings.All(existing => !existing.SequenceEqual(descriptor)))
                            timings.Add(descriptor);
                    }
                }
                else
                {
                    var raw = new byte[payload + 3];
                    Buffer.BlockCopy(edid, offset, raw, 0, raw.Length);
                    if (others.All(existing => !existing.SequenceEqual(raw)))
                        others.Add(raw);
                }

                offset += payload + 3;
            }
        }
    }

    private static byte[] TypeViiBlock(IReadOnlyList<byte[]> descriptors)
    {
        if (descriptors.Count < 1 || descriptors.Count > 5)
            throw new InvalidOperationException("The first DisplayID block can hold 1 to 5 Type VII timings.");

        var block = new byte[3 + (20 * descriptors.Count)];
        block[0] = 0x22;
        block[1] = 0x00;
        block[2] = (byte)(20 * descriptors.Count);
        int offset = 3;
        foreach (byte[] descriptor in descriptors)
        {
            Buffer.BlockCopy(descriptor, 0, block, offset, 20);
            offset += 20;
        }
        return block;
    }

    private static byte[] FinishSection(List<byte[]> blocks, byte productType, int extensionCount)
    {
        int length = blocks.Sum(block => block.Length);
        if (length > 121)
            throw new InvalidOperationException("DisplayID section is " + length + " bytes.");

        var section = new byte[128];
        section[0] = 0x70;
        section[1] = 0x20;
        section[2] = (byte)length;
        section[3] = productType;
        section[4] = (byte)extensionCount;
        int offset = 5;
        foreach (byte[] block in blocks)
        {
            Buffer.BlockCopy(block, 0, section, offset, block.Length);
            offset += block.Length;
        }

        int checksumAt = length + 5;
        int sum = 0;
        for (int i = 1; i < checksumAt; i++)
            sum += section[i];
        section[checksumAt] = (byte)((256 - (sum & 0xFF)) & 0xFF);
        section[127] = BlockChecksum(section, 0);
        return section;
    }

    private static bool EdidChecksumValid(byte[] block)
    {
        int sum = 0;
        for (int i = 0; i < 128; i++)
            sum += block[i];
        return (sum & 0xFF) == 0;
    }

    private static bool DisplayIdChecksumValid(byte[] block)
    {
        if (block.Length < 128 || block[0] != 0x70)
            return false;
        int checksumAt = block[2] + 5;
        if (checksumAt > 126)
            return false;
        int sum = 0;
        for (int i = 1; i <= checksumAt; i++)
            sum += block[i];
        return (sum & 0xFF) == 0;
    }

    public static byte[] BuildExtension(IReadOnlyList<byte[]> descriptors, int followingSections = 0)
    {
        if (descriptors.Count < 1 || descriptors.Count > 5)
            throw new ArgumentException("A DisplayID extension can hold 1 to 5 Type VII timings.");

        int payload = 3 + 20 * descriptors.Count;
        var block = new byte[128];
        block[0] = 0x70;
        block[1] = 0x20;
        block[2] = (byte)payload;
        block[3] = 0x00;
        block[4] = (byte)followingSections;
        block[5] = 0x22;
        block[6] = 0x00;
        block[7] = (byte)(20 * descriptors.Count);
        int offset = 8;
        foreach (byte[] descriptor in descriptors)
        {
            if (descriptor.Length != 20)
                throw new ArgumentException("Type VII descriptor must be 20 bytes.");
            Buffer.BlockCopy(descriptor, 0, block, offset, 20);
            offset += 20;
        }

        int checksumAt = payload + 5;
        int sum = 0;
        for (int i = 1; i < checksumAt; i++)
            sum += block[i];
        block[checksumAt] = (byte)((256 - (sum & 0xFF)) & 0xFF);
        block[127] = BlockChecksum(block, 0);
        return block;
    }

    public static byte BlockChecksum(byte[] data, int offset)
    {
        int sum = 0;
        for (int i = 0; i < 127; i++)
            sum += data[offset + i];
        return (byte)((256 - (sum & 0xFF)) & 0xFF);
    }

    private static PanelEdid Fail(string identity, List<string> report, string error)
    {
        report.Add("  " + error);
        return new PanelEdid { Identity = identity, Error = error, Report = report };
    }

    private static string? ReadDescriptorString(byte[] edid, byte tag)
    {
        for (int offset = 54; offset <= 108; offset += 18)
        {
            if (edid[offset] != 0 || edid[offset + 1] != 0 || edid[offset + 3] != tag)
                continue;
            return System.Text.Encoding.ASCII.GetString(edid, offset + 5, 13).Trim('\0', '\n', '\r', ' ');
        }
        return null;
    }

    private static string Describe(byte[] d)
    {
        Decode(d, out int clock, out int hAct, out int hBl, out int hFp, out int hSw, out bool hPos,
            out int vAct, out int vBl, out int vFp, out int vSw, out bool vPos, out byte options);
        int hTot = hAct + hBl;
        int vTot = vAct + vBl;
        double hz = hTot == 0 || vTot == 0 ? 0 : (clock * 1000.0) / (hTot * (double)vTot);
        string preferred = (options & 0x80) != 0 ? ", preferred" : "";
        return clock + " kHz, " + hAct + "x" + vAct
            + ", horizontal " + hFp + "+" + hSw + "+" + (hBl - hFp - hSw)
            + (hPos ? " +" : " -")
            + ", vertical " + vFp + "+" + vSw + "+" + (vBl - vFp - vSw)
            + (vPos ? " +" : " -")
            + ", total " + hTot + "x" + vTot
            + ", " + hz.ToString("0.000") + " Hz" + preferred;
    }

    private static void Decode(byte[] x, out int clock, out int hAct, out int hBl, out int hFp, out int hSw, out bool hPos,
        out int vAct, out int vBl, out int vFp, out int vSw, out bool vPos, out byte options)
    {
        clock = 1 + x[0] + (x[1] << 8) + (x[2] << 16);
        options = x[3];
        hAct = 1 + x[4] + (x[5] << 8);
        hBl = 1 + x[6] + (x[7] << 8);
        hFp = 1 + x[8] + ((x[9] & 0x7F) << 8);
        hSw = 1 + x[10] + (x[11] << 8);
        hPos = (x[9] & 0x80) != 0;
        vAct = 1 + x[12] + (x[13] << 8);
        vBl = 1 + x[14] + (x[15] << 8);
        vFp = 1 + x[16] + ((x[17] & 0x7F) << 8);
        vSw = 1 + x[18] + (x[19] << 8);
        vPos = (x[17] & 0x80) != 0;
    }

    private static void WriteClock(byte[] d, int khz)
    {
        int stored = khz - 1;
        d[0] = (byte)stored;
        d[1] = (byte)(stored >> 8);
        d[2] = (byte)(stored >> 16);
    }

    private static void Write16(byte[] d, int offset, int value)
    {
        int stored = value - 1;
        d[offset] = (byte)stored;
        d[offset + 1] = (byte)(stored >> 8);
    }

    private static void Write15(byte[] d, int offset, int value, bool positive)
    {
        int stored = value - 1;
        d[offset] = (byte)stored;
        d[offset + 1] = (byte)((stored >> 8) & 0x7F);
        if (positive)
            d[offset + 1] |= 0x80;
    }
}

internal static class Native
{
    public const int EnumCurrentSettings = -1;
    public const uint QdcOnlyActivePaths = 2;
    public const int ModeInfoTypeTarget = 2;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    public struct DisplayDevice
    {
        public int cb;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceString;
        public int StateFlags;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceID;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceKey;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    public struct DevMode
    {
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmDeviceName;
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
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmFormName;
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

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    public static extern bool EnumDisplayDevices(string? lpDevice, uint iDevNum, ref DisplayDevice lpDisplayDevice, uint dwFlags);

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    public static extern bool EnumDisplaySettings(string? deviceName, int modeNum, ref DevMode devMode);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    public struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    public struct Rational
    {
        public uint Numerator;
        public uint Denominator;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    public struct PathSource
    {
        public Luid adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint statusFlags;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    public struct PathTarget
    {
        public Luid adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint outputTechnology;
        public uint rotation;
        public uint scaling;
        public Rational refreshRate;
        public uint scanLineOrdering;
        public int targetAvailable;
        public uint statusFlags;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    public struct PathInfo
    {
        public PathSource Source;
        public PathTarget Target;
        public uint flags;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    public struct Region
    {
        public uint cx;
        public uint cy;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    public struct VideoSignal
    {
        public ulong pixelRate;
        public Rational hSyncFreq;
        public Rational vSyncFreq;
        public Region activeSize;
        public Region totalSize;
        public uint videoStandard;
        public uint scanLineOrdering;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    public struct TargetMode
    {
        public VideoSignal video;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    public struct SourceMode
    {
        public uint width;
        public uint height;
        public uint pixelFormat;
        public int x;
        public int y;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit)]
    public struct ModeUnion
    {
        [System.Runtime.InteropServices.FieldOffset(0)]
        public TargetMode targetMode;
        [System.Runtime.InteropServices.FieldOffset(0)]
        public SourceMode sourceMode;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    public struct ModeInfo
    {
        public int infoType;
        public uint id;
        public Luid adapterId;
        public ModeUnion mode;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern int QueryDisplayConfig(
        uint flags,
        ref uint numPathArrayElements,
        [System.Runtime.InteropServices.Out] PathInfo[] pathArray,
        ref uint numModeInfoArrayElements,
        [System.Runtime.InteropServices.Out] ModeInfo[] modeInfoArray,
        IntPtr currentTopologyId);
}
