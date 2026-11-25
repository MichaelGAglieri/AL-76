using System.Runtime.InteropServices;
using AL76.Core.Hardware;

namespace AL76.Linux.Platform;

/// <summary>Measures the PC from /proc and /sys, with no extra packages (Windows: WindowsHardware).</summary>
public static class LinuxHardware
{
    public static HardwareInfo Probe() =>
        new(InstalledGB() ?? RamGB(File.ReadAllText("/proc/meminfo")), Environment.ProcessorCount, RuntimeInformation.OSArchitecture.ToString(), Gpus());

    /// <summary>
    /// The memory installed, from the online blocks of /sys/devices/system/memory. MemTotal leaves out what the kernel
    /// and the graphics keep for themselves: an 8 GB VM said 7.2 GB and fell under the 7.5 GB requirement of the levels,
    /// while Windows reports nearly the whole 8. Null where the blocks are not there (containers): then MemTotal.
    /// </summary>
    public static double? InstalledGB(string memory = "/sys/devices/system/memory")
    {
        try
        {
            string block = File.ReadAllText(Path.Combine(memory, "block_size_bytes")).Trim();
            long size = Convert.ToInt64(block, 16);
            int online = Directory.GetDirectories(memory, "memory*")
                .Count(d => File.Exists(Path.Combine(d, "state")) && File.ReadAllText(Path.Combine(d, "state")).Trim() == "online");
            return size > 0 && online > 0 ? size * (double)online / 1024 / 1024 / 1024 : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException or ArgumentException) { return null; }
    }

    /// <summary>MemTotal in GB, from the text of /proc/meminfo ("MemTotal: 16316412 kB").</summary>
    public static double RamGB(string meminfo) => Field(meminfo, "MemTotal") / 1024.0 / 1024;

    /// <summary>Free memory in percent (MemAvailable of MemTotal), or null.</summary>
    public static int? FreeMemoryPercent()
    {
        try { return FreePercent(File.ReadAllText("/proc/meminfo")); }
        catch (IOException) { return null; }
    }

    public static int? FreePercent(string meminfo)
    {
        long total = Field(meminfo, "MemTotal"), available = Field(meminfo, "MemAvailable");
        return total > 0 && available >= 0 ? (int)Math.Round(100.0 * available / total) : null;
    }

    private static long Field(string meminfo, string name)
    {
        foreach (var line in meminfo.Split('\n'))
        {
            if (!line.StartsWith(name + ":", StringComparison.Ordinal)) continue;
            var parts = line[(name.Length + 1)..].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 0 && long.TryParse(parts[0], out long kb)) return kb;
        }
        return -1;
    }

    /// <summary>
    /// Graphics cards from /sys/class/drm/card*/device: the PCI vendor gives the maker (NVIDIA 0x10de, AMD 0x1002,
    /// Intel 0x8086), the driver name the rest (virtio_gpu, vmwgfx in a VM). NVIDIA's memory comes from nvidia-smi
    /// when it is there; otherwise unknown.
    /// </summary>
    public static List<GpuInfo> Gpus(string drm = "/sys/class/drm")
    {
        var result = new List<GpuInfo>();
        if (!Directory.Exists(drm)) return result;
        foreach (var card in Directory.GetDirectories(drm, "card*").Where(d => !Path.GetFileName(d).Contains('-')).Order())
        {
            string device = Path.Combine(card, "device");
            string vendor = Read(Path.Combine(device, "vendor"));
            string driver = "";
            try { driver = Path.GetFileName(new FileInfo(Path.Combine(device, "driver")).LinkTarget ?? ""); } catch (IOException) { }
            string name = GpuName(vendor, driver);
            double? vram = name.StartsWith("NVIDIA", StringComparison.Ordinal) ? NvidiaVramGB() : AmdVramGB(device);
            if (!result.Any(g => g.Name == name)) result.Add(new GpuInfo(name, vram));
        }
        return result;
    }

    public static string GpuName(string vendor, string driver) => vendor.Trim().ToLowerInvariant() switch
    {
        "0x10de" => "NVIDIA" + (driver.Length > 0 ? $" ({driver})" : ""),
        "0x1002" => "AMD" + (driver.Length > 0 ? $" ({driver})" : ""),
        "0x8086" => "Intel" + (driver.Length > 0 ? $" ({driver})" : ""),
        "0x15ad" => "VMware SVGA",
        "0x1af4" => "virtio-gpu",
        _ => driver.Length > 0 ? driver : "sconosciuta",
    };

    private static double? AmdVramGB(string device) =>
        long.TryParse(Read(Path.Combine(device, "mem_info_vram_total")), out long bytes) && bytes > 0 ? bytes / 1024.0 / 1024 / 1024 : null;

    private static double? NvidiaVramGB()
    {
        try
        {
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("nvidia-smi", "--query-gpu=memory.total --format=csv,noheader,nounits")
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false });
            if (p is null) return null;
            string first = p.StandardOutput.ReadLine() ?? "";
            p.WaitForExit(3000);
            return double.TryParse(first.Trim(), System.Globalization.CultureInfo.InvariantCulture, out double mb) ? mb / 1024 : null;
        }
        catch (System.ComponentModel.Win32Exception) { return null; }
    }

    private static string Read(string path)
    {
        try { return File.ReadAllText(path).Trim(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return ""; }
    }
}
