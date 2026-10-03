using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using SMI_Widget.Models;

namespace SMI_Widget.Services;

/// <summary>
/// Reads nvidia-smi -q -x (everything useful, without the huge supported-clocks dump)
/// and applies the small set of official setter flags.
/// </summary>
public sealed partial class NvidiaSmiClient
{
    private static readonly Encoding OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);
    private string? _cachedPath;

    public async Task<SmiQueryResult> QueryAsync(CancellationToken cancellationToken)
    {
        var xmlRun = await RunCapturedAsync("-q -x", TimeSpan.FromSeconds(12), cancellationToken);
        if (xmlRun.Started)
        {
            var xml = ExtractXml(xmlRun.Output);
            if (xml != null)
            {
                try
                {
                    return ParseXml(xml);
                }
                catch (Exception ex) when (ex is XmlException or InvalidOperationException)
                {
                    // Fall through to the compact CSV query.
                }
            }
            else if (!xmlRun.Success && !string.IsNullOrWhiteSpace(xmlRun.Output))
            {
                var csv = await QueryCsvAsync(cancellationToken);
                return csv.Success ? csv : new SmiQueryResult { Error = Formatting.SummarizeOutput(xmlRun.Output, "nvidia-smi failed.") };
            }
        }
        else if (!string.IsNullOrWhiteSpace(xmlRun.Output))
        {
            return new SmiQueryResult { Error = xmlRun.Output.Trim() };
        }

        var fallback = await QueryCsvAsync(cancellationToken);
        if (fallback.Success) return fallback;
        return new SmiQueryResult
        {
            Error = string.IsNullOrWhiteSpace(fallback.Error)
                ? "nvidia-smi did not return GPU data."
                : fallback.Error
        };
    }

    public Task<CommandResult> SetPowerLimitAsync(int gpuIndex, int watts, CancellationToken cancellationToken) =>
        RunControlAsync($"-i {gpuIndex} -pl {watts.ToString(CultureInfo.InvariantCulture)}", cancellationToken);

    public Task<CommandResult> LockGpuClocksAsync(int gpuIndex, int minMHz, int maxMHz, CancellationToken cancellationToken) =>
        RunControlAsync($"-i {gpuIndex} -lgc {minMHz.ToString(CultureInfo.InvariantCulture)},{maxMHz.ToString(CultureInfo.InvariantCulture)}", cancellationToken);

    public Task<CommandResult> ResetGpuClocksAsync(int gpuIndex, CancellationToken cancellationToken) =>
        RunControlAsync($"-i {gpuIndex} -rgc", cancellationToken);

    public Task<CommandResult> LockMemoryClocksAsync(int gpuIndex, int minMHz, int maxMHz, CancellationToken cancellationToken) =>
        RunControlAsync($"-i {gpuIndex} -lmc {minMHz.ToString(CultureInfo.InvariantCulture)},{maxMHz.ToString(CultureInfo.InvariantCulture)}", cancellationToken);

    public Task<CommandResult> ResetMemoryClocksAsync(int gpuIndex, CancellationToken cancellationToken) =>
        RunControlAsync($"-i {gpuIndex} -rmc", cancellationToken);

    public Task<CommandResult> SetApplicationsClocksAsync(int gpuIndex, int memoryMHz, int graphicsMHz, CancellationToken cancellationToken) =>
        RunControlAsync($"-i {gpuIndex} -ac {memoryMHz.ToString(CultureInfo.InvariantCulture)},{graphicsMHz.ToString(CultureInfo.InvariantCulture)}", cancellationToken);

    public Task<CommandResult> ResetApplicationsClocksAsync(int gpuIndex, CancellationToken cancellationToken) =>
        RunControlAsync($"-i {gpuIndex} -rac", cancellationToken);

    public Task<CommandResult> SetTargetTemperatureAsync(int gpuIndex, int celsius, CancellationToken cancellationToken) =>
        RunControlAsync($"-i {gpuIndex} -gtt {celsius.ToString(CultureInfo.InvariantCulture)}", cancellationToken);

    public Task<CommandResult> SetPersistenceModeAsync(int gpuIndex, bool enabled, CancellationToken cancellationToken) =>
        RunControlAsync($"-i {gpuIndex} -pm {(enabled ? 1 : 0)}", cancellationToken);

    public Task<CommandResult> SetComputeModeAsync(int gpuIndex, int mode, CancellationToken cancellationToken) =>
        RunControlAsync($"-i {gpuIndex} -c {mode.ToString(CultureInfo.InvariantCulture)}", cancellationToken);

    private async Task<SmiQueryResult> QueryCsvAsync(CancellationToken cancellationToken)
    {
        const string fields =
            "index,name,uuid,pci.bus_id,pstate,temperature.gpu,fan.speed,utilization.gpu,utilization.memory," +
            "memory.total,memory.used,memory.free,power.draw,power.limit,power.min_limit,power.max_limit," +
            "power.default_limit,clocks.gr,clocks.sm,clocks.mem,clocks.video,compute_mode,persistence_mode";
        var run = await RunCapturedAsync($"--query-gpu={fields} --format=csv,noheader,nounits", TimeSpan.FromSeconds(8), cancellationToken);
        if (!run.Started || string.IsNullOrWhiteSpace(run.Output))
            return new SmiQueryResult { Error = string.IsNullOrWhiteSpace(run.Output) ? "nvidia-smi failed." : run.Output.Trim() };

        var gpus = new List<GpuSnapshot>();
        foreach (var line in run.Output.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith("NVIDIA-SMI", StringComparison.OrdinalIgnoreCase)) continue;
            var cols = trimmed.Split(',').Select(part => part.Trim()).ToArray();
            if (cols.Length < 10) continue;
            gpus.Add(ParseCsvRow(cols));
        }

        if (gpus.Count == 0)
            return new SmiQueryResult { Error = Formatting.SummarizeOutput(run.Output, "nvidia-smi returned no GPUs.") };

        return new SmiQueryResult { Success = true, Gpus = gpus };
    }

    private static GpuSnapshot ParseCsvRow(string[] cols)
    {
        string At(int index) => index < cols.Length ? cols[index] : "";
        return new GpuSnapshot
        {
            Index = (int)(ParseNumber(At(0)) ?? 0),
            Name = BlankTo(At(1), "NVIDIA GPU"),
            Uuid = Clean(At(2)),
            BusId = Clean(At(3)),
            PerformanceState = Clean(At(4)),
            GpuTempC = ParseNumber(At(5)),
            FanPercent = ParseNumber(At(6)),
            FanText = ParseNumber(At(6)) is double fan ? $"{fan:0}%" : "",
            GpuUtil = ParseNumber(At(7)),
            MemoryUtil = ParseNumber(At(8)),
            MemoryTotalMiB = ParseNumber(At(9)),
            MemoryUsedMiB = ParseNumber(At(10)),
            MemoryFreeMiB = ParseNumber(At(11)),
            PowerDrawW = ParseNumber(At(12)),
            PowerLimitW = ParseNumber(At(13)),
            PowerMinW = ParseNumber(At(14)),
            PowerMaxW = ParseNumber(At(15)),
            PowerDefaultW = ParseNumber(At(16)),
            GraphicsClockMHz = ParseNumber(At(17)),
            SmClockMHz = ParseNumber(At(18)),
            MemoryClockMHz = ParseNumber(At(19)),
            VideoClockMHz = ParseNumber(At(20)),
            ComputeMode = Clean(At(21)),
            PersistenceMode = Clean(At(22))
        };
    }

    private async Task<CommandResult> RunControlAsync(string arguments, CancellationToken cancellationToken)
    {
        var direct = await RunCapturedAsync(arguments, TimeSpan.FromSeconds(20), cancellationToken);
        if (!direct.Started)
            return CommandResult.Fail(string.IsNullOrWhiteSpace(direct.Output) ? "Could not start nvidia-smi." : direct.Output.Trim());

        if (direct.Success && !LooksLikeFailure(direct.Output))
            return CommandResult.Ok(Formatting.SummarizeOutput(direct.Output, "Applied."));

        if (!NeedsAdmin(direct))
            return CommandResult.Fail(Formatting.SummarizeOutput(direct.Output, "nvidia-smi rejected the change."));

        try
        {
            return await RunElevatedAsync(arguments, cancellationToken);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return CommandResult.Cancel("Administrator approval was cancelled.");
        }
    }

    private async Task<CommandResult> RunElevatedAsync(string arguments, CancellationToken cancellationToken)
    {
        var exe = ResolvePath();
        var log = Path.Combine(Path.GetTempPath(), "smi-widget-" + Guid.NewGuid().ToString("N") + ".log");
        var command = $"/c \"\"{exe}\" {arguments} > \"{log}\" 2>&1\"";
        var start = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = command,
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden
        };

        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start elevated nvidia-smi.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (Exception) { /* already gone */ }
            return CommandResult.Fail("Timed out waiting for administrator approval.");
        }

        var text = File.Exists(log) ? await File.ReadAllTextAsync(log, cancellationToken) : "";
        try { File.Delete(log); } catch (IOException) { /* temp log can stay */ }

        var ok = process.ExitCode == 0 && !LooksLikeFailure(text);
        var message = Formatting.SummarizeOutput(text, ok ? "Applied." : "nvidia-smi failed.");
        return ok ? CommandResult.Ok(message) : CommandResult.Fail(message);
    }

    private async Task<Captured> RunCapturedAsync(string arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var exe = ResolvePath();
        var start = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = OutputEncoding,
            StandardErrorEncoding = OutputEncoding
        };

        using var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start())
                return Captured.Fail("Could not start nvidia-smi.");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 2)
        {
            return Captured.Fail("nvidia-smi.exe was not found. Install the NVIDIA driver, then reopen the widget.");
        }
        catch (Win32Exception ex)
        {
            return Captured.Fail(ex.Message);
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(linked.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (Exception) { /* already gone */ }
            return Captured.Fail("nvidia-smi timed out.");
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        var combined = string.Join(Environment.NewLine, new[] { stdout, stderr }.Where(part => !string.IsNullOrWhiteSpace(part)));
        return new Captured(true, process.ExitCode == 0, process.ExitCode, combined);
    }

    private string ResolvePath()
    {
        if (_cachedPath != null && File.Exists(_cachedPath)) return _cachedPath;

        var candidates = new[]
        {
            Path.Combine(Environment.SystemDirectory, "nvidia-smi.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation", "NVSMI", "nvidia-smi.exe")
        };
        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
                return _cachedPath = candidate;
        }

        return "nvidia-smi.exe";
    }

    private static SmiQueryResult ParseXml(string xml)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null
        };
        using var reader = XmlReader.Create(new StringReader(xml), settings);
        var doc = XDocument.Load(reader);
        var root = doc.Root ?? throw new XmlException("nvidia-smi returned an empty document.");
        var driver = Text(root, "driver_version");
        var cuda = Text(root, "cuda_version");
        var nodes = root.Elements().Where(e => e.Name.LocalName.Equals("gpu", StringComparison.OrdinalIgnoreCase)).ToList();
        if (nodes.Count == 0)
            return new SmiQueryResult { Error = "No NVIDIA GPU was reported by nvidia-smi." };

        var gpus = new List<GpuSnapshot>(nodes.Count);
        for (var i = 0; i < nodes.Count; i++)
            gpus.Add(ParseGpu(nodes[i], i, driver, cuda));

        return new SmiQueryResult
        {
            Success = true,
            DriverVersion = driver,
            CudaVersion = cuda,
            Gpus = gpus
        };
    }

    private static GpuSnapshot ParseGpu(XElement gpu, int index, string driver, string cuda)
    {
        var pci = Child(gpu, "pci");
        var link = Child(pci, "pci_gpu_link_info");
        var gen = Child(link, "pcie_gen");
        var widths = Child(link, "link_widths");
        var fb = Child(gpu, "fb_memory_usage");
        var bar1 = Child(gpu, "bar1_memory_usage");
        var util = Child(gpu, "utilization");
        var temp = Child(gpu, "temperature");
        var target = Child(gpu, "supported_gpu_target_temp");
        var power = Find(gpu, "gpu_power_readings", "power_readings", "module_power_readings");
        var clocks = Child(gpu, "clocks");
        var maxClocks = Child(gpu, "max_clocks");
        var appClocks = Child(gpu, "applications_clocks");
        var boost = Child(gpu, "max_customer_boost_clocks");
        var voltage = Child(gpu, "voltage");
        var fan = ParseFan(Child(gpu, "fan_speed"));
        var driverModel = Child(gpu, "driver_model");
        var mig = Child(gpu, "mig_mode");

        return new GpuSnapshot
        {
            Index = index,
            Name = BlankTo(Text(gpu, "product_name"), "NVIDIA GPU"),
            Brand = Clean(Text(gpu, "product_brand")),
            Architecture = Clean(Text(gpu, "product_architecture")),
            Uuid = Clean(Text(gpu, "uuid")),
            BusId = FirstNonBlank(gpu.Attribute("id")?.Value, Text(pci, "pci_bus_id")),
            Serial = Clean(Text(gpu, "serial")),
            PartNumber = Clean(Text(gpu, "gpu_part_number")),
            Vbios = Clean(Text(gpu, "vbios_version")),
            DriverVersion = Clean(driver),
            CudaVersion = Clean(cuda),
            DriverModel = Clean(FirstText(driverModel, "current_dm", "current")),
            GspFirmware = Clean(Text(gpu, "gsp_firmware_version")),
            PerformanceState = Clean(Text(gpu, "performance_state")),
            PersistenceMode = Clean(Text(gpu, "persistence_mode")),
            ComputeMode = Clean(Text(gpu, "compute_mode")),
            DisplayMode = Clean(Text(gpu, "display_mode")),
            DisplayActive = Clean(Text(gpu, "display_active")),
            AccountingMode = Clean(Text(gpu, "accounting_mode")),
            MigMode = Clean(FirstText(mig, "current_mig", "current")),
            FanPercent = fan.Percent,
            FanText = fan.Text,
            GpuUtil = Num(util, "gpu_util"),
            MemoryUtil = Num(util, "memory_util"),
            EncoderUtil = Num(util, "encoder_util"),
            DecoderUtil = Num(util, "decoder_util"),
            JpegUtil = Num(util, "jpeg_util"),
            OfaUtil = Num(util, "ofa_util"),
            MemoryTotalMiB = Num(fb, "total"),
            MemoryUsedMiB = Num(fb, "used"),
            MemoryFreeMiB = Num(fb, "free"),
            MemoryReservedMiB = Num(fb, "reserved"),
            Bar1TotalMiB = Num(bar1, "total"),
            Bar1UsedMiB = Num(bar1, "used"),
            GpuTempC = FirstNum(temp, "gpu_temp", "gpu_temp_current"),
            MemoryTempC = Num(temp, "memory_temp"),
            SlowTempC = Num(temp, "gpu_temp_slow_threshold"),
            MaxTempC = Num(temp, "gpu_temp_max_threshold"),
            TargetTempC = Num(temp, "gpu_target_temperature"),
            TargetTempMinC = FirstNum(target, "gpu_target_temp_min", "min"),
            TargetTempMaxC = FirstNum(target, "gpu_target_temp_max", "max"),
            TempMarginC = Num(temp, "gpu_temp_tlimit"),
            PowerDrawW = FirstNum(power, "instant_power_draw", "power_draw", "average_power_draw"),
            PowerAverageW = Num(power, "average_power_draw"),
            PowerLimitW = FirstNum(power, "current_power_limit", "power_limit"),
            PowerRequestedW = Num(power, "requested_power_limit"),
            PowerDefaultW = Num(power, "default_power_limit"),
            PowerMinW = Num(power, "min_power_limit"),
            PowerMaxW = Num(power, "max_power_limit"),
            PowerManagement = Clean(FirstText(power, "power_management")),
            GraphicsClockMHz = Num(clocks, "graphics_clock"),
            SmClockMHz = Num(clocks, "sm_clock"),
            MemoryClockMHz = Num(clocks, "mem_clock"),
            VideoClockMHz = Num(clocks, "video_clock"),
            MaxGraphicsClockMHz = Num(maxClocks, "graphics_clock"),
            MaxSmClockMHz = Num(maxClocks, "sm_clock"),
            MaxMemoryClockMHz = Num(maxClocks, "mem_clock"),
            MaxVideoClockMHz = Num(maxClocks, "video_clock"),
            AppGraphicsClockMHz = Num(appClocks, "graphics_clock"),
            AppMemoryClockMHz = Num(appClocks, "mem_clock"),
            BoostClockMHz = Num(boost, "graphics_clock"),
            GraphicsVoltageMv = FirstNum(voltage, "graphics_volt", "graphics_voltage"),
            PcieGenCurrent = Clean(FirstText(gen, "current_link_gen", "device_current_link_gen")),
            PcieGenMax = Clean(FirstText(gen, "max_link_gen", "max_device_link_gen")),
            PcieWidthCurrent = Clean(Text(widths, "current_link_width")),
            PcieWidthMax = Clean(Text(widths, "max_link_width")),
            PcieTx = Clean(Text(pci, "tx_util")),
            PcieRx = Clean(Text(pci, "rx_util")),
            EccSummary = EccLine(gpu),
            RetiredPagesSummary = SummarizeLeaves(Child(gpu, "retired_pages"), 4),
            RemappedRowsSummary = SummarizeLeaves(Child(gpu, "remapped_rows"), 4),
            EncoderStatsSummary = StatsLine(Child(gpu, "encoder_stats")),
            FbcStatsSummary = FbcLine(Child(gpu, "fbc_stats")),
            Throttles = ParseThrottles(Child(gpu, "clocks_throttle_reasons")),
            Processes = ParseProcesses(Child(gpu, "processes"))
        };
    }

    private static List<ThrottleReason> ParseThrottles(XElement? section)
    {
        var list = new List<ThrottleReason>();
        if (section == null) return list;
        foreach (var element in section.Elements())
        {
            var key = element.Name.LocalName;
            var (label, warning) = DescribeThrottle(key);
            list.Add(new ThrottleReason
            {
                Key = key,
                Label = label,
                Active = element.Value.Trim().Equals("Active", StringComparison.OrdinalIgnoreCase),
                Warning = warning
            });
        }
        return list;
    }

    private static (string Label, bool Warning) DescribeThrottle(string key)
    {
        var name = key.Replace("clocks_throttle_reason_", "", StringComparison.OrdinalIgnoreCase);
        return name.ToLowerInvariant() switch
        {
            "gpu_idle" => ("Idle", false),
            "applications_clocks_setting" => ("App clocks", false),
            "sw_power_cap" => ("Power cap", true),
            "hw_slowdown" => ("HW slowdown", true),
            "hw_thermal_slowdown" => ("Thermal", true),
            "hw_power_brake_slowdown" => ("Power brake", true),
            "sw_thermal_slowdown" => ("SW thermal", true),
            "sync_boost" => ("Sync boost", false),
            "display_clocks_setting" => ("Display clocks", false),
            _ => (Formatting.Humanize(name), name.Contains("slow", StringComparison.OrdinalIgnoreCase) || name.Contains("thermal", StringComparison.OrdinalIgnoreCase))
        };
    }

    private static List<GpuProcess> ParseProcesses(XElement? section)
    {
        var list = new List<GpuProcess>();
        if (section == null) return list;
        foreach (var process in section.Elements().Where(e => e.Name.LocalName.Equals("process_info", StringComparison.OrdinalIgnoreCase)))
        {
            var pid = Text(process, "pid");
            if (Formatting.IsBlank(pid)) continue;
            var rawName = Text(process, "process_name");
            var name = rawName;
            try
            {
                var file = Path.GetFileName(rawName.Replace('/', '\\'));
                if (!string.IsNullOrWhiteSpace(file)) name = file;
            }
            catch (ArgumentException)
            {
                name = rawName;
            }

            var memory = Text(process, "used_memory");
            list.Add(new GpuProcess
            {
                Pid = pid,
                Name = string.IsNullOrWhiteSpace(name) ? "process" : name,
                Type = FriendlyType(Text(process, "type")),
                MemoryText = Formatting.IsBlank(memory) ? "" : memory,
                MemoryMiB = ParseNumber(memory) ?? 0
            });
        }

        return list.OrderByDescending(p => p.MemoryMiB).ToList();
    }

    private static string FriendlyType(string type) => type.Trim().ToUpperInvariant() switch
    {
        "C" => "Compute",
        "G" => "Graphics",
        "C+G" => "Compute + Graphics",
        "" => "",
        _ => type.Trim()
    };

    private static (double? Percent, string Text) ParseFan(XElement? fan)
    {
        if (fan == null) return (null, "");
        if (!fan.HasElements)
        {
            var single = ParseNumber(fan.Value);
            return (single, single == null ? "" : $"{single.Value:0}%");
        }

        var values = fan.Elements().Select(e => ParseNumber(e.Value)).Where(v => v.HasValue).Select(v => v!.Value).ToList();
        if (values.Count == 0) return (null, "");
        var text = string.Join(" / ", values.Select(v => $"{v:0}%"));
        return (values.Average(), text);
    }

    private static string EccLine(XElement gpu)
    {
        var mode = FirstText(Child(gpu, "ecc_mode"), "current_ecc", "current");
        if (Formatting.IsBlank(mode)) return "";
        var errors = Child(gpu, "ecc_errors");
        if (errors == null) return mode;
        var totals = errors.Descendants()
            .Where(e => !e.HasElements && e.Name.LocalName.Contains("total", StringComparison.OrdinalIgnoreCase))
            .Select(e => $"{Formatting.Humanize(e.Name.LocalName)} {e.Value.Trim()}")
            .Where(part => !Formatting.IsBlank(part))
            .Take(4)
            .ToList();
        return totals.Count == 0 ? mode : mode + " · " + string.Join(" · ", totals);
    }

    private static string FbcLine(XElement? section)
    {
        var sessions = ParseNumber(Text(section, "session_count"));
        if (sessions is null or <= 0) return "";
        return StatsLine(section);
    }

    private static string StatsLine(XElement? section)
    {
        if (section == null) return "";
        var bits = new List<string>();
        var sessions = Text(section, "session_count");
        var fps = Text(section, "average_fps");
        var latency = Text(section, "average_latency");
        if (!Formatting.IsBlank(sessions)) bits.Add($"sessions {sessions}");
        if (!Formatting.IsBlank(fps)) bits.Add($"avg fps {fps}");
        if (!Formatting.IsBlank(latency)) bits.Add($"avg latency {latency}");
        return string.Join(" · ", bits);
    }

    private static string SummarizeLeaves(XElement? section, int maxParts)
    {
        if (section == null) return "";
        var parts = section.Descendants()
            .Where(e => !e.HasElements)
            .Select(e => (Name: e.Name.LocalName, Value: e.Value.Trim()))
            .Where(pair => !Formatting.IsBlank(pair.Value))
            .Take(maxParts)
            .Select(pair => $"{Formatting.Humanize(pair.Name)} {pair.Value}")
            .ToList();
        return string.Join(" · ", parts);
    }

    private static string? ExtractXml(string text)
    {
        var start = text.IndexOf("<nvidia_smi_log", StringComparison.OrdinalIgnoreCase);
        if (start < 0) return null;
        const string endTag = "</nvidia_smi_log>";
        var end = text.IndexOf(endTag, start, StringComparison.OrdinalIgnoreCase);
        if (end < 0) return null;
        return text[start..(end + endTag.Length)];
    }

    private static bool NeedsAdmin(Captured run)
    {
        if (!run.Started || run.Success) return false;
        var text = run.Output;
        return run.ExitCode == 5 ||
               text.Contains("Insufficient Permissions", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("does not have permission", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("administrator", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("Access is denied", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("requires elevated", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeFailure(string text) =>
        text.Contains("error", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("not supported", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("insufficient", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("invalid", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("not a valid", StringComparison.OrdinalIgnoreCase);

    private static XElement? Child(XElement? parent, string name) =>
        parent?.Elements().FirstOrDefault(e => e.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static XElement? Find(XElement parent, params string[] names)
    {
        foreach (var name in names)
        {
            var direct = Child(parent, name);
            if (direct != null) return direct;
        }

        return parent.Descendants().FirstOrDefault(e =>
            names.Any(name => e.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase)));
    }

    private static string Text(XElement? parent, string name)
    {
        var element = Child(parent, name);
        return element == null ? "" : element.Value.Replace("\r", " ").Replace("\n", " ").Trim();
    }

    private static string FirstText(XElement? parent, params string[] names)
    {
        if (parent == null) return "";
        foreach (var name in names)
        {
            var value = Text(parent, name);
            if (!Formatting.IsBlank(value)) return value;
        }
        return "";
    }

    private static double? Num(XElement? parent, string name) => ParseNumber(Text(parent, name));

    private static double? FirstNum(XElement? parent, params string[] names)
    {
        if (parent == null) return null;
        foreach (var name in names)
        {
            var value = Num(parent, name);
            if (value.HasValue) return value;
        }
        return null;
    }

    private static double? ParseNumber(string? raw)
    {
        if (Formatting.IsBlank(raw)) return null;
        var match = NumberRegex().Match(raw);
        if (!match.Success) return null;
        return double.TryParse(match.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    private static string Clean(string value) => Formatting.IsBlank(value) ? "" : value.Replace('_', ' ').Trim();

    private static string BlankTo(string value, string fallback) => Formatting.IsBlank(value) ? fallback : value.Trim();

    private static string FirstNonBlank(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!Formatting.IsBlank(value)) return value!.Trim();
        }
        return "";
    }

    [GeneratedRegex(@"-?\d+(?:\.\d+)?", RegexOptions.CultureInvariant)]
    private static partial Regex NumberRegex();

    private readonly record struct Captured(bool Started, bool Success, int ExitCode, string Output)
    {
        public static Captured Fail(string message) => new(false, false, -1, message);
    }
}
