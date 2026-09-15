# Valheim PingHud

Shows the current server's **latency (ms)** and **packet loss (%)** on the HUD, styled to match
[Aidin's DayTimeCountdown](https://thunderstore.io/c/valheim/p/Aidin/DayTimeCountdown/) — same
font, outline and panel size. It sits **below the minimap** by default while DayTimeCountdown sits
above, and it slides out of the way automatically if the two would ever overlap.

```
┌──────────────────────────────┐  ← DayTimeCountdown
│ Day 128            13m 42s   │
└──────────────────────────────┘
              ┌───────┐
              │minimap│
              └───────┘
┌──────────────────────────────┐  ← PingHud
│ Ping 45 ms        Loss 0.0%  │
└──────────────────────────────┘
```

Interface languages: **English, Simplified Chinese, Traditional Chinese**. Any other game language
falls back to English.

## Read this first: what data each connection type actually has

Valheim has four socket implementations and they report *completely different* statistics. This is
the only reason you might see "0 ms / 100%" while the game plays fine.

| Socket | When | Latency | Loss / quality | Bandwidth |
|---|---|---|---|---|
| `ZSteamSocket` | Steam P2P (friend invite, server browser, crossplay off) | ✅ real | ✅ real | ✅ real |
| `ZPlayFabSocket` | **Crossplay**, via the PlayFab relay | ❌ hardcoded 0 | ❌ hardcoded 0 | ✅ real |
| `ZSocket2` | Direct IP / LAN (TCP) | ❌ hardcoded 0 | ❌ hardcoded 0 | ✅ real |
| `ZSteamSocketOLD` | Legacy Steam transport | ❌ all zero | ❌ all zero | ❌ all zero |

`ZPlayFabSocket` and `ZSocket2` inherit `ZNetStats`, whose `GetConnectionQuality()` always ends with:

```csharp
localQuality = 0f;   // latency and quality simply do not exist here
remoteQuality = 0f;
ping = 0;
outByteSec = m_sendRate;   // only bandwidth is real
inByteSec  = m_recvRate;
```

So `0 ms` + `100%` does not mean "everything is being dropped" — it means **that transport does not
expose latency or loss at all**. TCP does not surface loss to the application either; retransmission
happens in the kernel.

**Which one am I on?** Check `BepInEx\LogOutput.log`. `Connecting to server with PlayFab-backend` or
`ZPlayFabSocket` = crossplay; `ZSocket2` = direct IP; neither (joined via Steam invite/browser) = Steam P2P.

**Want a real loss figure?** Use a Steam P2P connection — turn Crossplay off and join by Steam
invite or the server browser.

### How each value is obtained

| Panel | Steam P2P | Crossplay / direct TCP | Source |
|---|---|---|---|
| Ping | `m_nPing` | the game's own RPC ping/pong | see below |
| Loss | `1 − m_flConnectionQualityLocal` | `N/A` — no source | Valve: packets delivered end-to-end in order |
| Jitter | std-dev of the last 32 samples | same | derived from ping samples |
| Quality | `m_flConnectionQualityLocal` | `N/A` | as above |
| Up / down | `m_flOut/InBytesPerSec` | same | `ZNetStats` byte counters |

`--` means "no reading yet"; `N/A` means "this connection type does not provide it". The panel never
invents a number.

> **Ping on non-Steam transports** comes from the game's own RPC ping/pong: `ZRpc` pings every second
> (`m_pingInterval = 1`) and zeroes `m_timeSinceLastPing` on reply, so subtracting it from
> `m_pingTimer` gives the round trip. The game only services the network once per frame, so the value
> is quantised to a frame (~16 ms at 60 fps); it is smoothed.
>
> **Loss** is derived from Valve's definition of `m_flConnectionQualityLocal` in
> `steamnetworkingtypes.h`: *"Percentage of packets delivered end-to-end in order"*. So `1 − quality`
> really is the loss rate.

## Install

**r2modman / Thunderstore Mod Manager** — find `PingHud` on Thunderstore, or use *Settings → Import
local mod* on this folder.

**Manual** — copy `dist/ValheimPingHud.dll` into:

```
<Valheim>\BepInEx\plugins\ValheimPingHud\ValheimPingHud.dll
```

Requires **BepInExPack Valheim** (BepInEx 5.4.x). No Jotunn or other dependencies.
To uninstall, delete the `ValheimPingHud` folder.

## Configuration

`BepInEx\config\kagegawa.valheim.pinghud.cfg`, generated on first run. Also editable in-game via
ConfigurationManager (F1) if installed.

**Position** — `Anchor` (`BelowMinimap` default, plus `AboveMinimap` and the four screen corners),
`Extra X/Y offset`, and auto-avoid: `Auto avoid other panels` with `Avoid panel names`
(`DayTimePanel` is DayTimeCountdown's panel). Overlap is measured per frame with
`RectTransform.GetWorldCorners`, so the two panels never collide regardless of which side
DayTimeCountdown is on. The minimap rect is measured live, with a constant fallback.

**Appearance** — `Panel width/height`, `Font name` (`auto` = a system CJK font for Chinese, the
game's own `AveriaSansLibre-Bold` for English), `Font size/color`, outline, background, padding.

**Display** — `Show ping`, `Show packet loss`, `Show extra details` (jitter/quality/bandwidth),
`Smooth values`, `Show text labels`, `Ping on the right`, `Color by quality` and its thresholds,
`Language` (`auto` / `Chinese` / `TraditionalChinese` / `English`).

**Other keys** — `F8` toggles the panel temporarily (Valheim itself uses F1, F2, F3, F9, F11).
The panel hides in single player and when the HUD is hidden.

## Build from source

```
src/
├── PingHudPlugin.cs      plugin, configuration, lifecycle
├── NetStatsSampler.cs    network sampling and smoothing
├── PingHudPanel.cs       the panel, positioning, auto-avoid
└── HudStrings.cs         en / zh-Hans / zh-Hant strings and number formatting
tools/verify-refs.ps1     static reference check of the compiled DLL
build.ps1                 compiles with the csc.exe that ships with Visual Studio
```

```powershell
.\build.ps1                      # compile (finds Valheim and BepInEx automatically)
.\build.ps1 -Deploy              # compile and install into the r2modman Default profile
.\tools\verify-refs.ps1          # check every referenced type/method/field resolves
```

`build.ps1` prefers Visual Studio's Roslyn `csc.exe` and compiles straight against the Mono
assemblies in `valheim_Data\Managed` (`-nostdlib+`), so **no .NET SDK is required**; it falls back to
`dotnet build` when a SDK is present. Paths can be given with `-ValheimDir`, `-BepInExCoreDir`,
`$env:VALHEIM_DIR` or `$env:BEPINEX_CORE_DIR`.

## Known limitations

- **Packet loss is a derived estimate**, for the reasons in the first section.
- When you are the **host or dedicated server**, `ZNet.GetNetStats` returns the average over all
  connected players, so the panel shows that average rather than any single player's connection.
- The panel is parented to `Hud.m_rootObject` and is rebuilt on scene changes.
- It only shows data while connected (including being a host with players present).
