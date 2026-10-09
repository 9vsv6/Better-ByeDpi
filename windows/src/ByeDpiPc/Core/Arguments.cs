using System.Text;

namespace ByeDpiPc.Core;

/// <summary>Builds ciadpi argument lists, ported from ByeByeDPI's ByeDpiProxyPreferences.</summary>
public static class Arguments
{
    private static readonly HashSet<string> Blacklist = ["--help", "--version", "-h", "-v"];

    /// <summary>Arguments for the configured strategy (UI editor or command line).</summary>
    public static List<string> Build(AppSettings s) =>
        s.UseCommandLine ? FromCommandLine(s.CommandLine, s.ProxyIp, s.ProxyPort) : FromUi(s);

    public static List<string> FromCommandLine(string command, string ip, int port)
    {
        var args = ShellSplit(command)
            .SkipWhile(a => !a.StartsWith('-'))
            .Where(a => !Blacklist.Contains(a))
            .ToList();
        var prefix = new List<string>();
        if (!HasOption(args, "-i", "--ip")) prefix.AddRange(["--ip", ip]);
        if (!HasOption(args, "-p", "--port")) prefix.AddRange(["--port", port.ToString()]);
        return [.. prefix, .. args];
    }

    public static List<string> FromUi(AppSettings s)
    {
        var args = new List<string>();
        if (s.ProxyIp.Length > 0) args.Add($"-i{s.ProxyIp}");
        if (s.ProxyPort != 0) args.Add($"-p{s.ProxyPort}");
        if (s.MaxConnections != 0) args.Add($"-c{s.MaxConnections}");
        if (s.BufferSize != 0) args.Add($"-b{s.BufferSize}");

        var protocols = new List<string>();
        if (s.DesyncHttps) protocols.Add("t");
        if (s.DesyncHttp) protocols.Add("h");
        var protoArg = protocols.Count > 0 ? $"-K{string.Join(",", protocols)}" : null;

        var hosts = s.HostsMode switch
        {
            HostsMode.Blacklist => s.HostsBlacklist,
            HostsMode.Whitelist => s.HostsWhitelist,
            _ => "",
        };
        if (!string.IsNullOrWhiteSpace(hosts))
        {
            var hostStr = ":" + string.Join(" ", hosts.Split((char[])['\r', '\n', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries));
            if (s.HostsMode == HostsMode.Blacklist)
            {
                // Listed hosts fall into an empty group, so they get no desync.
                args.Add($"-H{hostStr}");
                args.Add("-An");
                if (protoArg != null) args.Add(protoArg);
            }
            else
            {
                if (protoArg != null) args.Add(protoArg);
                args.Add($"-H{hostStr}");
            }
        }
        else if (protoArg != null)
        {
            args.Add(protoArg);
        }

        if (s.DefaultTtl != 0) args.Add($"-g{s.DefaultTtl}");
        if (s.NoDomain) args.Add("-N");

        if (s.SplitPosition != 0)
        {
            var pos = s.SplitPosition + (s.SplitAtHost ? "+h" : "");
            var option = s.DesyncMethod switch
            {
                DesyncMethod.Split => "-s",
                DesyncMethod.Disorder => "-d",
                DesyncMethod.OOB => "-o",
                DesyncMethod.DISOOB => "-q",
                DesyncMethod.Fake => "-f",
                _ => "",
            };
            if (option.Length > 0) args.Add(option + pos);
        }

        if (s.DesyncMethod == DesyncMethod.Fake)
        {
            if (s.FakeTtl != 0) args.Add($"-t{s.FakeTtl}");
            if (s.FakeSni.Length > 0) args.Add($"-n{s.FakeSni}");
            if (s.FakeOffset != 0) args.Add($"-O{s.FakeOffset}");
        }

        if (s.DesyncMethod is DesyncMethod.OOB or DesyncMethod.DISOOB)
        {
            var c = string.IsNullOrEmpty(s.OobChar) ? 'a' : s.OobChar[0];
            args.Add($"-e{(byte)c}");
        }

        var mod = new List<string>();
        if (s.HostMixedCase) mod.Add("h");
        if (s.DomainMixedCase) mod.Add("d");
        if (s.HostRemoveSpaces) mod.Add("r");
        if (mod.Count > 0) args.Add($"-M{string.Join(",", mod)}");

        if (s.TlsRecordSplit && s.TlsRecordSplitPosition != 0)
            args.Add($"-r{s.TlsRecordSplitPosition}{(s.TlsRecordSplitAtSni ? "+s" : "")}");

        args.Add("-An");

        if (s.DesyncUdp)
        {
            args.Add("-Ku");
            if (s.UdpFakeCount != 0) args.Add($"-a{s.UdpFakeCount}");
            args.Add("-An");
        }

        return args;
    }

    /// <summary>True when the list already sets the option, in any of byedpi's spellings.</summary>
    public static bool HasOption(IEnumerable<string> args, string shortName, string longName) =>
        args.Any(a => a == shortName || a == longName || a.StartsWith(longName + "=") ||
                      (a.StartsWith(shortName) && !a.StartsWith("--") && a.Length > shortName.Length));

    /// <summary>Reads the listen endpoint back out of an argument list (falls back to byedpi's defaults).</summary>
    public static (string Ip, int Port) Endpoint(IReadOnlyList<string> args)
    {
        string ip = "0.0.0.0";
        int port = 1080;
        for (int i = 0; i < args.Count; i++)
        {
            var a = args[i];
            string? Next() => i + 1 < args.Count ? args[++i] : null;
            if (a is "-i" or "--ip") ip = Next() ?? ip;
            else if (a.StartsWith("--ip=")) ip = a[5..];
            else if (a.StartsWith("-i") && !a.StartsWith("--")) ip = a[2..];
            else if (a is "-p" or "--port") int.TryParse(Next(), out port);
            else if (a.StartsWith("--port=")) int.TryParse(a[7..], out port);
            else if (a.StartsWith("-p") && !a.StartsWith("--")) int.TryParse(a[2..], out port);
        }
        if (ip is "0.0.0.0" or "") ip = "127.0.0.1";
        if (ip is "::" or "[::]") ip = "::1";
        return (ip, port is > 0 and < 65536 ? port : 1080);
    }

    /// <summary>POSIX-ish splitter, same rules as ByeByeDPI's shellSplit.</summary>
    public static List<string> ShellSplit(string text)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        char quoteChar = ' ';
        bool escaping = false, quoting = false;
        int lastCloseQuote = int.MinValue;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (escaping)
            {
                current.Append(c);
                escaping = false;
            }
            else if (c == '\\' && quoting)
            {
                if (i + 1 < text.Length && text[i + 1] == quoteChar) escaping = true;
                else current.Append(c);
            }
            else if (quoting && c == quoteChar)
            {
                quoting = false;
                lastCloseQuote = i;
            }
            else if (!quoting && c is '\'' or '"')
            {
                quoting = true;
                quoteChar = c;
            }
            else if (!quoting && char.IsWhiteSpace(c))
            {
                if (current.Length > 0 || lastCloseQuote == i - 1)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(c);
            }
        }
        if (current.Length > 0 || lastCloseQuote == text.Length - 1) tokens.Add(current.ToString());
        return tokens;
    }

    /// <summary>Joins args back into a command line for display/history.</summary>
    public static string Join(IEnumerable<string> args) =>
        string.Join(" ", args.Select(a => a.Length == 0 || a.Any(char.IsWhiteSpace) ? $"\"{a.Replace("\"", "\\\"")}\"" : a));
}
