using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace ByeDpiPc.Core;

public static class NetUtil
{
    /// <summary>
    /// The IPv4 address of the adapter Windows would use to reach the internet right now
    /// (call it before the tunnel routes exist). byedpi binds its outgoing sockets to this
    /// address; with Windows' strong host model that pins them to the physical adapter,
    /// which is the PC equivalent of Android's addDisallowedApplication(self).
    /// </summary>
    public static (IPAddress Address, int InterfaceIndex, string Name)? PhysicalIPv4(int excludeIndex = -1)
    {
        var probe = BitConverter.ToUInt32(IPAddress.Parse("1.1.1.1").GetAddressBytes(), 0);
        if (GetBestInterface(probe, out var best) == 0 && (int)best != excludeIndex)
        {
            var found = AddressOf((int)best);
            if (found != null) return found;
        }

        // Fallback: the lowest-metric up adapter that has an IPv4 gateway.
        return NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                        n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
            .Select(n => (Nic: n, Props: n.GetIPProperties()))
            .Where(x => x.Props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork &&
                                                          !g.Address.Equals(IPAddress.Any)))
            .Select(x => AddressOf(x.Props.GetIPv4Properties().Index))
            .FirstOrDefault(x => x != null && x.Value.InterfaceIndex != excludeIndex);
    }

    private static (IPAddress Address, int InterfaceIndex, string Name)? AddressOf(int index)
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            var props = nic.GetIPProperties();
            IPv4InterfaceProperties? v4;
            try { v4 = props.GetIPv4Properties(); }
            catch (NetworkInformationException) { continue; }
            if (v4 == null || v4.Index != index) continue;
            var addr = props.UnicastAddresses
                .Select(u => u.Address)
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a));
            return addr == null ? null : (addr, index, nic.Name);
        }
        return null;
    }

    /// <summary>True when some other adapter has a public (2000::/3) IPv6 address, i.e. IPv6 could leak.</summary>
    public static bool HasGlobalIPv6(int excludeIndex)
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            var props = nic.GetIPProperties();
            int index;
            try { index = props.GetIPv6Properties()?.Index ?? -1; }
            catch (NetworkInformationException) { continue; }
            if (index == excludeIndex) continue;
            if (props.UnicastAddresses.Any(u => u.Address.AddressFamily == AddressFamily.InterNetworkV6 &&
                                                (u.Address.GetAddressBytes()[0] & 0xE0) == 0x20))
                return true;
        }
        return false;
    }

    public static int? InterfaceIndexByName(string name)
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (!string.Equals(nic.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
            try { return nic.GetIPProperties().GetIPv4Properties()?.Index; }
            catch (NetworkInformationException) { return null; }
        }
        return null;
    }

    /// <summary>Runs netsh (or another system tool) and returns its exit code; output goes to the log on failure.</summary>
    public static async Task<int> RunAsync(string file, string args, bool logFailure = true)
    {
        var info = new ProcessStartInfo(file, args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var p = Process.Start(info)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        if (p.ExitCode != 0 && logFailure)
            Log.Write($"[net] {file} {args} -> {p.ExitCode}: {(await stdout + await stderr).Trim()}");
        return p.ExitCode;
    }

    public static async Task<bool> WaitForPortAsync(string host, int port, TimeSpan timeout, CancellationToken token = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                using var client = new TcpClient();
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
                cts.CancelAfter(250);
                await client.ConnectAsync(host, port, cts.Token);
                return true;
            }
            catch (Exception) when (!token.IsCancellationRequested)
            {
                await Task.Delay(100, token);
            }
        }
        return false;
    }

    public static bool IsPortFree(string host, int port)
    {
        try
        {
            var addr = IPAddress.TryParse(host, out var a) ? a : IPAddress.Loopback;
            using var l = new TcpListener(addr, port);
            l.Start();
            l.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    [DllImport("iphlpapi.dll")]
    private static extern int GetBestInterface(uint destAddr, out uint bestIfIndex);
}
