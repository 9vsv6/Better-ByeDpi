using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace ByeDpiPc.Core;

public sealed class SiteResult(string site, int success, int total)
{
    public string Site { get; } = site;
    public int Success { get; } = success;
    public int Total { get; } = total;
    public override string ToString() => $"{Site}  {Success}/{Total}";
}

public sealed class StrategyResult(string command) : INotifyPropertyChanged
{
    private int _success, _total, _progress;
    private bool _completed, _running;

    public string Command { get; } = command;
    public ObservableCollection<SiteResult> Sites { get; } = [];

    public int Success { get => _success; set => Set(ref _success, value); }
    public int Total { get => _total; set => Set(ref _total, value); }
    public int Progress { get => _progress; set => Set(ref _progress, value); }
    public bool Completed { get => _completed; set => Set(ref _completed, value); }
    public bool Running { get => _running; set => Set(ref _running, value); }

    public int Percent => Total > 0 ? Success * 100 / Total : 0;

    public string Summary => Completed ? $"{Percent}%  ({Success}/{Total})"
        : Running ? $"testing… {Progress}/{Total}" : "waiting";

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string name = "")
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new(name));
        PropertyChanged?.Invoke(this, new(nameof(Percent)));
        PropertyChanged?.Invoke(this, new(nameof(Summary)));
    }
}

/// <summary>
/// Port of ByeByeDPI's proxy test: launch byedpi once per strategy on a spare port and
/// count how many of the chosen sites answer through it.
/// </summary>
public static class StrategyTester
{
    public static readonly string[] BuiltInLists =
        ["youtube", "googlevideo", "discord", "telegram", "social", "cloudflare", "general", "turkiye"];

    public static string ReadAsset(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new FileNotFoundException(name);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public static List<string> Lines(string text) =>
        text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')).ToList();

    public static List<string> Strategies(AppSettings s)
    {
        var content = s.TestUseCustomCommands ? s.TestCustomCommands : ReadAsset("proxytest_strategies.list");
        return Lines(content.Replace("{sni}", $"\"{s.TestSni}\""));
    }

    public static List<string> Sites(AppSettings s)
    {
        var sites = new List<string>();
        foreach (var list in s.TestSiteLists)
        {
            try { sites.AddRange(Lines(ReadAsset($"proxytest_{list}.sites"))); }
            catch (FileNotFoundException) { }
        }
        sites.AddRange(Lines(s.TestCustomSites));
        return sites.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <param name="post">Marshals result updates onto the UI thread.</param>
    public static async Task RunAsync(AppSettings s, IReadOnlyList<StrategyResult> strategies, List<string> sites,
        Action<Action> post, CancellationToken token)
    {
        using var resolver = new SiteResolver(s);
        foreach (var strategy in strategies)
        {
            token.ThrowIfCancellationRequested();
            post(() =>
            {
                strategy.Total = sites.Count * s.TestRequests;
                strategy.Running = true;
            });

            var args = Arguments.FromCommandLine(strategy.Command, "127.0.0.1", s.TestPort);
            var (host, port) = Arguments.Endpoint(args);
            ChildProcess? engine = null;
            int succeeded = 0, total = sites.Count * s.TestRequests;
            try
            {
                if (!NetUtil.IsPortFree(host, port))
                    throw new InvalidOperationException($"Test port {port} is busy");
                engine = ChildProcess.Start("ciadpi.exe", args, "test", quiet: true);
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(300, s.TestDelaySec * 500)), token);
                if (engine.HasExited || !await NetUtil.WaitForPortAsync(host, port, TimeSpan.FromSeconds(3), token))
                    throw new InvalidOperationException("byedpi rejected this command");

                await CheckSitesAsync(host, port, resolver, sites, s, (site, ok) =>
                {
                    Interlocked.Add(ref succeeded, ok);
                    post(() =>
                    {
                        strategy.Progress += s.TestRequests;
                        strategy.Success += ok;
                        strategy.Sites.Add(new SiteResult(site, ok, s.TestRequests));
                    });
                }, token);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                Log.Write($"[test] {strategy.Command}: {e.Message}");
                succeeded = 0;
                post(() =>
                {
                    strategy.Success = 0;
                    strategy.Sites.Clear();
                    foreach (var site in sites) strategy.Sites.Add(new SiteResult(site, 0, s.TestRequests));
                });
            }
            finally
            {
                engine?.Dispose();
                post(() =>
                {
                    strategy.Running = false;
                    strategy.Completed = true;
                });
            }
            Log.Write($"[test] {(total > 0 ? succeeded * 100 / total : 0),3}%  {strategy.Command}");
            await Task.Delay(TimeSpan.FromMilliseconds(s.TestDelaySec * 500), token);
        }
    }

    private static async Task CheckSitesAsync(string host, int port, SiteResolver resolver, List<string> sites,
        AppSettings s, Action<string, int> onSite, CancellationToken token)
    {
        using var handler = new SocketsHttpHandler
        {
            // Names are resolved here (over DoH) and byedpi gets a bare IP, exactly like traffic
            // arriving from the tunnel; byedpi's own lookup would hit the ISP's poisoned DNS.
            UseProxy = false,
            ConnectCallback = (ctx, ct) => Socks5ConnectAsync(host, port, resolver, ctx.DnsEndPoint, ct),
            AllowAutoRedirect = true,
            PooledConnectionLifetime = TimeSpan.Zero,
            ConnectTimeout = TimeSpan.FromSeconds(s.TestTimeoutSec),
            SslOptions = { RemoteCertificateValidationCallback = (_, _, _, _) => true },
        };
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0 Safari/537.36");
        client.DefaultRequestHeaders.ConnectionClose = true;

        using var limit = new SemaphoreSlim(Math.Max(1, s.TestConcurrency));
        await Task.WhenAll(sites.Select(async site =>
        {
            await limit.WaitAsync(token);
            try
            {
                int ok = 0;
                for (int i = 0; i < s.TestRequests; i++)
                    if (await CheckOnceAsync(client, site, s.TestTimeoutSec, token)) ok++;
                onSite(site, ok);
            }
            finally
            {
                limit.Release();
            }
        }));
    }

    /// <summary>
    /// Any HTTP answer counts, unless the body is cut short of its Content-Length — that is
    /// how DPI "throttling" by truncation shows up (same rule as SiteCheckUtils).
    /// </summary>
    private static async Task<bool> CheckOnceAsync(HttpClient client, string site, int timeoutSec, CancellationToken token)
    {
        var url = site.StartsWith("http://") || site.StartsWith("https://") ? site : $"https://{site}";
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSec));
        try
        {
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            long declared = response.Content.Headers.ContentLength ?? -1;
            long limit = declared > 0 ? declared : 1024 * 1024;
            long actual = 0;
            var buffer = new byte[8192];
            try
            {
                await using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
                while (actual < limit)
                {
                    int read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, limit - actual)), cts.Token);
                    if (read == 0) break;
                    actual += read;
                }
            }
            catch (Exception) when (!token.IsCancellationRequested)
            {
                // Truncated body: judged below.
            }
            return declared <= 0 || actual >= declared;
        }
        catch (Exception) when (!token.IsCancellationRequested)
        {
            return false;
        }
    }

    private static async ValueTask<Stream> Socks5ConnectAsync(string proxyHost, int proxyPort, SiteResolver resolver,
        DnsEndPoint target, CancellationToken token)
    {
        var ip = await resolver.ResolveAsync(target.Host, token)
            ?? throw new HttpRequestException($"Could not resolve {target.Host}");
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(proxyHost, proxyPort, token);
            var stream = new NetworkStream(socket, ownsSocket: true);
            await stream.WriteAsync(new byte[] { 5, 1, 0 }, token);
            var buf = new byte[262];
            await stream.ReadExactlyAsync(buf.AsMemory(0, 2), token);
            if (buf[0] != 5 || buf[1] != 0) throw new HttpRequestException("SOCKS5 greeting rejected");

            var request = new byte[10];
            request[0] = 5; request[1] = 1; request[3] = 1;
            ip.GetAddressBytes().CopyTo(request, 4);
            request[8] = (byte)(target.Port >> 8);
            request[9] = (byte)target.Port;
            await stream.WriteAsync(request, token);

            await stream.ReadExactlyAsync(buf.AsMemory(0, 4), token);
            if (buf[1] != 0) throw new HttpRequestException($"SOCKS5 connect failed ({buf[1]})");
            int rest = buf[3] switch { 1 => 4, 4 => 16, _ => -1 } + 2;
            if (buf[3] == 3)
            {
                await stream.ReadExactlyAsync(buf.AsMemory(0, 1), token);
                rest = buf[0] + 2;
            }
            await stream.ReadExactlyAsync(buf.AsMemory(0, rest), token);
            return stream;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>Caches one IPv4 per host, looked up over DoH when enabled.</summary>
    private sealed class SiteResolver(AppSettings s) : IDisposable
    {
        private readonly DohClient? _doh = s.UseDoh && !string.IsNullOrWhiteSpace(s.DohUrl) ? new DohClient(s.DohUrl.Trim()) : null;
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Task<IPAddress?>> _cache = new();

        public Task<IPAddress?> ResolveAsync(string host, CancellationToken token)
        {
            if (IPAddress.TryParse(host, out var literal)) return Task.FromResult<IPAddress?>(literal);
            return _cache.GetOrAdd(host.ToLowerInvariant(), h => LookupAsync(h, token));
        }

        private async Task<IPAddress?> LookupAsync(string host, CancellationToken token)
        {
            try
            {
                var list = _doh != null
                    ? await _doh.ResolveIPv4Async(host, token)
                    : (await Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, token)).ToList();
                return list.FirstOrDefault(a => !a.Equals(IPAddress.Any));
            }
            catch (Exception) when (!token.IsCancellationRequested)
            {
                return null;
            }
        }

        public void Dispose() => _doh?.Dispose();
    }

    public static string ResultsPath => Path.Combine(AppSettings.Directory, "test-results.txt");
}
