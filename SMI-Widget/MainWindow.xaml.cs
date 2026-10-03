using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using SMI_Widget.Models;
using SMI_Widget.Services;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;

namespace SMI_Widget;

public partial class MainWindow : Window
{
    private readonly NvidiaSmiClient _client = new();
    private readonly WidgetSettings _settings;
    private readonly DispatcherTimer _pollTimer;
    private readonly DispatcherTimer _saveTimer;
    private TrayIconHost? _tray;
    private SmiQueryResult? _lastResult;
    private GpuSnapshot? _currentGpu;
    private bool _polling;
    private bool _commandBusy;
    private bool _hasLiveData;
    private bool _loadingSettings = true;
    private bool _syncingControls;
    private bool _powerDirty;
    private bool _tempDirty;
    private bool _exiting;
    private bool _trayOk;
    private bool _docked;
    private int _switchCount = -1;

    public MainWindow()
    {
        InitializeComponent();
        _settings = SettingsStore.Load();
        _settings.RefreshMs = _settings.RefreshMs is 500 or 1000 or 2000 or 5000 ? _settings.RefreshMs : 1000;
        _settings.CardOpacity = Math.Clamp(_settings.CardOpacity, 0.78, 1);

        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(_settings.RefreshMs) };
        _pollTimer.Tick += async (_, _) => await PollAsync();
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            SaveSettings();
        };

        OpacitySlider.Value = _settings.CardOpacity;
        StartupToggle.IsChecked = _settings.StartWithWindows || SettingsStore.IsStartupEnabled();
        DetailsPanel.Visibility = _settings.DetailsOpen ? Visibility.Visible : Visibility.Collapsed;
        ControlsPanel.Visibility = _settings.ControlsOpen ? Visibility.Visible : Visibility.Collapsed;
        Topmost = _settings.AlwaysOnTop;
        ApplyChrome();
        RefreshChromeButtons();
        HighlightRefresh();
        UpdateScrollVisibility();
        _loadingSettings = false;

        if (_settings.StartWithWindows && !SettingsStore.IsStartupEnabled())
        {
            try { SettingsStore.SetStartup(true); }
            catch (Exception) { /* the toggle can retry later */ }
        }

        InitTray();
        ContentRendered += Window_ContentRendered;
        LocationChanged += (_, _) =>
        {
            if (!IsLoaded) return;
            _saveTimer.Stop();
            _saveTimer.Start();
        };
        Loaded += async (_, _) =>
        {
            FitScroll();
            await PollAsync();
            _pollTimer.Start();
        };
    }

    private void Window_ContentRendered(object? sender, EventArgs e)
    {
        if (!double.IsNaN(_settings.Left) && !double.IsNaN(_settings.Top))
        {
            Left = _settings.Left;
            Top = _settings.Top;
            EnsureOnScreen();
            return;
        }

        if (_docked) return;
        _docked = true;
        var area = SystemParameters.WorkArea;
        Left = area.Right - ActualWidth - 18;
        Top = Math.Max(area.Top + 12, area.Bottom - ActualHeight - 18);
    }

    private void InitTray()
    {
        try
        {
            _tray = new TrayIconHost();
            _tray.ShowRequested += () => Dispatcher.InvokeAsync(ShowFromTray);
            _tray.ExitRequested += () => Dispatcher.InvokeAsync(ExitApp);
            _trayOk = true;
        }
        catch (Exception)
        {
            _trayOk = false;
            ShowInTaskbar = true;
        }
    }

    private async Task PollAsync()
    {
        if (_polling) return;
        _polling = true;
        try
        {
            var result = await _client.QueryAsync(CancellationToken.None);
            _lastResult = result;
            if (!result.Success || result.Gpus.Count == 0)
            {
                ShowQueryError(string.IsNullOrWhiteSpace(result.Error) ? "nvidia-smi did not return a GPU." : result.Error);
                return;
            }

            ErrorBanner.Visibility = Visibility.Collapsed;
            LivePanel.Visibility = Visibility.Visible;
            RenderGpuSwitch(result.Gpus);
            var gpu = result.Gpus.FirstOrDefault(g => g.Index == _settings.SelectedGpuIndex) ?? result.Gpus[0];
            _settings.SelectedGpuIndex = gpu.Index;
            Render(gpu);
            _hasLiveData = true;
            UpdatedText.Foreground = new SolidColorBrush(Color.FromRgb(110, 120, 140));
            UpdatedText.Text = "Updated " + DateTime.Now.ToString("HH:mm:ss");
        }
        catch (Exception ex)
        {
            ShowQueryError(ex.Message);
        }
        finally
        {
            _polling = false;
        }
    }

    private void ShowQueryError(string message)
    {
        if (_hasLiveData)
        {
            UpdatedText.Text = message;
            UpdatedText.Foreground = new SolidColorBrush(Color.FromRgb(255, 160, 120));
            return;
        }

        ErrorText.Text = message;
        ErrorBanner.Visibility = Visibility.Visible;
        LivePanel.Visibility = Visibility.Collapsed;
        GpuNameText.Text = "SMI Widget";
        SubtitleText.Text = "Waiting for nvidia-smi";
    }

    private void RenderGpuSwitch(IReadOnlyList<GpuSnapshot> gpus)
    {
        if (gpus.Count < 2)
        {
            GpuSwitchPanel.Visibility = Visibility.Collapsed;
            return;
        }

        GpuSwitchPanel.Visibility = Visibility.Visible;
        if (_switchCount != gpus.Count)
        {
            GpuSwitchPanel.Children.Clear();
            foreach (var gpu in gpus)
            {
                var button = new Button
                {
                    Content = gpu.ShortName,
                    Tag = gpu.Index,
                    Style = (Style)FindResource("ChipButton"),
                    Margin = new Thickness(0, 0, 6, 6),
                    ToolTip = gpu.Name
                };
                button.Click += (_, _) =>
                {
                    _settings.SelectedGpuIndex = gpu.Index;
                    _powerDirty = false;
                    _tempDirty = false;
                    if (_lastResult != null)
                    {
                        var selected = _lastResult.Gpus.FirstOrDefault(g => g.Index == gpu.Index);
                        if (selected != null) Render(selected);
                    }
                    HighlightGpuSwitch();
                };
                GpuSwitchPanel.Children.Add(button);
            }
            _switchCount = gpus.Count;
        }

        HighlightGpuSwitch();
    }

    private void HighlightGpuSwitch()
    {
        foreach (var child in GpuSwitchPanel.Children)
        {
            if (child is not Button button) continue;
            var selected = button.Tag is int index && index == _settings.SelectedGpuIndex;
            button.Background = selected ? AccentBrush() : ChipBrush();
            button.Foreground = selected ? DarkBrush() : TextBrush();
        }
    }

    private async Task RunCommandAsync(Func<CancellationToken, Task<CommandResult>> action)
    {
        if (_commandBusy) return;
        _commandBusy = true;
        ControlsPanel.IsEnabled = false;
        CommandStatus.Foreground = MutedBrush();
        CommandStatus.Text = "Sending to nvidia-smi… approve the administrator prompt if it appears.";
        try
        {
            var result = await action(CancellationToken.None);
            CommandStatus.Text = result.Message;
            CommandStatus.Foreground = result.Success
                ? new SolidColorBrush(Color.FromRgb(182, 227, 74))
                : result.Cancelled
                    ? MutedBrush()
                    : new SolidColorBrush(Color.FromRgb(255, 160, 120));
            if (result.Success)
            {
                _powerDirty = false;
                _tempDirty = false;
                await PollAsync();
            }
        }
        catch (Exception ex)
        {
            CommandStatus.Text = ex.Message;
            CommandStatus.Foreground = new SolidColorBrush(Color.FromRgb(255, 160, 120));
        }
        finally
        {
            _commandBusy = false;
            ControlsPanel.IsEnabled = true;
        }
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleDetails();
            return;
        }

        try { DragMove(); }
        catch (InvalidOperationException) { /* mouse already released */ }
    }

    private void Pin_Click(object sender, RoutedEventArgs e)
    {
        _settings.AlwaysOnTop = !_settings.AlwaysOnTop;
        Topmost = _settings.AlwaysOnTop;
        RefreshChromeButtons();
        SaveSettings();
    }

    private void Tune_Click(object sender, RoutedEventArgs e)
    {
        _settings.ControlsOpen = !_settings.ControlsOpen;
        ControlsPanel.Visibility = _settings.ControlsOpen ? Visibility.Visible : Visibility.Collapsed;
        RefreshChromeButtons();
        UpdateScrollVisibility();
        NudgeOntoScreen();
        SaveSettings();
    }

    private void Details_Click(object sender, RoutedEventArgs e) => ToggleDetails();

    private void ToggleDetails()
    {
        _settings.DetailsOpen = !_settings.DetailsOpen;
        DetailsPanel.Visibility = _settings.DetailsOpen ? Visibility.Visible : Visibility.Collapsed;
        RefreshChromeButtons();
        UpdateScrollVisibility();
        NudgeOntoScreen();
        SaveSettings();
    }

    private void Hide_Click(object sender, RoutedEventArgs e)
    {
        if (_trayOk) HideToTray();
        else ExitApp();
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => ExitApp();

    private void HideToTray()
    {
        SaveSettings();
        Hide();
        _tray?.ShowBalloonOnce();
    }

    private void ShowFromTray()
    {
        Show();
        Activate();
        FitScroll();
        _ = PollAsync();
    }

    private void ExitApp()
    {
        _exiting = true;
        Close();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_exiting && _trayOk)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }

        SaveSettings();
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _pollTimer.Stop();
        _tray?.Dispose();
        base.OnClosed(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.F5)
        {
            _ = PollAsync();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            if (_trayOk) HideToTray();
            else ExitApp();
            e.Handled = true;
        }

        base.OnKeyDown(e);
    }

    private void PowerSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded) return;
        PowerSliderValue.Text = $"{PowerSlider.Value.ToString("0", CultureInfo.InvariantCulture)} W";
        if (!_syncingControls) _powerDirty = true;
    }

    private void TargetTempSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded) return;
        TargetTempValue.Text = $"{TargetTempSlider.Value.ToString("0", CultureInfo.InvariantCulture)}°";
        if (!_syncingControls) _tempDirty = true;
    }

    private void OpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded) return;
        _settings.CardOpacity = OpacitySlider.Value;
        ApplyChrome();
        if (_loadingSettings) return;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string tag) return;
        if (!int.TryParse(tag, out var ms)) return;
        _settings.RefreshMs = ms;
        _pollTimer.Interval = TimeSpan.FromMilliseconds(ms);
        HighlightRefresh();
        SaveSettings();
    }

    private void StartupToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        var enabled = StartupToggle.IsChecked == true;
        try
        {
            SettingsStore.SetStartup(enabled);
            _settings.StartWithWindows = enabled;
            CommandStatus.Text = enabled
                ? "The widget will start with Windows."
                : "Removed from Windows startup.";
            SaveSettings();
        }
        catch (Exception ex)
        {
            _loadingSettings = true;
            StartupToggle.IsChecked = !enabled;
            _loadingSettings = false;
            CommandStatus.Text = ex.Message;
        }
    }

    private void PowerPreset_Click(object sender, RoutedEventArgs e)
    {
        if (_currentGpu is not { CanSetPower: true } gpu || sender is not Button button) return;
        var tag = button.Tag as string ?? "";
        double target = tag switch
        {
            "default" => gpu.PowerDefaultW ?? gpu.PowerLimitW ?? gpu.PowerMaxW!.Value,
            "max" => gpu.PowerMaxW!.Value,
            _ when double.TryParse(tag, NumberStyles.Float, CultureInfo.InvariantCulture, out var scale) =>
                (gpu.PowerDefaultW ?? gpu.PowerMaxW!.Value) * scale,
            _ => PowerSlider.Value
        };
        _syncingControls = true;
        PowerSlider.Value = Math.Clamp(target, PowerSlider.Minimum, PowerSlider.Maximum);
        _syncingControls = false;
        _powerDirty = true;
        PowerSliderValue.Text = $"{PowerSlider.Value.ToString("0", CultureInfo.InvariantCulture)} W";
    }

    private void ApplyPower_Click(object sender, RoutedEventArgs e)
    {
        if (_currentGpu is not { CanSetPower: true } gpu) return;
        var watts = (int)Math.Round(PowerSlider.Value);
        _ = RunCommandAsync(ct => _client.SetPowerLimitAsync(gpu.Index, watts, ct));
    }

    private void ResetPower_Click(object sender, RoutedEventArgs e)
    {
        if (_currentGpu?.PowerDefaultW is not double fallback) return;
        var watts = (int)Math.Round(fallback);
        _ = RunCommandAsync(ct => _client.SetPowerLimitAsync(_currentGpu.Index, watts, ct));
    }

    private void LockGpuClocks_Click(object sender, RoutedEventArgs e) => LockClocks(graphics: true);
    private void LockMemClocks_Click(object sender, RoutedEventArgs e) => LockClocks(graphics: false);

    private void LockClocks(bool graphics)
    {
        if (_currentGpu == null) return;
        var minBox = graphics ? GpuClockMinBox : MemClockMinBox;
        var maxBox = graphics ? GpuClockMaxBox : MemClockMaxBox;
        if (!TryReadClock(minBox, out var min) || !TryReadClock(maxBox, out var max))
        {
            CommandStatus.Text = "Enter clock values in MHz.";
            return;
        }
        if (min > max)
        {
            CommandStatus.Text = "Minimum clock cannot be higher than maximum.";
            return;
        }

        var ceiling = graphics ? GraphicsCeiling(_currentGpu) : MemoryCeiling(_currentGpu);
        if (max > ceiling)
        {
            CommandStatus.Text = $"That is above the reported maximum of {ceiling} MHz.";
            return;
        }

        var gpu = _currentGpu;
        _ = RunCommandAsync(ct => graphics
            ? _client.LockGpuClocksAsync(gpu.Index, min, max, ct)
            : _client.LockMemoryClocksAsync(gpu.Index, min, max, ct));
    }

    private void UseCurrentGpuClock_Click(object sender, RoutedEventArgs e)
    {
        if (_currentGpu?.GraphicsClockMHz is not double clock) return;
        var text = Math.Round(clock).ToString(CultureInfo.InvariantCulture);
        GpuClockMinBox.Text = text;
        GpuClockMaxBox.Text = text;
    }

    private void UseCurrentMemClock_Click(object sender, RoutedEventArgs e)
    {
        if (_currentGpu?.MemoryClockMHz is not double clock) return;
        var text = Math.Round(clock).ToString(CultureInfo.InvariantCulture);
        MemClockMinBox.Text = text;
        MemClockMaxBox.Text = text;
    }

    private void UnlockGpuClocks_Click(object sender, RoutedEventArgs e)
    {
        if (_currentGpu == null) return;
        var gpu = _currentGpu;
        _ = RunCommandAsync(ct => _client.ResetGpuClocksAsync(gpu.Index, ct));
    }

    private void UnlockMemClocks_Click(object sender, RoutedEventArgs e)
    {
        if (_currentGpu == null) return;
        var gpu = _currentGpu;
        _ = RunCommandAsync(ct => _client.ResetMemoryClocksAsync(gpu.Index, ct));
    }

    private void ApplyAppClocks_Click(object sender, RoutedEventArgs e)
    {
        if (_currentGpu == null) return;
        if (!TryReadClock(AppMemBox, out var memory) || !TryReadClock(AppGfxBox, out var graphics))
        {
            CommandStatus.Text = "Enter applications clocks as memory MHz, then graphics MHz.";
            return;
        }

        var gpu = _currentGpu;
        _ = RunCommandAsync(ct => _client.SetApplicationsClocksAsync(gpu.Index, memory, graphics, ct));
    }

    private void ResetAppClocks_Click(object sender, RoutedEventArgs e)
    {
        if (_currentGpu == null) return;
        var gpu = _currentGpu;
        _ = RunCommandAsync(ct => _client.ResetApplicationsClocksAsync(gpu.Index, ct));
    }

    private void ApplyTargetTemp_Click(object sender, RoutedEventArgs e)
    {
        if (_currentGpu is not { CanSetTargetTemp: true } gpu) return;
        var temp = (int)Math.Round(TargetTempSlider.Value);
        _ = RunCommandAsync(ct => _client.SetTargetTemperatureAsync(gpu.Index, temp, ct));
    }

    private void PersistenceOn_Click(object sender, RoutedEventArgs e) => SetPersistence(true);
    private void PersistenceOff_Click(object sender, RoutedEventArgs e) => SetPersistence(false);

    private void SetPersistence(bool enabled)
    {
        if (_currentGpu == null) return;
        var gpu = _currentGpu;
        _ = RunCommandAsync(ct => _client.SetPersistenceModeAsync(gpu.Index, enabled, ct));
    }

    private void ComputeMode_Click(object sender, RoutedEventArgs e)
    {
        if (_currentGpu == null || sender is not Button { Tag: string tag } || !int.TryParse(tag, out var mode)) return;
        var gpu = _currentGpu;
        _ = RunCommandAsync(ct => _client.SetComputeModeAsync(gpu.Index, mode, ct));
    }

    private static bool TryReadClock(TextBox box, out int mhz)
    {
        var text = box.Text.Trim();
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out mhz) && mhz >= 0 && mhz <= 20000;
    }

    private void ApplyChrome()
    {
        var alpha = (byte)Math.Clamp(_settings.CardOpacity * 255, 190, 255);
        RootCard.Background = new SolidColorBrush(Color.FromArgb(alpha, 16, 20, 28));
        OpacityValue.Text = $"{Math.Round(_settings.CardOpacity * 100):0}%";
    }

    private void RefreshChromeButtons()
    {
        PinButton.Content = _settings.AlwaysOnTop ? "Pinned" : "Pin";
        PinButton.Background = _settings.AlwaysOnTop ? AccentBrush() : ChipBrush();
        PinButton.Foreground = _settings.AlwaysOnTop ? DarkBrush() : TextBrush();
        TuneButton.Background = _settings.ControlsOpen ? AccentBrush() : ChipBrush();
        TuneButton.Foreground = _settings.ControlsOpen ? DarkBrush() : TextBrush();
        DetailsToggle.Content = _settings.DetailsOpen ? "Hide details" : "Details";
    }

    private void HighlightRefresh()
    {
        foreach (var child in RefreshPanel.Children)
        {
            if (child is not Button button || button.Tag is not string tag) continue;
            var selected = int.TryParse(tag, out var ms) && ms == _settings.RefreshMs;
            button.Background = selected ? AccentBrush() : ChipBrush();
            button.Foreground = selected ? DarkBrush() : TextBrush();
        }
    }

    private void UpdateScrollVisibility()
    {
        var open = DetailsPanel.Visibility == Visibility.Visible || ControlsPanel.Visibility == Visibility.Visible;
        DetailsScroll.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
    }

    private void FitScroll()
    {
        var area = SystemParameters.WorkArea;
        DetailsScroll.MaxHeight = Math.Max(180, area.Height - 360);
    }

    private void NudgeOntoScreen()
    {
        Dispatcher.BeginInvoke(() =>
        {
            var area = SystemParameters.WorkArea;
            if (Top + ActualHeight > area.Bottom - 8)
                Top = Math.Max(area.Top + 8, area.Bottom - ActualHeight - 8);
        }, DispatcherPriority.Loaded);
    }

    private void EnsureOnScreen()
    {
        var area = SystemParameters.WorkArea;
        var width = ActualWidth > 0 ? ActualWidth : Width;
        var height = ActualHeight > 0 ? ActualHeight : 200;
        if (Left + 80 > area.Right) Left = area.Right - width - 12;
        if (Top + 40 > area.Bottom) Top = Math.Max(area.Top + 8, area.Bottom - height - 12);
        if (Left < area.Left) Left = area.Left + 12;
        if (Top < area.Top) Top = area.Top + 12;
    }

    private void SaveSettings()
    {
        if (_loadingSettings) return;
        _settings.Left = Left;
        _settings.Top = Top;
        _settings.AlwaysOnTop = Topmost;
        _settings.CardOpacity = OpacitySlider.Value;
        _settings.DetailsOpen = DetailsPanel.Visibility == Visibility.Visible;
        _settings.ControlsOpen = ControlsPanel.Visibility == Visibility.Visible;
        _settings.StartWithWindows = StartupToggle.IsChecked == true;
        try { SettingsStore.Save(_settings); }
        catch (IOException) { /* keep running if the settings file is locked */ }
    }

    private static SolidColorBrush AccentBrush() => Freeze(118, 185, 0);
    private static SolidColorBrush ChipBrush() => Freeze(34, 40, 54);
    private static SolidColorBrush DarkBrush() => Freeze(16, 20, 10);
    private static SolidColorBrush TextBrush() => Freeze(232, 237, 245);
    private static SolidColorBrush MutedBrush() => Freeze(147, 160, 181);

    private static SolidColorBrush Freeze(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
