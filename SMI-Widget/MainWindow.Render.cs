using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using SMI_Widget.Models;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;

namespace SMI_Widget;

public partial class MainWindow
{
    private readonly Dictionary<int, List<Sample>> _history = [];

    private readonly record struct Sample(double Util, double Temp, double Power);

    private void Render(GpuSnapshot gpu)
    {
        _currentGpu = gpu;
        var title = gpu.ShortName;
        Title = "SMI Widget — " + title;
        GpuNameText.Text = title;
        GpuNameText.ToolTip = string.IsNullOrWhiteSpace(gpu.Uuid) ? gpu.Name : gpu.Name + "\n" + gpu.Uuid;
        SubtitleText.Text = JoinBits(gpu.Architecture, gpu.DriverModel, string.IsNullOrWhiteSpace(gpu.CudaVersion) ? "" : "CUDA " + gpu.CudaVersion);
        _tray?.SetText($"{title}  {Formatting.Temp(gpu.GpuTempC)}  {Formatting.Watts(gpu.PowerDrawW)}");

        GpuUtilText.Text = Formatting.Percent(gpu.GpuUtil);
        SetBar(GpuUtilBar, GpuUtilTrack, gpu.GpuUtil, Color.FromRgb(118, 185, 0));

        TempText.Text = Formatting.Temp(gpu.GpuTempC);
        TempText.Foreground = new SolidColorBrush(TempColor(gpu.GpuTempC, gpu.SlowTempC));
        var tempScale = gpu.SlowTempC ?? gpu.MaxTempC ?? 100;
        var tempPct = gpu.GpuTempC == null || tempScale <= 0 ? (double?)null : gpu.GpuTempC.Value / tempScale * 100;
        SetBar(TempBar, TempTrack, tempPct, TempColor(gpu.GpuTempC, gpu.SlowTempC));
        TempHint.Text = gpu.SlowTempC is double slow ? $"slow {slow:0}°" : gpu.TargetTempC is double target ? $"target {target:0}°" : "";

        PowerText.Text = Formatting.Watts(gpu.PowerDrawW);
        var powerScale = gpu.PowerLimitW ?? gpu.PowerMaxW ?? 0;
        var powerPct = gpu.PowerDrawW == null || powerScale <= 0 ? (double?)null : gpu.PowerDrawW.Value / powerScale * 100;
        var powerColor = RatioColor(powerPct);
        PowerText.Foreground = new SolidColorBrush(powerColor);
        SetBar(PowerBar, PowerTrack, powerPct, powerColor);
        PowerHint.Text = gpu.PowerLimitW is double limit ? $"of {limit:0} W" : "";

        var memPct = gpu.MemoryUsedMiB is double used && gpu.MemoryTotalMiB is double total && total > 0
            ? used / total * 100
            : (double?)null;
        MemText.Text = gpu.MemoryTotalMiB == null
            ? "—"
            : $"{Formatting.Gib(gpu.MemoryUsedMiB)} / {Formatting.Gib(gpu.MemoryTotalMiB)} GB";
        MemHint.Text = memPct == null ? "" : $"{memPct.Value:0}%";
        SetBar(MemBar, MemTrack, memPct, memPct is >= 92 ? Color.FromRgb(255, 92, 77) : memPct is >= 80 ? Color.FromRgb(255, 176, 48) : Color.FromRgb(122, 215, 255));

        GfxClockText.Text = Formatting.Mhz(gpu.GraphicsClockMHz);
        SmClockText.Text = Formatting.Mhz(gpu.SmClockMHz);
        MemClockText.Text = Formatting.Mhz(gpu.MemoryClockMHz);
        VidClockText.Text = Formatting.Mhz(gpu.VideoClockMHz);
        GfxClockHint.Text = HintMax(gpu.MaxGraphicsClockMHz);
        SmClockHint.Text = HintMax(gpu.MaxSmClockMHz);
        MemClockHint.Text = HintMax(gpu.MaxMemoryClockMHz);
        VidClockHint.Text = HintMax(gpu.MaxVideoClockMHz);

        PStateText.Text = string.IsNullOrWhiteSpace(gpu.PerformanceState) ? "P-state —" : gpu.PerformanceState;
        var activeState = gpu.PerformanceState is "P0" or "P1" or "P2";
        PStateText.Foreground = activeState ? new SolidColorBrush(Color.FromRgb(182, 227, 74)) : new SolidColorBrush(Color.FromRgb(231, 237, 246));

        var fanStopped = gpu.FanPercent is 0 && gpu.GpuTempC is < 55;
        FanText.Text = fanStopped ? "Fan stop" : string.IsNullOrWhiteSpace(gpu.FanText) ? "Fan —" : "Fan " + gpu.FanText;
        FanText.Foreground = gpu.FanPercent is 0 && gpu.GpuTempC is >= 70
            ? new SolidColorBrush(Color.FromRgb(255, 120, 100))
            : new SolidColorBrush(Color.FromRgb(231, 237, 246));

        var pcie = FormatPcie(gpu);
        PcieText.Text = pcie.Text;
        PcieText.Foreground = pcie.Downshifted
            ? new SolidColorBrush(Color.FromRgb(255, 193, 77))
            : new SolidColorBrush(Color.FromRgb(231, 237, 246));
        PcieChip.ToolTip = JoinBits(
            Pair("Gen", gpu.PcieGenCurrent, gpu.PcieGenMax),
            Pair("Width", gpu.PcieWidthCurrent, gpu.PcieWidthMax),
            string.IsNullOrWhiteSpace(gpu.PcieTx) ? "" : "TX " + gpu.PcieTx,
            string.IsNullOrWhiteSpace(gpu.PcieRx) ? "" : "RX " + gpu.PcieRx);

        DriverText.Text = string.IsNullOrWhiteSpace(gpu.DriverVersion) ? "Driver —" : "Driver " + gpu.DriverVersion;
        RenderStatus(gpu);
        RememberSample(gpu);
        DrawHistory(gpu);
        RenderDetails(gpu);
        SyncControls(gpu);
    }

    private void RenderStatus(GpuSnapshot gpu)
    {
        var warning = gpu.Throttles.FirstOrDefault(t => t.Active && t.Warning);
        if (warning != null)
        {
            var hot = warning.Label.Contains("Thermal", StringComparison.OrdinalIgnoreCase) ||
                      warning.Label.Contains("slowdown", StringComparison.OrdinalIgnoreCase);
            StatusChipText.Text = warning.Label;
            StatusChip.Background = new SolidColorBrush(hot ? Color.FromRgb(120, 42, 36) : Color.FromRgb(92, 64, 24));
            StatusChipText.Foreground = new SolidColorBrush(hot ? Color.FromRgb(255, 180, 170) : Color.FromRgb(255, 214, 150));
            return;
        }

        var idle = gpu.Throttles.Any(t => t.Active && t.Key.Contains("idle", StringComparison.OrdinalIgnoreCase));
        if (idle && (gpu.GpuUtil ?? 0) < 15)
        {
            StatusChipText.Text = "Idle";
            StatusChip.Background = ChipBrush();
            StatusChipText.Foreground = MutedBrush();
            return;
        }

        StatusChipText.Text = gpu.Throttles.Count == 0 ? "Live" : "Nominal";
        StatusChip.Background = new SolidColorBrush(Color.FromRgb(32, 58, 24));
        StatusChipText.Foreground = new SolidColorBrush(Color.FromRgb(198, 230, 140));
    }

    private void RememberSample(GpuSnapshot gpu)
    {
        if (!_history.TryGetValue(gpu.Index, out var series))
        {
            series = [];
            _history[gpu.Index] = series;
        }

        series.Add(new Sample(gpu.GpuUtil ?? 0, gpu.GpuTempC ?? 0, gpu.PowerDrawW ?? 0));
        if (series.Count > 60) series.RemoveAt(0);
    }

    private void DrawHistory(GpuSnapshot gpu)
    {
        HistoryCanvas.Children.Clear();
        if (!_history.TryGetValue(gpu.Index, out var series)) return;
        var width = HistoryCanvas.ActualWidth;
        if (width < 8 && HistoryCanvas.Parent is FrameworkElement parent)
            width = parent.ActualWidth;
        var height = HistoryCanvas.ActualHeight > 8 ? HistoryCanvas.ActualHeight : 42;
        if (width < 8 || series.Count < 2) return;

        HistoryCanvas.Children.Add(new Line
        {
            X1 = 0,
            X2 = width,
            Y1 = height - 1,
            Y2 = height - 1,
            Stroke = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)),
            StrokeThickness = 1
        });

        AddSeries(series, width, height, sample => sample.Util / 100.0, Color.FromRgb(118, 185, 0));
        var tempTop = gpu.SlowTempC ?? 95;
        if (tempTop < 50) tempTop = 95;
        AddSeries(series, width, height, sample => Math.Clamp((sample.Temp - 25) / (tempTop - 25), 0, 1), Color.FromRgb(255, 193, 77));
        var powerTop = gpu.PowerMaxW ?? gpu.PowerLimitW ?? 350;
        if (powerTop < 10) powerTop = 350;
        AddSeries(series, width, height, sample => Math.Clamp(sample.Power / powerTop, 0, 1), Color.FromRgb(122, 215, 255));
    }

    private void AddSeries(IReadOnlyList<Sample> series, double width, double height, Func<Sample, double> selector, Color color)
    {
        var line = new Polyline
        {
            Stroke = new SolidColorBrush(color),
            StrokeThickness = 1.6,
            StrokeLineJoin = PenLineJoin.Round
        };
        var last = series.Count - 1;
        for (var i = 0; i < series.Count; i++)
        {
            var x = width * i / last;
            var y = height - 2 - selector(series[i]) * (height - 6);
            line.Points.Add(new Point(x, y));
        }
        HistoryCanvas.Children.Add(line);
    }

    private void RenderDetails(GpuSnapshot gpu)
    {
        DetailList.Children.Clear();
        foreach (var (label, value) in DetailRows(gpu))
        {
            var row = DetailRow(label, value);
            if (row != null) DetailList.Children.Add(row);
        }

        ThrottleList.Children.Clear();
        ThrottleHeader.Visibility = gpu.Throttles.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        foreach (var reason in gpu.Throttles)
            ThrottleList.Children.Add(ThrottleChip(reason));

        ProcessList.Children.Clear();
        if (gpu.Processes.Count == 0)
        {
            ProcessHeader.Visibility = Visibility.Visible;
            ProcessList.Children.Add(new TextBlock
            {
                Text = "No compute or graphics processes",
                Foreground = MutedBrush(),
                FontSize = 11
            });
        }
        else
        {
            ProcessHeader.Visibility = Visibility.Visible;
            foreach (var process in gpu.Processes.Take(8))
                ProcessList.Children.Add(ProcessRow(process));
            if (gpu.Processes.Count > 8)
            {
                ProcessList.Children.Add(new TextBlock
                {
                    Text = $"+ {gpu.Processes.Count - 8} more",
                    Foreground = MutedBrush(),
                    FontSize = 11,
                    Margin = new Thickness(0, 4, 0, 0)
                });
            }
        }
    }

    private static IEnumerable<(string Label, string Value)> DetailRows(GpuSnapshot gpu)
    {
        yield return ("Brand", gpu.Brand);
        yield return ("Bus", gpu.BusId);
        yield return ("UUID", gpu.Uuid);
        yield return ("Serial", gpu.Serial);
        yield return ("Part", gpu.PartNumber);
        yield return ("VBIOS", gpu.Vbios);
        yield return ("GSP", gpu.GspFirmware);
        yield return ("Display", JoinBits(gpu.DisplayMode, gpu.DisplayActive == "" ? "" : "active " + gpu.DisplayActive));
        yield return ("Persistence", gpu.PersistenceMode);
        yield return ("Compute", gpu.ComputeMode);
        yield return ("Accounting", gpu.AccountingMode);
        yield return ("MIG", gpu.MigMode);
        yield return ("GPU temp", JoinBits(Formatting.Temp(gpu.GpuTempC), gpu.TempMarginC is double margin ? $"margin {margin:0}°" : ""));
        yield return ("Memory temp", Formatting.Temp(gpu.MemoryTempC));
        yield return ("Target temp", Formatting.Temp(gpu.TargetTempC));
        yield return ("Slow / max", JoinBits(Formatting.Temp(gpu.SlowTempC), Formatting.Temp(gpu.MaxTempC)));
        yield return ("Power draw", Formatting.Watts(gpu.PowerDrawW));
        yield return ("Power avg", Formatting.Watts(gpu.PowerAverageW));
        yield return ("Power limit", Formatting.Watts(gpu.PowerLimitW));
        yield return ("Requested", Formatting.Watts(gpu.PowerRequestedW));
        yield return ("Default limit", Formatting.Watts(gpu.PowerDefaultW));
        yield return ("Limit range", gpu.CanSetPower ? $"{gpu.PowerMinW:0}–{gpu.PowerMaxW:0} W" : "");
        yield return ("Power mgmt", gpu.PowerManagement);
        yield return ("Voltage", gpu.GraphicsVoltageMv is double mv ? $"{mv:0} mV" : "");
        yield return ("Graphics", ClockPair(gpu.GraphicsClockMHz, gpu.MaxGraphicsClockMHz));
        yield return ("SM", ClockPair(gpu.SmClockMHz, gpu.MaxSmClockMHz));
        yield return ("Memory clock", ClockPair(gpu.MemoryClockMHz, gpu.MaxMemoryClockMHz));
        yield return ("Video", ClockPair(gpu.VideoClockMHz, gpu.MaxVideoClockMHz));
        yield return ("App clocks", gpu.ApplicationsClocksSupported ? $"{Formatting.Mhz(gpu.AppMemoryClockMHz)} / {Formatting.Mhz(gpu.AppGraphicsClockMHz)} MHz" : "");
        yield return ("Boost clock", gpu.BoostClockMHz is double boost ? $"{boost:0} MHz" : "");
        yield return ("FB memory", MemoryLine(gpu.MemoryUsedMiB, gpu.MemoryTotalMiB));
        yield return ("Reserved", gpu.MemoryReservedMiB is double reserved ? $"{reserved:0} MiB" : "");
        yield return ("Free", gpu.MemoryFreeMiB is double free ? $"{Formatting.Gib(free)} GB" : "");
        yield return ("BAR1", MemoryLine(gpu.Bar1UsedMiB, gpu.Bar1TotalMiB));
        yield return ("Mem controller", Formatting.Percent(gpu.MemoryUtil));
        yield return ("Encoder", Formatting.Percent(gpu.EncoderUtil));
        yield return ("Decoder", Formatting.Percent(gpu.DecoderUtil));
        yield return ("JPEG", Formatting.Percent(gpu.JpegUtil));
        yield return ("OFA", Formatting.Percent(gpu.OfaUtil));
        yield return ("Encoder stats", gpu.EncoderStatsSummary);
        yield return ("Capture", gpu.FbcStatsSummary);
        yield return ("PCIe", JoinBits(Pair("gen", gpu.PcieGenCurrent, gpu.PcieGenMax), Pair("width", gpu.PcieWidthCurrent, gpu.PcieWidthMax)));
        yield return ("PCIe traffic", JoinBits(string.IsNullOrWhiteSpace(gpu.PcieTx) ? "" : "TX " + gpu.PcieTx, string.IsNullOrWhiteSpace(gpu.PcieRx) ? "" : "RX " + gpu.PcieRx));
        yield return ("ECC", gpu.EccSummary);
        yield return ("Retired pages", gpu.RetiredPagesSummary);
        yield return ("Remapped rows", gpu.RemappedRowsSummary);
    }

    private void SyncControls(GpuSnapshot gpu)
    {
        _syncingControls = true;
        PowerUnsupported.Visibility = gpu.CanSetPower ? Visibility.Collapsed : Visibility.Visible;
        PowerSlider.Visibility = gpu.CanSetPower ? Visibility.Visible : Visibility.Collapsed;
        ApplyPowerButton.Visibility = gpu.CanSetPower ? Visibility.Visible : Visibility.Collapsed;
        ResetPowerButton.Visibility = gpu.PowerDefaultW == null ? Visibility.Collapsed : Visibility.Visible;
        if (gpu.CanSetPower)
        {
            PowerSlider.Minimum = gpu.PowerMinW!.Value;
            PowerSlider.Maximum = gpu.PowerMaxW!.Value;
            if (!_powerDirty && gpu.PowerLimitW is double limit)
                PowerSlider.Value = Math.Clamp(limit, PowerSlider.Minimum, PowerSlider.Maximum);
            PowerRangeText.Text = $"Allowed {gpu.PowerMinW:0}–{gpu.PowerMaxW:0} W · default {Formatting.Watts(gpu.PowerDefaultW)} · now {Formatting.Watts(gpu.PowerLimitW)}";
        }
        else
        {
            PowerRangeText.Text = gpu.PowerLimitW == null ? "" : $"Current limit {Formatting.Watts(gpu.PowerLimitW)}";
        }
        PowerSliderValue.Text = $"{PowerSlider.Value.ToString("0", System.Globalization.CultureInfo.InvariantCulture)} W";

        GpuClockRangeText.Text = gpu.MaxGraphicsClockMHz is double maxGfx
            ? $"Reported max {maxGfx:0} MHz · now {Formatting.Mhz(gpu.GraphicsClockMHz)}"
            : "MHz";
        MemClockRangeText.Text = gpu.MaxMemoryClockMHz is double maxMem
            ? $"Reported max {maxMem:0} MHz · now {Formatting.Mhz(gpu.MemoryClockMHz)}"
            : "MHz";
        FillIfEmpty(GpuClockMinBox, gpu.GraphicsClockMHz);
        FillIfEmpty(GpuClockMaxBox, gpu.MaxGraphicsClockMHz ?? gpu.GraphicsClockMHz);
        FillIfEmpty(MemClockMinBox, gpu.MemoryClockMHz);
        FillIfEmpty(MemClockMaxBox, gpu.MaxMemoryClockMHz ?? gpu.MemoryClockMHz);

        AppClocksPanel.Visibility = gpu.ApplicationsClocksSupported ? Visibility.Visible : Visibility.Collapsed;
        FillIfEmpty(AppMemBox, gpu.AppMemoryClockMHz);
        FillIfEmpty(AppGfxBox, gpu.AppGraphicsClockMHz);

        TargetTempPanel.Visibility = gpu.CanSetTargetTemp ? Visibility.Visible : Visibility.Collapsed;
        if (gpu.CanSetTargetTemp)
        {
            TargetTempSlider.Minimum = gpu.TargetTempMinC!.Value;
            TargetTempSlider.Maximum = gpu.TargetTempMaxC!.Value;
            if (!_tempDirty && gpu.TargetTempC is double current)
                TargetTempSlider.Value = Math.Clamp(current, TargetTempSlider.Minimum, TargetTempSlider.Maximum);
            TargetTempRangeText.Text = $"Allowed {gpu.TargetTempMinC:0}–{gpu.TargetTempMaxC:0}° · now {Formatting.Temp(gpu.TargetTempC)}";
            TargetTempValue.Text = $"{TargetTempSlider.Value:0}°";
        }

        PersistencePanel.Visibility = gpu.PersistenceSupported ? Visibility.Visible : Visibility.Collapsed;
        PersistenceStateText.Text = string.IsNullOrWhiteSpace(gpu.PersistenceMode) ? "" : "Now " + gpu.PersistenceMode;
        ComputePanel.Visibility = gpu.ComputeSupported ? Visibility.Visible : Visibility.Collapsed;
        ComputeStateText.Text = string.IsNullOrWhiteSpace(gpu.ComputeMode) ? "" : "Now " + gpu.ComputeMode;
        _syncingControls = false;
    }

    private static void FillIfEmpty(TextBox box, double? value)
    {
        if (box.IsKeyboardFocusWithin || !string.IsNullOrWhiteSpace(box.Text) || value == null) return;
        box.Text = Math.Round(value.Value).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static UIElement? DetailRow(string label, string value)
    {
        if (Formatting.IsBlank(value) || value == "—") return null;
        var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(108) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var caption = new TextBlock { Text = label, Foreground = MutedBrush(), FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        var text = new TextBlock
        {
            Text = value,
            Foreground = TextBrush(),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(text, 1);
        grid.Children.Add(caption);
        grid.Children.Add(text);
        return grid;
    }

    private static Border ThrottleChip(ThrottleReason reason)
    {
        Color background;
        Color foreground;
        if (!reason.Active)
        {
            background = Color.FromRgb(28, 33, 44);
            foreground = Color.FromRgb(140, 148, 164);
        }
        else if (reason.Warning)
        {
            background = Color.FromRgb(92, 48, 28);
            foreground = Color.FromRgb(255, 206, 140);
        }
        else
        {
            background = Color.FromRgb(32, 58, 24);
            foreground = Color.FromRgb(198, 230, 140);
        }

        return new Border
        {
            Background = new SolidColorBrush(background),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(8, 3, 8, 3),
            Margin = new Thickness(0, 0, 6, 6),
            Child = new TextBlock { Text = reason.Label, Foreground = new SolidColorBrush(foreground), FontSize = 11 }
        };
    }

    private static UIElement ProcessRow(GpuProcess process)
    {
        var grid = new Grid { Margin = new Thickness(0, 2, 0, 2), ToolTip = $"PID {process.Pid}" };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var name = new TextBlock
        {
            Text = process.Name,
            Foreground = TextBrush(),
            FontSize = 11,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var meta = new TextBlock
        {
            Text = JoinBits(process.Type, process.MemoryText),
            Foreground = MutedBrush(),
            FontSize = 11,
            Margin = new Thickness(8, 0, 0, 0)
        };
        Grid.SetColumn(meta, 1);
        grid.Children.Add(name);
        grid.Children.Add(meta);
        return grid;
    }

    private static void SetBar(FrameworkElement bar, FrameworkElement track, double? percent, Color color)
    {
        if (bar is Border border)
            border.Background = new SolidColorBrush(color);
        var pct = Math.Clamp(percent ?? 0, 0, 100);
        var width = Math.Max(0, track.ActualWidth * (pct / 100.0));
        bar.BeginAnimation(FrameworkElement.WidthProperty, new DoubleAnimation(width, TimeSpan.FromMilliseconds(220))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        });
    }

    private static Color TempColor(double? temp, double? slow)
    {
        if (temp == null) return Color.FromRgb(118, 185, 0);
        var limit = slow ?? 95;
        if (temp.Value >= limit - 3) return Color.FromRgb(255, 92, 77);
        if (temp.Value >= 80 || temp.Value >= limit - 10) return Color.FromRgb(255, 176, 48);
        if (temp.Value >= 65) return Color.FromRgb(196, 214, 64);
        return Color.FromRgb(118, 185, 0);
    }

    private static Color RatioColor(double? percent)
    {
        if (percent is >= 92) return Color.FromRgb(255, 92, 77);
        if (percent is >= 75) return Color.FromRgb(255, 176, 48);
        return Color.FromRgb(122, 215, 255);
    }

    private static (string Text, bool Downshifted) FormatPcie(GpuSnapshot gpu)
    {
        if (string.IsNullOrWhiteSpace(gpu.PcieGenCurrent) && string.IsNullOrWhiteSpace(gpu.PcieWidthCurrent))
            return ("PCIe —", false);
        var gen = string.IsNullOrWhiteSpace(gpu.PcieGenCurrent) ? "" : "Gen " + gpu.PcieGenCurrent;
        var width = gpu.PcieWidthCurrent;
        var text = JoinBits(gen, width);
        var down = (!string.IsNullOrWhiteSpace(gpu.PcieGenMax) && gpu.PcieGenCurrent != gpu.PcieGenMax) ||
                   (!string.IsNullOrWhiteSpace(gpu.PcieWidthMax) && gpu.PcieWidthCurrent != gpu.PcieWidthMax);
        if (down)
            text += $" (max {JoinBits(string.IsNullOrWhiteSpace(gpu.PcieGenMax) ? "" : "Gen " + gpu.PcieGenMax, gpu.PcieWidthMax)})";
        return ("PCIe " + text, down);
    }

    private static string HintMax(double? max) => max == null ? "" : $"max {max.Value:0}";

    private static string ClockPair(double? current, double? max)
    {
        if (current == null && max == null) return "";
        return max == null ? $"{Formatting.Mhz(current)} MHz" : $"{Formatting.Mhz(current)} / {Formatting.Mhz(max)} MHz";
    }

    private static string MemoryLine(double? used, double? total)
    {
        if (used == null && total == null) return "";
        if (total == null) return $"{used:0} MiB";
        return $"{Formatting.Gib(used)} / {Formatting.Gib(total)} GB";
    }

    private static string Pair(string label, string current, string max)
    {
        if (string.IsNullOrWhiteSpace(current) && string.IsNullOrWhiteSpace(max)) return "";
        if (string.IsNullOrWhiteSpace(max) || current == max) return $"{label} {current}";
        return $"{label} {current}/{max}";
    }

    private static string JoinBits(params string?[] bits)
    {
        var values = bits.Where(bit => !Formatting.IsBlank(bit) && bit != "—").Select(bit => bit!.Trim()).ToList();
        return values.Count == 0 ? "" : string.Join(" · ", values);
    }

    private static int GraphicsCeiling(GpuSnapshot gpu) => Ceiling(gpu.MaxGraphicsClockMHz, gpu.GraphicsClockMHz, gpu.BoostClockMHz, 4500);
    private static int MemoryCeiling(GpuSnapshot gpu) => Ceiling(gpu.MaxMemoryClockMHz, gpu.MemoryClockMHz, null, 12000);

    private static int Ceiling(double? max, double? current, double? extra, int fallback)
    {
        var top = new[] { max, current, extra }.Where(v => v.HasValue).Select(v => v!.Value).DefaultIfEmpty(fallback).Max();
        return (int)Math.Ceiling(Math.Max(top, 1));
    }
}
