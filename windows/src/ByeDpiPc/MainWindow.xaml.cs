using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ByeDpiPc.Core;

namespace ByeDpiPc;

public partial class MainWindow : Window
{
    private readonly TrayIcon _tray;
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private readonly DispatcherTimer _logTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private readonly StringBuilder _pendingLog = new();
    private bool _loading;
    private bool _trayHintShown;
    private CancellationTokenSource? _testCts;

    private static AppSettings S => App.Settings;
    private static ConnectionService Conn => App.Connection;

    public MainWindow()
    {
        InitializeComponent();
        Icon = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
            TrayIcon.MakeIcon(System.Drawing.Color.FromArgb(34, 197, 94)).Handle, Int32Rect.Empty, null);

        MethodBox.ItemsSource = Enum.GetValues<DesyncMethod>();
        HostsModeBox.ItemsSource = Enum.GetValues<HostsMode>();
        foreach (var list in StrategyTester.BuiltInLists)
            SiteListsPanel.Children.Add(new CheckBox { Content = list, Tag = list, Margin = new Thickness(0, 0, 14, 4) });

        _tray = new TrayIcon(ShowFromTray, () => _ = Toggle(), App.Quit);
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SaveQuietly(); };

        LogBox.Text = Log.Snapshot();
        Log.LineAdded += line => { lock (_pendingLog) _pendingLog.AppendLine(line); };
        _logTimer.Tick += (_, _) => FlushLog();
        _logTimer.Start();

        Conn.StateChanged += () => Dispatcher.BeginInvoke(RefreshState);
        Conn.CommandUsed += cmd => Dispatcher.BeginInvoke(() => RememberCommand(cmd));

        LoadSettingsIntoUi();
        RefreshState();
    }

    // ───────────────────────── window / tray ─────────────────────────

    public void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        SaveQuietly();
        if (S.CloseToTray)
        {
            e.Cancel = true;
            Hide();
            if (!_trayHintShown)
            {
                _tray.Notify("ByeDPI is still running", "Use the tray icon to open it or exit.");
                _trayHintShown = true;
            }
            return;
        }
        e.Cancel = true;
        App.Quit();
    }

    protected override void OnClosed(EventArgs e)
    {
        _tray.Dispose();
        base.OnClosed(e);
    }

    private void OnNav(object sender, RoutedEventArgs e)
    {
        if (PageHome == null) return; // fires during InitializeComponent
        var target = (string)((FrameworkElement)sender).Tag;
        foreach (var page in new FrameworkElement[] { PageHome, PageSettings, PageTest, PageLogs })
            page.Visibility = page.Name == target ? Visibility.Visible : Visibility.Collapsed;
        if (target == "PageLogs") LogBox.ScrollToEnd();
        if (target == "PageHome") RefreshState();
    }

    private void OnLink(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }

    // ───────────────────────── home ─────────────────────────

    private async void OnPower(object sender, RoutedEventArgs e) => await Toggle();

    private async Task Toggle()
    {
        if (_testCts != null) return;
        if (Conn.State == ConnectionState.Connected) await Conn.StopAsync();
        else if (Conn.State == ConnectionState.Disconnected)
        {
            SaveQuietly();
            await Conn.StartAsync(S);
        }
    }

    private void OnModeChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        S.Mode = ModeVpn.IsChecked == true ? ConnectionMode.Vpn : ConnectionMode.Proxy;
        SaveQuietly();
        RefreshState();
    }

    private void OnEditStrategy(object sender, RoutedEventArgs e)
    {
        foreach (var rb in ((Panel)NavHome.Parent).Children.OfType<RadioButton>())
            if ((string)rb.Tag == "PageSettings") rb.IsChecked = true;
    }

    private void RefreshState()
    {
        var state = Conn.State;
        bool busy = state is ConnectionState.Connecting or ConnectionState.Disconnecting;
        bool on = state == ConnectionState.Connected;

        PowerButton.IsEnabled = !busy && _testCts == null;
        PowerButton.Background = new SolidColorBrush(on ? Color.FromRgb(34, 197, 94)
            : busy ? Color.FromRgb(245, 158, 11) : Color.FromRgb(107, 114, 128));
        PowerText.Text = state switch
        {
            ConnectionState.Connected => "Disconnect",
            ConnectionState.Connecting => "Connecting…",
            ConnectionState.Disconnecting => "Stopping…",
            _ => "Connect",
        };
        var modeName = (Conn.ActiveMode ?? S.Mode) == ConnectionMode.Vpn ? "VPN" : "Proxy";
        StatusText.Text = state switch
        {
            ConnectionState.Connected => $"Connected · {modeName}",
            ConnectionState.Connecting => "Connecting…",
            ConnectionState.Disconnecting => "Disconnecting…",
            _ => _testCts != null ? "Paused for strategy test" : "Disconnected",
        };
        StatusDetail.Text = on
            ? $"SOCKS5 {Conn.Endpoint}" + (Conn.BoundAdapter != null ? $"\nOutgoing via {Conn.BoundAdapter}" : "")
              + (Conn.ActiveMode == ConnectionMode.Proxy && S.SetSystemProxy ? "\nSet as the Windows proxy" : "")
            : "";
        ErrorText.Text = Conn.LastError ?? "";
        ErrorText.Visibility = string.IsNullOrEmpty(Conn.LastError) || on ? Visibility.Collapsed : Visibility.Visible;
        ModeVpn.IsEnabled = ModeProxy.IsEnabled = state == ConnectionState.Disconnected;

        StrategySource.Text = S.UseCommandLine ? "Command line" : "Simple editor";
        StrategyPreview.Text = Arguments.Join(Arguments.Build(S));

        _tray.Update(on, busy, on ? $"ByeDPI — connected ({modeName})" : busy ? "ByeDPI — working…" : "ByeDPI — disconnected");
        if (state == ConnectionState.Disconnected && !string.IsNullOrEmpty(Conn.LastError) && !IsVisible)
            _tray.Notify("ByeDPI stopped", Conn.LastError);
    }

    // ───────────────────────── settings ─────────────────────────

    private void LoadSettingsIntoUi()
    {
        _loading = true;
        DataContext = null;
        DataContext = S;
        ModeVpn.IsChecked = S.Mode == ConnectionMode.Vpn;
        ModeProxy.IsChecked = S.Mode == ConnectionMode.Proxy;
        EditorCmd.IsChecked = S.UseCommandLine;
        EditorUi.IsChecked = !S.UseCommandLine;
        AutostartBox.IsChecked = Autostart.IsEnabled();
        HistoryBox.ItemsSource = S.CommandHistory.ToList();

        foreach (var cb in SiteListsPanel.Children.OfType<CheckBox>())
            cb.IsChecked = S.TestSiteLists.Contains((string)cb.Tag);
        CustomSitesBox.Text = S.TestCustomSites;
        TestRequestsBox.Text = S.TestRequests.ToString();
        TestTimeoutBox.Text = S.TestTimeoutSec.ToString();
        TestConcurrencyBox.Text = S.TestConcurrency.ToString();
        TestDelayBox.Text = S.TestDelaySec.ToString();
        TestSniBox.Text = S.TestSni;
        CustomCommandsCheck.IsChecked = S.TestUseCustomCommands;
        CustomCommandsBox.Text = S.TestUseCustomCommands && S.TestCustomCommands.Length > 0
            ? S.TestCustomCommands
            : StrategyTester.ReadAsset("proxytest_strategies.list").Trim();
        _loading = false;
        UpdateEditorVisibility();
    }

    private void OnSettingEdited(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        UpdateEditorVisibility();
        SavedHint.Visibility = Visibility.Collapsed;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void OnEditorChanged(object sender, RoutedEventArgs e)
    {
        if (_loading || EditorCmd == null) return;
        bool cmd = EditorCmd.IsChecked == true;
        if (cmd && string.IsNullOrWhiteSpace(S.CommandLine))
        {
            // Start the command line from what the simple editor currently produces.
            S.CommandLine = Arguments.Join(Arguments.FromUi(S).Where(a => !a.StartsWith("-i") && !a.StartsWith("-p")));
            CmdBox.Text = S.CommandLine;
        }
        S.UseCommandLine = cmd;
        UpdateEditorVisibility();
    }

    private void UpdateEditorVisibility()
    {
        if (CmdEditor == null) return;
        CmdEditor.Visibility = S.UseCommandLine ? Visibility.Visible : Visibility.Collapsed;
        UiEditor.Visibility = S.UseCommandLine ? Visibility.Collapsed : Visibility.Visible;
        FakeOptions.Visibility = S.DesyncMethod == DesyncMethod.Fake ? Visibility.Visible : Visibility.Collapsed;
        OobOptions.Visibility = S.DesyncMethod is DesyncMethod.OOB or DesyncMethod.DISOOB ? Visibility.Visible : Visibility.Collapsed;
        BlacklistBox.Visibility = S.HostsMode == HostsMode.Blacklist ? Visibility.Visible : Visibility.Collapsed;
        WhitelistBox.Visibility = S.HostsMode == HostsMode.Whitelist ? Visibility.Visible : Visibility.Collapsed;
        HostsHint.Text = S.HostsMode switch
        {
            HostsMode.Blacklist => "Hosts listed here are left alone (no desync). Space or newline separated.",
            HostsMode.Whitelist => "Only hosts listed here get desync. Space or newline separated.",
            _ => "Desync applies to every host.",
        };
        try { ArgsPreview.Text = "ciadpi " + Arguments.Join(Arguments.Build(S)); }
        catch (Exception ex) { ArgsPreview.Text = ex.Message; }
    }

    private async void OnSaveSettings(object sender, RoutedEventArgs e)
    {
        _saveTimer.Stop();
        SaveQuietly();
        if (Conn.State == ConnectionState.Connected)
        {
            await Conn.StopAsync();
            await Conn.StartAsync(S);
            SavedHint.Text = "Saved and reconnected.";
        }
        else
        {
            SavedHint.Text = "Saved.";
        }
        SavedHint.Visibility = Visibility.Visible;
    }

    private void OnResetSettings(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "Reset all settings to their defaults?", "ByeDPI", MessageBoxButton.OKCancel,
                MessageBoxImage.Question) != MessageBoxResult.OK) return;
        var fresh = new AppSettings { CommandHistory = S.CommandHistory };
        App.Settings = fresh;
        SaveQuietly();
        LoadSettingsIntoUi();
        RefreshState();
    }

    private void OnAutostart(object sender, RoutedEventArgs e)
    {
        bool want = AutostartBox.IsChecked == true;
        if (!Autostart.Set(want))
            MessageBox.Show(this, "Could not update the scheduled task (see Logs).", "ByeDPI", MessageBoxButton.OK, MessageBoxImage.Warning);
        AutostartBox.IsChecked = Autostart.IsEnabled();
    }

    private void OnHistoryPicked(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || HistoryBox.SelectedItem is not string cmd) return;
        CmdBox.Text = cmd;
        e.Handled = true;
    }

    private void OnClearHistory(object sender, RoutedEventArgs e)
    {
        S.CommandHistory.Clear();
        HistoryBox.ItemsSource = null;
        SaveQuietly();
    }

    private void RememberCommand(string cmd)
    {
        if (cmd.Length == 0) return;
        S.CommandHistory.Remove(cmd);
        S.CommandHistory.Insert(0, cmd);
        if (S.CommandHistory.Count > 20) S.CommandHistory.RemoveRange(20, S.CommandHistory.Count - 20);
        _loading = true;
        HistoryBox.ItemsSource = S.CommandHistory.ToList();
        _loading = false;
        SaveQuietly();
    }

    private void SaveQuietly()
    {
        try { S.Save(); }
        catch (Exception e) { Log.Write($"Could not save settings: {e.Message}"); }
    }

    // ───────────────────────── strategy test ─────────────────────────

    private void ReadTestOptions()
    {
        S.TestSiteLists = SiteListsPanel.Children.OfType<CheckBox>().Where(c => c.IsChecked == true).Select(c => (string)c.Tag).ToList();
        S.TestCustomSites = CustomSitesBox.Text;
        S.TestRequests = Math.Clamp(int.TryParse(TestRequestsBox.Text, out var r) ? r : 1, 1, 10);
        S.TestTimeoutSec = Math.Clamp(int.TryParse(TestTimeoutBox.Text, out var t) ? t : 5, 1, 60);
        S.TestConcurrency = Math.Clamp(int.TryParse(TestConcurrencyBox.Text, out var c) ? c : 20, 1, 100);
        S.TestDelaySec = Math.Clamp(int.TryParse(TestDelayBox.Text, out var d) ? d : 1, 0, 30);
        S.TestSni = string.IsNullOrWhiteSpace(TestSniBox.Text) ? "google.com" : TestSniBox.Text.Trim();
        S.TestUseCustomCommands = CustomCommandsCheck.IsChecked == true;
        if (S.TestUseCustomCommands) S.TestCustomCommands = CustomCommandsBox.Text;
        SaveQuietly();
    }

    private async void OnTest(object sender, RoutedEventArgs e)
    {
        if (_testCts != null)
        {
            _testCts.Cancel();
            TestButton.IsEnabled = false;
            return;
        }

        ReadTestOptions();
        try
        {
            ChildProcess.BinPath("ciadpi.exe");
        }
        catch (System.IO.FileNotFoundException ex)
        {
            TestStatus.Text = ex.Message;
            return;
        }
        var sites = StrategyTester.Sites(S);
        var commands = StrategyTester.Strategies(S);
        if (sites.Count == 0 || commands.Count == 0)
        {
            TestStatus.Text = sites.Count == 0 ? "Pick at least one site list (Options)." : "The strategy list is empty.";
            return;
        }

        var results = commands.Select(c => new StrategyResult(c)).ToList();
        ResultsList.ItemsSource = results;
        TestOptionsToggle.IsChecked = false;

        var resume = Conn.State == ConnectionState.Connected;
        _testCts = new CancellationTokenSource();
        TestButton.Content = "Stop";
        if (resume) await Conn.StopAsync();
        RefreshState();

        var started = DateTime.Now;
        int done = 0;
        foreach (var r in results)
            r.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName != nameof(StrategyResult.Completed)) return;
                done = results.Count(x => x.Completed);
                TestStatus.Text = $"Tested {done} of {results.Count} strategies on {sites.Count} sites…";
            };
        TestStatus.Text = $"Testing {results.Count} strategies on {sites.Count} sites…";

        bool cancelled = false;
        try
        {
            await Task.Run(() => StrategyTester.RunAsync(S.Clone(), results, sites,
                action => Dispatcher.BeginInvoke(action), _testCts.Token));
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }
        catch (Exception ex)
        {
            TestStatus.Text = $"Test failed: {ex.Message}";
            Log.Write($"[test] failed: {ex}");
        }

        // Let queued UI updates land before sorting.
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
        var ranked = results.Where(r => r.Completed).OrderByDescending(r => r.Percent).ToList();
        ResultsList.ItemsSource = ranked.Concat(results.Where(r => !r.Completed)).ToList();
        SaveResults(ranked);

        var best = ranked.FirstOrDefault();
        var elapsed = DateTime.Now - started;
        TestStatus.Text = (cancelled ? "Stopped. " : "Done. ")
            + (best != null ? $"Best: {best.Percent}% — press Use to apply it. " : "")
            + $"({elapsed:mm\\:ss})";

        _testCts.Dispose();
        _testCts = null;
        TestButton.Content = "Start test";
        TestButton.IsEnabled = true;
        if (resume) await Conn.StartAsync(S);
        RefreshState();
    }

    private static void SaveResults(List<StrategyResult> ranked)
    {
        try
        {
            System.IO.File.WriteAllLines(StrategyTester.ResultsPath,
                ranked.Select(r => $"{r.Percent,3}%  {r.Success}/{r.Total}  {r.Command}"));
        }
        catch (Exception e)
        {
            Log.Write($"Could not save test results: {e.Message}");
        }
    }

    private async void OnUseStrategy(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not StrategyResult r) return;
        S.UseCommandLine = true;
        S.CommandLine = r.Command;
        SaveQuietly();
        LoadSettingsIntoUi();
        NavHome.IsChecked = true;
        if (_testCts == null && Conn.State == ConnectionState.Connected)
        {
            await Conn.StopAsync();
            await Conn.StartAsync(S);
        }
        RefreshState();
    }

    private void OnCopyStrategy(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is StrategyResult r) Clipboard.SetText(r.Command);
    }

    // ───────────────────────── logs ─────────────────────────

    private void FlushLog()
    {
        string text;
        lock (_pendingLog)
        {
            if (_pendingLog.Length == 0) return;
            text = _pendingLog.ToString();
            _pendingLog.Clear();
        }
        bool atEnd = LogBox.VerticalOffset + LogBox.ViewportHeight >= LogBox.ExtentHeight - 4;
        LogBox.AppendText(text);
        if (LogBox.LineCount > 3000) LogBox.Text = Log.Snapshot() + Environment.NewLine;
        if (atEnd) LogBox.ScrollToEnd();
    }

    private void OnCopyLogs(object sender, RoutedEventArgs e) => Clipboard.SetText(LogBox.Text);

    private void OnClearLogs(object sender, RoutedEventArgs e)
    {
        Log.Clear();
        LogBox.Clear();
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        System.IO.Directory.CreateDirectory(AppSettings.Directory);
        Process.Start(new ProcessStartInfo(AppSettings.Directory) { UseShellExecute = true });
    }
}
