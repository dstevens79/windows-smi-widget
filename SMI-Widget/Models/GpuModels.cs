namespace SMI_Widget.Models;

public sealed class GpuSnapshot
{
    public int Index { get; init; }
    public string Name { get; init; } = "NVIDIA GPU";
    public string Brand { get; init; } = "";
    public string Architecture { get; init; } = "";
    public string Uuid { get; init; } = "";
    public string BusId { get; init; } = "";
    public string Serial { get; init; } = "";
    public string PartNumber { get; init; } = "";
    public string Vbios { get; init; } = "";
    public string DriverVersion { get; init; } = "";
    public string CudaVersion { get; init; } = "";
    public string DriverModel { get; init; } = "";
    public string GspFirmware { get; init; } = "";
    public string PerformanceState { get; init; } = "";
    public string PersistenceMode { get; init; } = "";
    public string ComputeMode { get; init; } = "";
    public string DisplayMode { get; init; } = "";
    public string DisplayActive { get; init; } = "";
    public string AccountingMode { get; init; } = "";
    public string MigMode { get; init; } = "";
    public double? FanPercent { get; init; }
    public string FanText { get; init; } = "";
    public double? GpuUtil { get; init; }
    public double? MemoryUtil { get; init; }
    public double? EncoderUtil { get; init; }
    public double? DecoderUtil { get; init; }
    public double? JpegUtil { get; init; }
    public double? OfaUtil { get; init; }
    public double? MemoryTotalMiB { get; init; }
    public double? MemoryUsedMiB { get; init; }
    public double? MemoryFreeMiB { get; init; }
    public double? MemoryReservedMiB { get; init; }
    public double? Bar1TotalMiB { get; init; }
    public double? Bar1UsedMiB { get; init; }
    public double? GpuTempC { get; init; }
    public double? MemoryTempC { get; init; }
    public double? SlowTempC { get; init; }
    public double? MaxTempC { get; init; }
    public double? TargetTempC { get; init; }
    public double? TargetTempMinC { get; init; }
    public double? TargetTempMaxC { get; init; }
    public double? TempMarginC { get; init; }
    public double? PowerDrawW { get; init; }
    public double? PowerAverageW { get; init; }
    public double? PowerLimitW { get; init; }
    public double? PowerRequestedW { get; init; }
    public double? PowerDefaultW { get; init; }
    public double? PowerMinW { get; init; }
    public double? PowerMaxW { get; init; }
    public string PowerManagement { get; init; } = "";
    public double? GraphicsClockMHz { get; init; }
    public double? SmClockMHz { get; init; }
    public double? MemoryClockMHz { get; init; }
    public double? VideoClockMHz { get; init; }
    public double? MaxGraphicsClockMHz { get; init; }
    public double? MaxSmClockMHz { get; init; }
    public double? MaxMemoryClockMHz { get; init; }
    public double? MaxVideoClockMHz { get; init; }
    public double? AppGraphicsClockMHz { get; init; }
    public double? AppMemoryClockMHz { get; init; }
    public double? BoostClockMHz { get; init; }
    public double? GraphicsVoltageMv { get; init; }
    public string PcieGenCurrent { get; init; } = "";
    public string PcieGenMax { get; init; } = "";
    public string PcieWidthCurrent { get; init; } = "";
    public string PcieWidthMax { get; init; } = "";
    public string PcieTx { get; init; } = "";
    public string PcieRx { get; init; } = "";
    public string EccSummary { get; init; } = "";
    public string RetiredPagesSummary { get; init; } = "";
    public string RemappedRowsSummary { get; init; } = "";
    public string EncoderStatsSummary { get; init; } = "";
    public string FbcStatsSummary { get; init; } = "";
    public List<ThrottleReason> Throttles { get; init; } = [];
    public List<GpuProcess> Processes { get; init; } = [];

    public string ShortName =>
        Name.StartsWith("NVIDIA ", StringComparison.OrdinalIgnoreCase) ? Name[7..] : Name;

    public bool CanSetPower =>
        PowerMinW is > 0 && PowerMaxW is > 0 && PowerMaxW > PowerMinW;

    public bool CanSetTargetTemp =>
        TargetTempMinC is > 0 && TargetTempMaxC.HasValue && TargetTempMinC.HasValue &&
        TargetTempMaxC.Value > TargetTempMinC.Value;

    public bool PersistenceSupported => IsReported(PersistenceMode);
    public bool ComputeSupported => IsReported(ComputeMode);
    public bool ApplicationsClocksSupported => AppGraphicsClockMHz.HasValue || AppMemoryClockMHz.HasValue;

    public static bool IsReported(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        !value.Equals("N/A", StringComparison.OrdinalIgnoreCase) &&
        !value.Contains("Not Supported", StringComparison.OrdinalIgnoreCase) &&
        !value.Contains("not supported", StringComparison.OrdinalIgnoreCase);
}

public sealed class ThrottleReason
{
    public string Key { get; init; } = "";
    public string Label { get; init; } = "";
    public bool Active { get; init; }
    public bool Warning { get; init; }
}

public sealed class GpuProcess
{
    public string Pid { get; init; } = "";
    public string Name { get; init; } = "";
    public string Type { get; init; } = "";
    public string MemoryText { get; init; } = "";
    public double MemoryMiB { get; init; }
}

public sealed class SmiQueryResult
{
    public bool Success { get; init; }
    public string Error { get; init; } = "";
    public string DriverVersion { get; init; } = "";
    public string CudaVersion { get; init; } = "";
    public List<GpuSnapshot> Gpus { get; init; } = [];
}

public sealed class CommandResult
{
    public bool Success { get; init; }
    public bool Cancelled { get; init; }
    public string Message { get; init; } = "";

    public static CommandResult Ok(string message) => new() { Success = true, Message = message };
    public static CommandResult Fail(string message) => new() { Success = false, Message = message };
    public static CommandResult Cancel(string message) => new() { Cancelled = true, Message = message };
}

public sealed class WidgetSettings
{
    public double Left { get; set; } = double.NaN;
    public double Top { get; set; } = double.NaN;
    public bool AlwaysOnTop { get; set; } = true;
    public double CardOpacity { get; set; } = 0.96;
    public int RefreshMs { get; set; } = 1000;
    public bool DetailsOpen { get; set; }
    public bool ControlsOpen { get; set; }
    public int SelectedGpuIndex { get; set; }
    public bool StartWithWindows { get; set; }
}
