using System.Net.NetworkInformation;

namespace ByeDpiPc.Core;

public enum ConnectionState { Disconnected, Connecting, Connected, Disconnecting }

/// <summary>Starts and stops byedpi (+ the tunnel in VPN mode). The PC version of ServiceManager.</summary>
public sealed class ConnectionService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ChildProcess? _proxy;
    private Tunnel? _tunnel;
    private LocalDnsServer? _dns;
    private AppSettings? _running;
    private string? _boundIp;
    private CancellationTokenSource? _networkDebounce;
    private bool _stopping;

    public ConnectionState State { get; private set; } = ConnectionState.Disconnected;
    public ConnectionMode? ActiveMode => _running?.Mode;
    public string? Endpoint { get; private set; }
    public string? LastError { get; private set; }
    public string? BoundAdapter { get; private set; }

    /// <summary>Raised on any thread.</summary>
    public event Action? StateChanged;

    public ConnectionService()
    {
        NetworkChange.NetworkAddressChanged += (_, _) => OnNetworkChanged();
    }

    public async Task StartAsync(AppSettings settings)
    {
        await _gate.WaitAsync();
        try
        {
            if (State != ConnectionState.Disconnected) return;
            LastError = null;
            _stopping = false;
            SetState(ConnectionState.Connecting);
            try
            {
                await StartCoreAsync(settings.Clone());
                SetState(ConnectionState.Connected);
            }
            catch (Exception e)
            {
                LastError = e.Message;
                Log.Write($"Start failed: {e.Message}");
                StopCore();
                SetState(ConnectionState.Disconnected);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (State == ConnectionState.Disconnected) return;
            SetState(ConnectionState.Disconnecting);
            await Task.Run(StopCore);
            SetState(ConnectionState.Disconnected);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Synchronous teardown for app exit.</summary>
    public void Shutdown()
    {
        StopCore();
        State = ConnectionState.Disconnected;
    }

    private async Task StartCoreAsync(AppSettings s)
    {
        var args = Arguments.Build(s);
        var (host, port) = Arguments.Endpoint(args);
        if (!NetUtil.IsPortFree(host, port))
            throw new InvalidOperationException($"Port {port} is already in use. Pick another port in Settings.");

        if (s.Mode == ConnectionMode.Vpn && !Arguments.HasOption(args, "-I", "--conn-ip"))
        {
            var phys = NetUtil.PhysicalIPv4()
                ?? throw new InvalidOperationException("No connected network adapter with an IPv4 address was found.");
            _boundIp = phys.Address.ToString();
            BoundAdapter = $"{phys.Name} ({_boundIp})";
            args = [.. args, "-I", _boundIp];
        }
        else
        {
            _boundIp = null;
            BoundAdapter = null;
        }

        _running = s;
        _proxy = ChildProcess.Start("ciadpi.exe", args, "byedpi");
        var proxy = _proxy;
        _proxy.Exited += () => OnChildExited("byedpi", proxy.ExitCode);
        if (!await NetUtil.WaitForPortAsync(host, port, TimeSpan.FromSeconds(5)))
        {
            throw new InvalidOperationException(_proxy.HasExited
                ? $"byedpi exited with code {_proxy.ExitCode}. Check the arguments (see Logs)."
                : "byedpi did not open its port in time.");
        }
        Endpoint = $"{host}:{port}";

        if (s.Mode == ConnectionMode.Vpn)
        {
            var dns = s.Dns;
            if (s.UseDoh && !string.IsNullOrWhiteSpace(s.DohUrl))
            {
                try
                {
                    _dns = new LocalDnsServer(s.DohUrl.Trim());
                    dns = LocalDnsServer.Address;
                }
                catch (Exception e)
                {
                    Log.Write($"[dns] encrypted DNS unavailable, falling back to {s.Dns}: {e.Message}");
                }
            }
            _tunnel = new Tunnel();
            var tunnel = _tunnel;
            _tunnel.Exited += () => OnChildExited("tun2socks", tunnel.ExitCode);
            await _tunnel.StartAsync(host, port, dns, captureIPv6: true, CancellationToken.None);
        }
        else if (s.SetSystemProxy)
        {
            SystemProxy.Enable(host, port);
        }

        if (s.UseCommandLine) RememberCommand(s.CommandLine);
        Log.Write($"Connected ({s.Mode}) via {Endpoint}{(BoundAdapter != null ? $", out through {BoundAdapter}" : "")}");
    }

    private void StopCore()
    {
        _stopping = true;
        SystemProxy.Restore();
        _tunnel?.Dispose();
        _tunnel = null;
        _dns?.Dispose();
        _dns = null;
        _proxy?.Dispose();
        _proxy = null;
        _running = null;
        Endpoint = null;
        BoundAdapter = null;
        _boundIp = null;
    }

    private readonly List<DateTime> _autoRestarts = [];

    /// <summary>
    /// An engine died on its own. Reconnect (a crash shouldn't leave the PC unprotected), but
    /// give up after 3 tries in 5 minutes so a broken setup doesn't loop forever.
    /// </summary>
    private void OnChildExited(string name, int? exitCode)
    {
        var settings = _running;
        if (_stopping || State != ConnectionState.Connected || settings == null) return;
        Log.Write($"{name} stopped unexpectedly (exit code {exitCode?.ToString() ?? "?"})");

        _autoRestarts.RemoveAll(t => DateTime.UtcNow - t > TimeSpan.FromMinutes(5));
        bool retry = _autoRestarts.Count < 3;
        if (retry) _autoRestarts.Add(DateTime.UtcNow);
        _ = Task.Run(async () =>
        {
            await StopAsync();
            if (retry)
            {
                Log.Write("Reconnecting");
                await Task.Delay(1000);
                await StartAsync(settings);
            }
            else
            {
                LastError = $"{name} keeps stopping (exit code {exitCode?.ToString() ?? "?"}); see Logs.";
                SetState(ConnectionState.Disconnected);
            }
        });
    }

    /// <summary>
    /// byedpi is pinned to the physical adapter's address, so if Wi-Fi/Ethernet changes we
    /// restart against the new one (like Android re-establishing the VPN).
    /// </summary>
    private void OnNetworkChanged()
    {
        var settings = _running;
        if (settings is not { Mode: ConnectionMode.Vpn, RestartOnNetworkChange: true } || _boundIp == null) return;

        _networkDebounce?.Cancel();
        var cts = _networkDebounce = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            await Task.Delay(3000, cts.Token);
            var phys = NetUtil.PhysicalIPv4(excludeIndex: _tunnel?.InterfaceIndex ?? -1);
            if (State != ConnectionState.Connected || phys == null || phys.Value.Address.ToString() == _boundIp) return;
            Log.Write($"Network changed ({_boundIp} -> {phys.Value.Address}), reconnecting");
            await StopAsync();
            await StartAsync(settings);
        }, cts.Token);
    }

    private void RememberCommand(string command)
    {
        // History lives in the persisted settings; the UI owns saving them.
        CommandUsed?.Invoke(command.Trim());
    }

    public event Action<string>? CommandUsed;

    private void SetState(ConnectionState state)
    {
        State = state;
        StateChanged?.Invoke();
    }
}
