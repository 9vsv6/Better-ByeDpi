namespace ByeDpiPc.Core;

/// <summary>
/// System-wide "VPN" mode: tun2socks creates a Wintun adapter and feeds every TCP/UDP flow
/// into byedpi's SOCKS port; we then give the adapter an address and take over the default
/// route with two /1 routes (so the real default route is never touched and disappears with
/// the adapter). This is the PC counterpart of ByeByeDPI's VpnService + hev-socks5-tunnel.
/// </summary>
public sealed class Tunnel : IDisposable
{
    public const string AdapterName = "ByeDPI";
    private const string Address = "198.18.0.1";
    private const string AddressV6 = "fdfe:dcba:9876::1";

    private ChildProcess? _process;

    public int InterfaceIndex { get; private set; } = -1;
    public int? ExitCode => _process is { HasExited: true } p ? p.ExitCode : null;
    public event Action? Exited;

    public async Task StartAsync(string socksHost, int socksPort, string dns, bool captureIPv6, CancellationToken token)
    {
        var host = socksHost.Contains(':') ? $"[{socksHost}]" : socksHost;
        _process = ChildProcess.Start("tun2socks.exe",
        [
            "--device", $"tun://{AdapterName}",
            "--proxy", $"socks5://{host}:{socksPort}",
            "--mtu", "1500",
            "--loglevel", "warn",
        ], "tun2socks");
        _process.Exited += () => Exited?.Invoke();

        var deadline = DateTime.UtcNow.AddSeconds(15);
        while ((InterfaceIndex = NetUtil.InterfaceIndexByName(AdapterName) ?? -1) < 0)
        {
            if (_process.HasExited) throw new InvalidOperationException($"tun2socks exited with code {_process.ExitCode} (see Logs).");
            if (DateTime.UtcNow > deadline) throw new TimeoutException("The Wintun adapter did not appear.");
            await Task.Delay(200, token);
        }

        var i = InterfaceIndex;
        Log.Write($"[tun] adapter '{AdapterName}' is interface {i}");
        await Netsh($"interface ipv4 set address name={i} source=static address={Address} mask=255.255.255.0 store=active");
        await Netsh($"interface ipv4 set interface interface={i} metric=1");
        if (!string.IsNullOrWhiteSpace(dns))
        {
            var servers = dns.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries);
            await Netsh($"interface ipv4 set dnsservers name={i} source=static address={servers[0]} register=none validate=no");
            foreach (var extra in servers.Skip(1))
                await Netsh($"interface ipv4 add dnsservers name={i} address={extra} validate=no", required: false);
        }
        await Netsh($"interface ipv4 add route prefix=0.0.0.0/1 interface={i} nexthop={Address} metric=1 store=active");
        await Netsh($"interface ipv4 add route prefix=128.0.0.0/1 interface={i} nexthop={Address} metric=1 store=active");

        if (captureIPv6 && NetUtil.HasGlobalIPv6(excludeIndex: i))
        {
            // byedpi is bound to an IPv4 address and refuses IPv6 targets, so IPv6 flows are reset
            // inside the tunnel and apps fall back to IPv4 (which does go through byedpi) instead of
            // leaking straight past it. The adapter's IPv6 stack can come up a moment after IPv4.
            bool ok = false;
            for (int attempt = 0; attempt < 10 && !ok; attempt++)
            {
                if (attempt > 0) await Task.Delay(300, token);
                ok = await NetUtil.RunAsync("netsh", $"interface ipv6 add address interface={i} address={AddressV6}/64 store=active",
                    logFailure: attempt == 9) == 0;
            }
            if (ok)
            {
                await Netsh($"interface ipv6 add route prefix=::/1 interface={i} metric=1 store=active", required: false);
                await Netsh($"interface ipv6 add route prefix=8000::/1 interface={i} metric=1 store=active", required: false);
            }
            else
            {
                Log.Write("[tun] warning: IPv6 could not be captured; IPv6 sites will bypass byedpi");
            }
        }

        await NetUtil.RunAsync("ipconfig", "/flushdns", logFailure: false);
    }

    private static async Task Netsh(string args, bool required = true)
    {
        var code = await NetUtil.RunAsync("netsh", args);
        if (code != 0 && required) throw new InvalidOperationException($"netsh {args} failed (code {code}).");
    }

    public void Stop()
    {
        // Routes are store=active and bound to the adapter, which Wintun deletes when tun2socks exits.
        _process?.Dispose();
        _process = null;
        InterfaceIndex = -1;
    }

    public void Dispose() => Stop();
}
