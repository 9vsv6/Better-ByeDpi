using System.Buffers.Binary;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;

namespace ByeDpiPc.Core;

/// <summary>
/// Encrypted DNS. Many ISPs answer every plain DNS query (to any server, port 53 is
/// intercepted) with a sinkhole for blocked sites, which no DPI trick can undo, so in
/// VPN mode Windows is pointed at this local resolver and every query goes out as DoH.
/// </summary>
public sealed class DohClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly Uri _url;

    public DohClient(string url)
    {
        _url = new Uri(url);
        IPAddress[]? pinned = null;
        if (!IPAddress.TryParse(_url.Host.Trim('[', ']'), out _))
        {
            // Resolve the DoH host once, now, so it never depends on the resolver it serves.
            pinned = Dns.GetHostAddresses(_url.Host);
        }
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            ConnectTimeout = TimeSpan.FromSeconds(5),
            EnableMultipleHttp2Connections = true,
        };
        if (pinned != null)
        {
            handler.ConnectCallback = async (ctx, token) =>
            {
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(pinned, ctx.DnsEndPoint.Port, token);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            };
        }
        _http = new HttpClient(handler)
        {
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
            Timeout = TimeSpan.FromSeconds(6),
        };
    }

    /// <summary>Sends a raw DNS message and returns the raw answer.</summary>
    public async Task<byte[]> QueryAsync(byte[] query, CancellationToken token)
    {
        using var content = new ByteArrayContent(query);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/dns-message");
        using var request = new HttpRequestMessage(HttpMethod.Post, _url) { Content = content };
        request.Headers.Accept.ParseAdd("application/dns-message");
        using var response = await _http.SendAsync(request, token);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(token);
    }

    /// <summary>A records for a name (used by the strategy tester).</summary>
    public async Task<List<IPAddress>> ResolveIPv4Async(string host, CancellationToken token)
    {
        var answer = await QueryAsync(BuildQuery(host, type: 1), token);
        return ParseARecords(answer);
    }

    private static byte[] BuildQuery(string host, ushort type)
    {
        var body = new List<byte> { 0, 0, 1, 0, 0, 1, 0, 0, 0, 0, 0, 0 }; // id 0, RD, 1 question
        foreach (var label in host.TrimEnd('.').Split('.'))
        {
            var bytes = System.Text.Encoding.ASCII.GetBytes(label);
            body.Add((byte)bytes.Length);
            body.AddRange(bytes);
        }
        body.AddRange([0, (byte)(type >> 8), (byte)type, 0, 1]);
        return [.. body];
    }

    private static List<IPAddress> ParseARecords(byte[] msg)
    {
        var result = new List<IPAddress>();
        if (msg.Length < 12) return result;
        int qd = BinaryPrimitives.ReadUInt16BigEndian(msg.AsSpan(4));
        int an = BinaryPrimitives.ReadUInt16BigEndian(msg.AsSpan(6));
        int pos = 12;
        for (int i = 0; i < qd; i++) pos = SkipName(msg, pos) + 4;
        for (int i = 0; i < an && pos + 10 <= msg.Length; i++)
        {
            pos = SkipName(msg, pos);
            ushort type = BinaryPrimitives.ReadUInt16BigEndian(msg.AsSpan(pos));
            ushort len = BinaryPrimitives.ReadUInt16BigEndian(msg.AsSpan(pos + 8));
            pos += 10;
            if (type == 1 && len == 4 && pos + 4 <= msg.Length) result.Add(new IPAddress(msg.AsSpan(pos, 4)));
            pos += len;
        }
        return result;
    }

    private static int SkipName(byte[] msg, int pos)
    {
        while (pos < msg.Length)
        {
            byte b = msg[pos];
            if (b == 0) return pos + 1;
            if ((b & 0xC0) == 0xC0) return pos + 2;
            pos += b + 1;
        }
        return pos;
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>Plain DNS server on 127.0.0.1:53 (UDP + TCP) that forwards everything over DoH.</summary>
public sealed class LocalDnsServer : IDisposable
{
    public const string Address = "127.0.0.1";

    private readonly DohClient _doh;
    private readonly CancellationTokenSource _cts = new();
    private readonly UdpClient _udp;
    private readonly TcpListener _tcp;

    public LocalDnsServer(string dohUrl)
    {
        _doh = new DohClient(dohUrl);
        var endpoint = new IPEndPoint(IPAddress.Parse(Address), 53);
        try
        {
            _udp = new UdpClient(endpoint);
            _tcp = new TcpListener(endpoint);
            _tcp.Start();
        }
        catch (SocketException e)
        {
            _udp?.Dispose();
            _doh.Dispose();
            throw new InvalidOperationException($"Port 53 on {Address} is taken by another program ({e.SocketErrorCode}).");
        }
        // Windows reports ICMP "port unreachable" from earlier replies as a receive error; ignore it.
        _udp.Client.IOControl(-1744830452 /* SIO_UDP_CONNRESET */, [0, 0, 0, 0], null);
        _ = Task.Run(UdpLoop);
        _ = Task.Run(TcpLoop);
        Log.Write($"[dns] encrypted DNS on {Address}:53 -> {dohUrl}");
    }

    private async Task UdpLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            UdpReceiveResult packet;
            try { packet = await _udp.ReceiveAsync(_cts.Token); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { continue; }
            _ = Task.Run(async () =>
            {
                var reply = await AnswerAsync(packet.Buffer);
                if (reply != null)
                {
                    try { await _udp.SendAsync(reply, packet.RemoteEndPoint, _cts.Token); }
                    catch (Exception) { }
                }
            });
        }
    }

    private async Task TcpLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _tcp.AcceptTcpClientAsync(_cts.Token); }
            catch (Exception) { return; }
            _ = Task.Run(async () =>
            {
                using (client)
                {
                    try
                    {
                        var stream = client.GetStream();
                        var lenBuf = new byte[2];
                        while (true)
                        {
                            await stream.ReadExactlyAsync(lenBuf, _cts.Token);
                            var query = new byte[BinaryPrimitives.ReadUInt16BigEndian(lenBuf)];
                            await stream.ReadExactlyAsync(query, _cts.Token);
                            var reply = await AnswerAsync(query);
                            if (reply == null) return;
                            BinaryPrimitives.WriteUInt16BigEndian(lenBuf, (ushort)reply.Length);
                            await stream.WriteAsync(lenBuf, _cts.Token);
                            await stream.WriteAsync(reply, _cts.Token);
                        }
                    }
                    catch (Exception) { }
                }
            });
        }
    }

    private async Task<byte[]?> AnswerAsync(byte[] query)
    {
        if (query.Length < 12) return null;
        try
        {
            byte[] reply;
            try
            {
                reply = await _doh.QueryAsync(query, _cts.Token);
            }
            catch (HttpRequestException)
            {
                // Typically the instant the tunnel routes are being swapped in; one retry covers it.
                await Task.Delay(400, _cts.Token);
                reply = await _doh.QueryAsync(query, _cts.Token);
            }
            if (reply.Length >= 2)
            {
                reply[0] = query[0]; // keep the client's transaction id
                reply[1] = query[1];
            }
            return reply;
        }
        catch (Exception e) when (!_cts.IsCancellationRequested)
        {
            Log.Write($"[dns] DoH query failed: {e.Message}");
            var fail = (byte[])query.Clone(); // SERVFAIL echoing the question
            fail[2] = (byte)(0x80 | (query[2] & 0x01));
            fail[3] = 0x82;
            return fail;
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _udp.Dispose();
        _tcp.Stop();
        _doh.Dispose();
    }
}
