using System.Runtime.InteropServices;
using AL76.Core.Hardware;
using Microsoft.Win32;

namespace AL76.Platform;

/// <summary>Measures the PC with plain Windows APIs (no WMI, no extra packages).</summary>
public static class WindowsHardware
{
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx status);

    /// <summary>Display adapters class in the registry: one subkey per adapter.</summary>
    private const string DisplayClass = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    public static HardwareInfo Probe() =>
        new(RamGB(), Environment.ProcessorCount, RuntimeInformation.OSArchitecture.ToString(), Gpus());

    /// <summary>Free physical memory in percent (100 minus Windows' memory load), or null.</summary>
    public static int? FreeMemoryPercent()
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        return GlobalMemoryStatusEx(ref status) ? 100 - (int)status.MemoryLoad : null;
    }

    private static double RamGB()
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        return GlobalMemoryStatusEx(ref status) ? status.TotalPhys / 1024.0 / 1024 / 1024 : 0;
    }

    private static List<GpuInfo> Gpus()
    {
        var result = new List<GpuInfo>();
        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(DisplayClass);
            if (root is null) return result;
            foreach (var name in root.GetSubKeyNames().Where(n => n.All(char.IsDigit)))
            {
                using var key = root.OpenSubKey(name);
                if (key?.GetValue("DriverDesc") is not string desc) continue;
                double? vram = key.GetValue("HardwareInformation.qwMemorySize") switch
                {
                    long bytes => bytes / 1024.0 / 1024 / 1024,
                    byte[] raw when raw.Length >= 8 => BitConverter.ToInt64(raw, 0) / 1024.0 / 1024 / 1024,
                    _ => null,
                };
                if (!result.Any(g => g.Name == desc)) result.Add(new GpuInfo(desc, vram));
            }
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException) { }
        return result;
    }
}
