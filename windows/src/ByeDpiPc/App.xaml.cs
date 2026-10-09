using System.Threading;
using System.Windows;
using ByeDpiPc.Core;

namespace ByeDpiPc;

public partial class App : Application
{
    private const string InstanceName = "ByeDPI-PC-7f3c1e";
    private Mutex? _instance;
    private EventWaitHandle? _showSignal;

    public static AppSettings Settings { get; set; } = new();
    public static ConnectionService Connection { get; } = new();
    public static bool LaunchedAtSignIn { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        _instance = new Mutex(true, InstanceName, out bool first);
        if (!first)
        {
            // Bring the running copy to the front instead of starting a second engine.
            try { EventWaitHandle.OpenExisting(InstanceName + "-show").Set(); } catch { }
            Shutdown();
            return;
        }
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, InstanceName + "-show");
        new Thread(() =>
        {
            while (_showSignal.WaitOne())
                Dispatcher.Invoke(() => (MainWindow as MainWindow)?.ShowFromTray());
        }) { IsBackground = true }.Start();

        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Write($"Unhandled: {args.Exception}");
            MessageBox.Show(args.Exception.Message, "ByeDPI", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        LaunchedAtSignIn = e.Args.Contains("--autostart");
        Settings = AppSettings.Load();
        SystemProxy.Restore(); // undo a proxy left behind by a crash
        Log.Write($"ByeDPI for PC started, engines in {ChildProcess.BinDirectory}");

        var window = new MainWindow();
        MainWindow = window;
        bool hidden = LaunchedAtSignIn ? Settings.TrayAtSignIn : Settings.StartMinimized;
        bool connect = LaunchedAtSignIn ? Settings.ConnectAtSignIn : Settings.ConnectOnLaunch;
        if (!hidden) window.Show();
        if (connect) _ = LaunchedAtSignIn ? ConnectWhenNetworkIsUpAsync() : Connection.StartAsync(Settings);
    }

    /// <summary>At sign-in the network adapter is often not ready yet; keep trying for about a minute.</summary>
    private static async Task ConnectWhenNetworkIsUpAsync()
    {
        for (int attempt = 0; attempt < 12; attempt++)
        {
            await Connection.StartAsync(Settings);
            if (Connection.State != ConnectionState.Disconnected || Connection.LastError == null) return;
            await Task.Delay(5000);
            if (Connection.State != ConnectionState.Disconnected) return; // the user connected meanwhile
        }
    }

    public static void Quit()
    {
        try { Settings.Save(); } catch { }
        Connection.Shutdown();
        Current.Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Connection.Shutdown();
        _instance?.Dispose();
        base.OnExit(e);
    }
}
