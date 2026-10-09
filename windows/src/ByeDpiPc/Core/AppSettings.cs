using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ByeDpiPc.Core;

public enum ConnectionMode { Vpn, Proxy }

public enum DesyncMethod { None, Split, Disorder, Fake, OOB, DISOOB }

public enum HostsMode { Disable, Blacklist, Whitelist }

/// <summary>
/// Everything the user can configure. Mirrors ByeByeDPI's preferences (UISettings + main settings)
/// minus the Linux-only byedpi options (TCP Fast Open, drop SACK, md5sig).
/// </summary>
public sealed class AppSettings
{
    // Main
    public ConnectionMode Mode { get; set; } = ConnectionMode.Vpn;
    public string Dns { get; set; } = "1.1.1.1";
    public bool UseDoh { get; set; } = true;
    public string DohUrl { get; set; } = "https://1.1.1.1/dns-query";
    public bool SetSystemProxy { get; set; } = true;
    public bool ConnectOnLaunch { get; set; }
    public bool ConnectAtSignIn { get; set; } = true;
    public bool TrayAtSignIn { get; set; }
    public bool StartMinimized { get; set; }
    public bool CloseToTray { get; set; } = true;
    public bool RestartOnNetworkChange { get; set; } = true;

    // Proxy endpoint
    public string ProxyIp { get; set; } = "127.0.0.1";
    public int ProxyPort { get; set; } = 1080;

    // Command line editor. The Android default (OOB) does not get through common Windows/ISP setups.
    public bool UseCommandLine { get; set; } = true;
    public string CommandLine { get; set; } = DefaultCommandLine;

    // Picked by testing TikTok, MyAnimeList, YouTube, Discord and Google on a DPI-filtered line
    // (27/27 requests). The earlier default -s1+s -d3+s let TikTok's handshakes get dropped.
    public const string DefaultCommandLine = "-Kt,h -d1 -s1+s -r1+s -An -Ku -a1 -An";
    private static readonly string[] OldDefaultCommandLines = ["-Kt,h -s1+s -d3+s -An -Ku -a1 -An"];
    public List<string> CommandHistory { get; set; } = [];

    // UI editor (byedpi options)
    public int MaxConnections { get; set; } = 512;
    public int BufferSize { get; set; } = 16384;
    public int DefaultTtl { get; set; }
    public bool NoDomain { get; set; }
    public bool DesyncHttp { get; set; } = true;
    public bool DesyncHttps { get; set; } = true;
    public bool DesyncUdp { get; set; } = true;
    public DesyncMethod DesyncMethod { get; set; } = DesyncMethod.OOB;
    public int SplitPosition { get; set; } = 1;
    public bool SplitAtHost { get; set; }
    public int FakeTtl { get; set; } = 8;
    public string FakeSni { get; set; } = "www.iana.org";
    public int FakeOffset { get; set; }
    public string OobChar { get; set; } = "a";
    public bool HostMixedCase { get; set; }
    public bool DomainMixedCase { get; set; }
    public bool HostRemoveSpaces { get; set; }
    public bool TlsRecordSplit { get; set; }
    public int TlsRecordSplitPosition { get; set; }
    public bool TlsRecordSplitAtSni { get; set; }
    public HostsMode HostsMode { get; set; } = HostsMode.Disable;
    public string HostsBlacklist { get; set; } = "";
    public string HostsWhitelist { get; set; } = "";
    public int UdpFakeCount { get; set; } = 1;

    // Strategy tester
    public string TestSni { get; set; } = "google.com";
    public bool TestUseCustomCommands { get; set; }
    public string TestCustomCommands { get; set; } = "";
    public List<string> TestSiteLists { get; set; } = ["youtube", "googlevideo"];
    public string TestCustomSites { get; set; } = "";
    public int TestDelaySec { get; set; } = 1;
    public int TestRequests { get; set; } = 1;
    public int TestTimeoutSec { get; set; } = 5;
    public int TestConcurrency { get; set; } = 20;
    public int TestPort { get; set; } = 10801;

    public static string Directory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ByeDPI-PC");

    private static string FilePath => Path.Combine(Directory, "settings.json");

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json) ?? new();
                // Move people still on a previous built-in default to the current one.
                if (OldDefaultCommandLines.Contains(loaded.CommandLine.Trim())) loaded.CommandLine = DefaultCommandLine;
                return loaded;
            }
        }
        catch (Exception e)
        {
            Log.Write($"Settings were unreadable, using defaults: {e.Message}");
        }
        return new();
    }

    public void Save()
    {
        System.IO.Directory.CreateDirectory(Directory);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        File.Move(tmp, FilePath, overwrite: true);
    }

    public AppSettings Clone() =>
        JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(this, Json), Json)!;
}
