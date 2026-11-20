namespace AL76.Core.Hardware;

/// <summary>One graphics card as Windows reports it. Video memory can be unknown.</summary>
public sealed record GpuInfo(string Name, double? VramGB)
{
    public bool IsNvidia => Name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase);
}

/// <summary>What the PC has, measured at start-up by the Platform project.</summary>
public sealed record HardwareInfo(double RamGB, int LogicalCores, string Architecture, IReadOnlyList<GpuInfo> Gpus);
