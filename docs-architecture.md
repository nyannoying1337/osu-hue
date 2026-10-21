# osu!hue

Native WPF app for Windows x64. Reads the current beatmap in any of the four rulesets and
clock from local tosu v2 and streams combo-coloured pulses to a Hue
Entertainment area. Supports stable and lazer.

The companion remains a separate program. UI, Hue device control and effects
live here; the optional tosu contribution exports generic beatmap data only.
Released tosu versions continue to work through the existing raw-file fallback.

## UI

`App.xaml` defines shared dark control templates, spacing, colour resources
and visible keyboard-focus adorners. `MainWindow.xaml` has one page scroll
region and a persistent tempo/start/stop toolbar. `MainWindow.Layout.cs`
switches between two columns and one column below 940 device-independent
pixels, without moving controls out of their namescope or rebuilding them.
The minimum supported window width is 640 device-independent pixels.
Connection, streaming and parsing logic stay outside the layout partial.

## Build

Requires the .NET 10 SDK on Windows.

```powershell
dotnet run --project OsuHue.Tests -c Release
dotnet publish OsuHue/OsuHue.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false -o publish
```

Tests use a local in-process HTTP/WebSocket server. They do not contact or
control any Hue hardware and do not start osu! or tosu. Test credentials
are protected/unprotected in memory only.

## Design

- `Beatmap.cs`: osu! parsing, combo indices and pulse envelope. Palette
  indexing starts at Combo2. Map offsets apply to beatmap palettes; skin
  palettes use the plain combo index. Spinners do not consume colours.
- `TosuClient.cs`: fragmented WebSocket reception, reconnect, HTTP map/skin
  loading, stale-map rejection, retry and palette selection. Prefers the
  proposed `/api/v2/beatmap/timeline`; a 404 falls back to the existing
  raw-map endpoint. Only loopback
  HTTP endpoints are accepted.
- `SyncEngine.cs`: 50 Hz render/send loop, 500 ms stale-data cutoff,
  bounded clock extrapolation, pause/menu handling and idempotent disposal.
- `DemoSequence.cs`: deterministic 90/120/180/240/300 BPM cycle (8 seconds
  per stage) or fixed tempo. Connected demos drive the same Hue transport
  as gameplay; unpaired demos explicitly stay on screen. Both stop actions
  release the session and restore the saved lamp state. No osu!/tosu is required.
- `HueSession.cs`: pairing, Entertainment areas, pre-stream light snapshot,
  certificate-validated HTTPS activation and restoration on stop.
  `HueCertificateValidator.cs` verifies a chain to the bundled Hue roots,
  certificate validity/server purpose and the paired Bridge ID. DHCP address
  mismatch is expected because Hue certificates name the Bridge ID.
  `HueStreamingTransport.cs` uses Bouncy Castle DTLS 1.2 PSK with
  AES-128-GCM-SHA256. It deliberately avoids the SDK streaming client's
  certificate-bypassing REST path; SDK layer/colour conversion remains in use.
- `Settings.cs`: per-user settings and DPAPI-protected credentials.
- `AppLog.cs`: bounded local error log without credential-bearing exception
  messages. Unreadable settings show a warning; numeric effects and the local
  URL are validated on load. Settings save atomically through a unique file.
- `TosuInstaller.cs`: optional official tosu download, pinned SHA-256,
  archive and executable verification (including cached installs), bounded
  download/extraction sizes and explicit launch with automatic updates disabled.
  tosu is not bundled in this source.

## Verification limits

Automated tests cover game data and lifecycle. Real Bridge DTLS startup,
demo streaming and successful stop/restoration responses were checked on
Windows. Physical colour and timing have not been measured. lazer
skin retrieval and precoloured textures are
not implemented for arbitrary custom textures. Built-in Argon palettes and
Mania accents are supported. No audio analysis or spinner effects; standard
slider ticks/repeats are omitted, while Taiko converts short sliders into hits.

Lifecycle tests inject `IHueSession` to exercise startup failures, transport
failures, concurrent close, and ownership without physical lights. Map/skin
options are captured atomically. Switching maps cancels the previous request;
both the proposed timeline and the raw-file fallback reject stale checksums.
Disconnect and dispose serialize against reconnect, and the render loop runs
off the WPF dispatcher. Closing still stops the stream if settings cannot save.

## Dependencies and protocol references

- HueApi.Entertainment / HueApi.ColorConverters / HueApi 3.3.0, MIT:
  https://github.com/michielpost/Q42.HueApi
- Portable.BouncyCastle 1.9.0, Bouncy Castle licence (MIT style).
- .NET/WPF 10.0.13, MIT and third-party notices.
- tosu API: https://github.com/tosuapp/tosu/wiki
- osu! combo behaviour and tests:
  https://github.com/ppy/osu/blob/master/osu.Game.Rulesets.Osu/Objects/OsuHitObject.cs
  https://github.com/ppy/osu/blob/master/osu.Game.Rulesets.Osu.Tests/TestSceneLegacyBeatmapSkin.cs

The app has no telemetry. Hue discovery and the optional tosu download
use internet access; gameplay data and Hue control stay local.

## Branding

Display name: `osu!hue`, an independent community tool. The heading uses
plain text; no osu! cookie logo is included, recreated, modified or animated.
The app icon is an independent ribbon-shaped h mark. The animated circle is a
gameplay preview, not a logo. Existing executable/config identifiers retain
`OsuHue` for compatibility. Reviewed against:
https://osu.ppy.sh/wiki/en/Brand_identity_guidelines

## Ruleset effects

The active ruleset is part of the map cache identity. Native Taiko and Mania
load the raw beatmap endpoint even when a timeline API exists: hit sounds
and X coordinates are needed for Don/Kat and column mapping. The upstream
tosu API contribution stays generic and unchanged. Catch uses the existing
combo progression for fruit/juice-stream heads. Nested objects are not
synthesised. Mania groups simultaneous notes once during loading and mixes
their column accents during rendering. This adds no map scan to the 50-Hz loop.
Unsupported conversions fail closed instead of reusing the source map's mode.

## Localisation and branding

Translations.json contains English/German text and formatted message templates.
UiText translates at the presentation boundary and updates WPF dynamic resources
on language changes. Incoming data, beatmap titles, paths and persisted palette
IDs remain unchanged. Bounded caching avoids repeated template matching in the
preview refresh. A language change refreshes labels, status and area display
without disconnecting tosu or Hue. External OS/library diagnostics may retain
their original language. The optional Pillow icon script generates SVG, PNG
and ICO assets from one ribbon geometry; no graphics dependency ships at runtime.

## Taiko conversion

A source Mode:0 file can be played as Taiko in lazer/stable. ParseFrame uses
the active gameplay mode, or the selected menu mode outside gameplay, so
changing between selection and play does not switch a convert back to osu!.
TaikoConversion follows the short-slider conversion branch in ppy/osu's
TaikoBeatmapConverter (MIT notice included). Red timing, inherited SV,
version-dependent thresholds, integer duration truncation, repeats and node
hitsounds determine converted notes. Preserve its float promotion and operation
order at the two-beat boundary. Long sliders/drum rolls and swells stay silent.
A conversion cue limit rejects pathological maps before unbounded allocation.
Load errors appear in the main preview before pause/loading status, while
the current frame retains the map title. Failed retries retain their reason.
