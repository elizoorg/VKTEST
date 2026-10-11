using System.Management;


namespace BmwBenchmarkAutomator;

public sealed class GpuInfo
{
    public string Name { get; set; } = "";
    public string Driver { get; set; } = "";
    public double VramGb { get; set; }
}

public sealed class HardwareReport
{
    public string Cpu { get; set; } = "";
    public string Ram { get; set; } = "";
    public string Os { get; set; } = "";
    public string Screen { get; set; } = "";
    public List<GpuInfo> Gpus { get; set; } = new();
}

public static class HardwareInfo
{
    public static HardwareReport Collect()
    {
        var report = new HardwareReport();

        try
        {
            using var s = new ManagementObjectSearcher(
                "SELECT Name, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed FROM Win32_Processor");
            foreach (var o in s.Get())
            {
                var mhz = double.TryParse(o["MaxClockSpeed"]?.ToString(), out var m) ? m : 0;
                report.Cpu = $"{o["Name"]?.ToString()?.Trim()} " +
                             $"({o["NumberOfCores"]} ядер / {o["NumberOfLogicalProcessors"]} потоков" +
                             (mhz > 0 ? $", до {mhz / 1000:0.##} ГГц)" : ")");
                break;
            }
        }
        catch (Exception ex) { Log.Warn($"CPU info: {ex.Message}"); report.Cpu = "<нет данных>"; }

        try
        {
            using var s = new ManagementObjectSearcher(
                "SELECT Capacity, Speed, ConfiguredClockSpeed FROM Win32_PhysicalMemory");
            double totalBytes = 0;
            var speeds = new List<int>();
            var count = 0;
            foreach (var o in s.Get())
            {
                totalBytes += double.TryParse(o["Capacity"]?.ToString(), out var c) ? c : 0;
                var spd = int.TryParse(o["ConfiguredClockSpeed"]?.ToString(), out var sp) ? sp
                        : int.TryParse(o["Speed"]?.ToString(), out var sp2) ? sp2 : 0;
                if (spd > 0) speeds.Add(spd);
                count++;
            }
            report.Ram = $"{totalBytes / 1024 / 1024 / 1024:0.#} ГБ" +
                         (speeds.Count > 0 ? $" (частота до {speeds.Max()} МГц)" : "") +
                         $" — {count} модулей";
        }
        catch (Exception ex) { Log.Warn($"RAM info: {ex.Message}"); }

        try
        {
            using var s = new ManagementObjectSearcher("SELECT Name, DriverVersion FROM Win32_VideoController");
            foreach (var o in s.Get())
                report.Gpus.Add(new GpuInfo
                {
                    Name = o["Name"]?.ToString()?.Trim() ?? "?",
                    Driver = o["DriverVersion"]?.ToString() ?? "?",
                });
            FillVramFromRegistry(report.Gpus);
        }
        catch (Exception ex) { Log.Warn($"GPU info: {ex.Message}"); }

        try
        {
            using var s = new ManagementObjectSearcher("SELECT Caption, BuildNumber FROM Win32_OperatingSystem");
            foreach (var o in s.Get())
            {
                report.Os = $"{o["Caption"]} (build {o["BuildNumber"]})";
                break;
            }
        }
        catch { report.Os = Environment.OSVersion.VersionString; }

        var scr = System.Windows.Forms.Screen.PrimaryScreen?.Bounds;
        report.Screen = scr is { Width: > 0 } ? $"{scr.Value.Width}×{scr.Value.Height}" : "?";

        return report;
    }

    /// WMI AdapterRAM часто «ограничен» 4 ГБ — точный объём VRAM берём из реестра видеодрайвера.
    private static void FillVramFromRegistry(List<GpuInfo> gpus)
    {
        try
        {
            using var baseKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (baseKey == null) return;

            foreach (var sub in baseKey.GetSubKeyNames())
            {
                using var k = baseKey.OpenSubKey(sub);
                if (k?.GetValue("DriverDesc") is not string desc) continue;

                var gpu = gpus.FirstOrDefault(g =>
                    desc.Equals(g.Name, StringComparison.OrdinalIgnoreCase) ||
                    g.Name.Contains(desc, StringComparison.OrdinalIgnoreCase) ||
                    desc.Contains(g.Name, StringComparison.OrdinalIgnoreCase));
                if (gpu == null || gpu.VramGb > 0) continue;

                long bytes = 0;
                if (k.GetValue("HardwareInformation.qwMemorySize") is long l) bytes = l;
                else if (k.GetValue("HardwareInformation.qwMemorySize") is int i) bytes = i;
                else if (k.GetValue("HardwareInformation.qwMemorySize") is byte[] arr && arr.Length >= 4)
                    bytes = arr.Length >= 8 ? BitConverter.ToInt64(arr, 0) : BitConverter.ToInt32(arr, 0);

                if (bytes > 0) gpu.VramGb = bytes / 1024.0 / 1024.0 / 1024.0;
            }
        }
        catch { }
    }
}