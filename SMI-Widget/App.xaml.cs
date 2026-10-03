using System.Windows;

namespace SMI_Widget;

public partial class App : System.Windows.Application
{
    private static Mutex? _mutex;
    private static bool _ownsMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        try
        {
            _mutex = new Mutex(true, @"Local\SMIWidget.SingleInstance", out _ownsMutex);
        }
        catch (Exception)
        {
            _ownsMutex = false;
        }

        if (_mutex != null && !_ownsMutex)
        {
            System.Windows.MessageBox.Show(
                "SMI Widget is already running. Look for the green S icon in the system tray.",
                "SMI Widget",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            System.Windows.MessageBox.Show(args.Exception.Message, "SMI Widget", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_ownsMutex)
        {
            try { _mutex?.ReleaseMutex(); } catch (ApplicationException) { /* not owned */ }
        }
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
