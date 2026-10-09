# Better ByeDPI

A fork of [ByeByeDPI](https://github.com/romanvht/ByeByeDPI) that adds a Windows version.

DPI bypass for **Windows** and **Android**, built on [byedpi](https://github.com/hufrea/byedpi).

- **Windows:** a port of the app with a whole-PC VPN mode.
- **Android:** ByeByeDPI with defaults that work on networks where the stock ones don't.

Both were tuned on a line whose ISP blocks sites in two ways: fake DNS answers (every plain DNS query, to any server, comes back `0.0.0.0`), and DPI that resets or silently drops TLS handshakes by site name. On that line the setup here gets TikTok (video included), MyAnimeList, YouTube and Discord working.

## Windows app (`windows/`)

| ByeByeDPI (Android)            | Windows app                                                       |
|--------------------------------|-------------------------------------------------------------------|
| VpnService + hev-socks5-tunnel | Wintun adapter + tun2socks; takes the default route with 0/1 + 128/1 |
| App excluded from its own VPN  | byedpi bound (`-I`) to the real adapter's IP, so its traffic skips the tunnel |
| Proxy mode                     | SOCKS proxy, optionally set as the Windows proxy                  |
| UI editor / command line       | Same options (minus Linux-only ones: TFO, drop-SACK, md5sig)      |
| Proxy test                     | Same strategy and site lists; resolves over DoH                   |
| Quick tile / boot receiver     | Tray icon, "Open when Windows starts" (Task Scheduler, no UAC prompt) |

Extras:

- **Encrypted DNS:** in VPN mode a local resolver on `127.0.0.1:53` sends every query over DoH (default `https://1.1.1.1/dns-query`), which defeats fake DNS answers.
- **Auto-reconnect:** reconnects after a network change or an engine crash. Engines run in a kill-on-close job, so a crash never leaves the adapter or routes behind.
- **Default strategy:** `-Kt,h -d1 -s1+s -r1+s -An -Ku -a1 -An`.

Build (needs the .NET 9 SDK):

```powershell
.\windows\scripts\fetch-deps.ps1   # ciadpi.exe, tun2socks.exe, wintun.dll -> windows\deps\  (official releases)
.\windows\scripts\build.ps1        # self-contained app -> windows\dist\ByeDPI.exe
```

The app runs as admin; it needs that to create the network adapter and routes. Settings and logs are in `%AppData%\ByeDPI-PC`.

## Android app (repo root)

ByeByeDPI with these changes ([original readme](README-ru.md), [English](README-en.md)):

- **Default strategy:** `-d1 -s1+s -r1+s -a1`. The stock `-o1 -a1 -r-5+se` failed for both TikTok and MyAnimeList on the test line.
- **Own app id** (`io.github.romanvht.byedpi.mod`, shown as "ByeByeDPI Mod"), so it installs next to the official app.
- **Release builds signed with the local debug key**, so no keystore is needed for personal use.

If your ISP fakes DNS answers, set **Settings → Network & internet → Private DNS → `one.one.one.one`**. ByeByeDPI detects it and uses it.

Build (needs the Android SDK + NDK and JDK 21):

```bash
git submodule update --init --recursive
./gradlew assembleRelease
```

On Windows without Developer Mode, git checks the tunnel library's symlinked headers out as text stubs. Replace each with the file it points to before building.

## Credits

[ByeByeDPI](https://github.com/romanvht/ByeByeDPI) by romanvht (GPL-3.0) · [byedpi](https://github.com/hufrea/byedpi) by hufrea (MIT) · [hev-socks5-tunnel](https://github.com/heiher/hev-socks5-tunnel) (MIT) · [tun2socks](https://github.com/xjasonlyu/tun2socks) (GPL-3.0) · [Wintun](https://www.wintun.net)

Licensed GPL-3.0, like ByeByeDPI.
