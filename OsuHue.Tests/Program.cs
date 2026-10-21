using OsuHue;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

if (args is ["--probe-bridge", var bridgeIp])
{
    var bridge = await HueSession.ProbeAsync(bridgeIp);
    Console.WriteLine($"HTTPS probe passed: {bridge.Name}, model {bridge.Model}, IP {bridge.Ip}");
    var discovered = await HueSession.DiscoverAsync();
    Console.WriteLine("Discovery results: " + string.Join(", ", discovered));
    return;
}

if (args is ["--probe-tosu", var address])
{
    await using var live = new SyncEngine();
    await live.Tosu.ConnectAsync(address);
    int frames = 0, pulses = 0;
    string previous = "";
    for (int i = 0; i < 200; i++)
    {
        await Task.Delay(100);
        var current = live.Current;
        if (live.Tosu.Frame is { Age: < 500 })
            frames++;
        if (current.Playing && current.Pulse.Brightness > 0)
            pulses++;
        string state = $"{live.Tosu.Frame?.Client} | {current.Status} | {current.Source} | {live.Tosu.Status}";
        if (state != previous)
            Console.WriteLine(state);
        previous = state;
    }
    Console.WriteLine($"Fresh samples: {frames}; active pulse samples: {pulses}; palette: {string.Join(",", live.Current.Palette.Select(x => x.Hex))}");
    Environment.ExitCode = frames > 0 && live.Tosu.Loaded is not null ? 0 : 1;
    return;
}

var checks = new List<string>();
void Check(bool condition, string name)
{
    if (!condition)
        throw new Exception("FAIL: " + name);
    checks.Add(name);
    Console.WriteLine("PASS: " + name);
}
void Throws(Action action, string name)
{
    bool threw = false;
    try
    {
        action();
    }
    catch (ArgumentException) { threw = true; }
    catch (FormatException) { threw = true; }
    catch (InvalidOperationException) { threw = true; }
    Check(threw, name);
}

var demoOptions = new EffectOptions(.45, 180, 60);
var automaticTempos = new[] { 90, 120, 180, 240, 300, 90 };
for (int stage = 0; stage < automaticTempos.Length; stage++)
{
    var demoPreview = DemoSequence.GetPreview(stage * 8000, 0, demoOptions, true);
    Check(demoPreview.Title == $"Demo · {automaticTempos[stage]} BPM" && demoPreview.Pulse.Brightness == .45,
        $"automatic demo stage {stage} starts at {automaticTempos[stage]} BPM on a downbeat");
}
var fastDemo = DemoSequence.GetPreview(180, 300, demoOptions, true);
Check(Math.Abs(fastDemo.Pulse.Brightness - .45 / Math.E) < .001, "demo fade stays in real milliseconds at 300 BPM");
Check(DemoSequence.GetPreview(200, 300, demoOptions, true).Pulse.HitIndex == 1, "300 BPM demo pulses every 200 ms");
Check(DemoSequence.GetPreview(1000, 120, demoOptions, false).Status.StartsWith("Vorschau läuft ohne Lampen"), "unpaired demo explicitly identifies screen-only mode");
Throws(() => DemoSequence.ValidateTempo(500), "reject unsupported demo tempos");
await using (var demoEngine = new SyncEngine())
{
    var demoLights = new FakeHueSession();
    demoEngine.SetDemoTempo(300);
    await demoEngine.StartAsync(demoLights, demo: true);
    await WaitFor(() => demoLights.LastPulse is { Brightness: > 0 } && demoEngine.Current.Title == "Demo · 300 BPM",
        "demo sends coloured pulses to the light transport without tosu or osu");
    Check(demoEngine.Demo && demoEngine.IsStreaming, "demo and light stream remain active together");
    await demoEngine.StopAsync();
    Check(!demoEngine.Demo && !demoEngine.IsStreaming && demoLights.Disposals == 1, "demo stop restores lights and clears demo mode");
    await WaitFor(() => demoEngine.Current.Pulse.Brightness == 0, "stopping demo also stops its preview pulses");
    var brokenDemo = new FakeHueSession { FailSend = true };
    await demoEngine.StartAsync(brokenDemo, demo: true);
    await WaitFor(() => !demoEngine.IsStreaming && !demoEngine.Demo && brokenDemo.Disposals == 1,
        "demo transport failure restores lights and exits demo mode");
}
static string Map(string objects, string colours = "Combo1:255,0,0\nCombo2:0,255,0\nCombo3:0,0,255\nCombo4:255,255,0", string events = "", int version = 14, int mode = 0) =>
    $"osu file format v{version}\n[General]\nMode:{mode}\n[Metadata]\nArtist:Test\nTitle:Timing\nVersion:Hard\n[Colours]\n{colours}\n[Events]\n{events}\n[HitObjects]\n{objects}";

const string objects = "64,64,1000,5,0\n64,64,1500,1,0\n64,64,2000,38,0,L|100:100,1,100\n256,192,2500,12,0,2900\n64,64,3000,1,0\n64,64,4500,1,0";
string sample = Map(objects, events: "2,3500,4000");
var map = Beatmap.Parse(sample);
Check(map.Title == "Test — Timing [Hard]", "metadata");
Check(map.Hits.Length == 5, "circles + slider heads; no spinner impulses");
Check(map.Hits[0].ColourIndex == 1, "osu first combo is Combo2");
Check(map.Hits[1].ColourIndex == 1, "same combo retains colour");
Check(map.Hits[2].ColourIndex == 4, "new combo with skip=2 advances three colours");
Check(map.Hits[2].SkinColourIndex == 2, "custom skin ignores map colour skips");
Check(map.Hits[3].ColourIndex == 5, "spinner does not consume colour; next object starts combo");
Check(map.Hits[4].ColourIndex == 6, "first object after break starts combo");
Check(Beatmap.Parse(Map("64,64,0,5,0", version: 4)).Hits[0].Time == 24, "pre-v5 timing correction");
Check(Beatmap.Parse(Map("64,64,0,5,0", mode: 3)).Mode == 3, "other game modes are identified");
var ordered = Beatmap.Parse(Map("64,64,2000,1,0\n64,64,1000,5,0\n64,64,1000,1,0"));
Check(ordered.Hits[0].Time == 1000 && ordered.Hits[1].Time == 1000, "stable sorting of unordered objects");
var palette = Beatmap.ReadColours("\uFEFF[Colours]\nCombo2: 1,2,3 // comment\nCombo1:255,128,0\nCombo9:12,34,56\nCombo3:999,0,0\n[Mania]\nCombo4:255,255,255");
Check(palette.Length == 2 && palette[0] == new Rgb(255, 128, 0), "skin palette sections, ordering, comments and bounds");
Throws(() => Beatmap.Parse("<html>missing map</html>"), "reject HTML and malformed beatmap responses");
Throws(() => Beatmap.Parse(Map("")), "reject empty beatmaps");
Throws(() => Beatmap.Parse(Map("64,64,NaN,5,0")), "reject non-finite hitobject times");
Check(Beatmap.Parse(sample.Replace("Artist:Test", "Artist:A//B")).Title.StartsWith("A//B"), "metadata preserves double slashes in artist names");
Check(Beatmap.Parse(Map("256,192,100,124,0,200\n64,64,300,1,0")).Hits[0].ColourIndex == 1,
    "spinner combo-skip bits do not affect the next circle");
Check(map.FindHit(999) == -1 && map.FindHit(1000) == 0 && map.FindHit(1900) == 1, "binary search handles exact and pre-hit boundaries");
Check(map.FindHit(99999) == 4 && map.FindHit(1100) == 0, "seeking backward and forward remains correct");
Check(Pulse.At(map, map.Colours, 900, 1, 0, 180, .5).Brightness == 0, "no pulse before first object");
var pulse = Pulse.At(map, map.Colours, 1000, 1, 0, 180, .5);
Check(pulse.Colour == new Rgb(0, 255, 0) && pulse.Brightness == .5, "correct first colour and configured peak brightness");
Check(Math.Abs(Pulse.At(map, map.Colours, 1180, 1, 0, 180, 1).Brightness - 1 / Math.E) < 0.001, "exponential afterglow");
Check(Pulse.At(map, map.Colours, 940, 1, 60, 180, .5).HitIndex == 0, "positive offset brings pulse forward");
Check(Pulse.At(map, map.Colours, 1000, 1, -60, 180, .5).Brightness == 0, "negative offset delays pulse");
Check(Pulse.At(map, map.Colours, 910, 1.5, 60, 180, .5).HitIndex == 0, "DT offset is in real milliseconds");
Check(Pulse.At(map, map.Colours, 955, .75, 60, 180, .5).HitIndex == 0, "HT offset is in real milliseconds");
Check(Pulse.At(map, map.Colours, 2000, 1, 0, 180, .5, false).Colour == new Rgb(0, 0, 255), "skin pulse uses plain combo index");
Check(Pulse.At(map, map.Colours, 2000, 1, 0, 180, .5, true).Colour == new Rgb(255, 0, 0), "beatmap pulse applies colour skip");
Check(Pulse.At(map, map.Colours, 9000, 1, 0, 180, .5).Brightness == 0, "lights fade out in long gaps and after map");
Check(Pulse.At(map, map.Colours, double.NaN, 1, 0, 180, .5) == Pulse.Off, "invalid clock fails closed");
Check(Pulse.At(map, map.Colours, 1000, 1, 0, double.PositiveInfinity, .5) == Pulse.Off, "invalid effect settings fail closed");
var skin = new[] { new Rgb(255, 0, 255) };
Check(TosuClient.SelectPalette(map.Colours, skin, false, "Automatisch").Source == "Beatmap-Farben", "automatic beatmap palette");
Check(TosuClient.SelectPalette(map.Colours, skin, true, "Automatisch").Palette == skin, "ignore beatmap skins selects user palette");
Check(TosuClient.SelectPalette(map.Colours, skin, false, "Skin").Palette == skin, "manual skin override");
Check(TosuClient.SelectPalette(map.Colours, skin, true, "Beatmap").Palette == map.Colours, "manual beatmap override");
Check(TosuClient.SelectPalette([], [], true, "Automatisch").Palette.Length == 4, "fallback palette without skin.ini");
var argon = RulesetEffects.BuiltInComboColours("lazer", "osu! \"argon\" (2022)");
Check(argon.Length == 6 && argon[1] == new Rgb(0, 241, 53), "lazer Argon first combo uses its green, not classic green");
Check(RulesetEffects.BuiltInComboColours("lazer", "osu! \"argon\" pro (2022)").SequenceEqual(argon), "Argon Pro inherits Argon colours");
Check(RulesetEffects.BuiltInComboColours("stable", "osu! \"argon\" (2022)").Length == 0, "stable skin names do not trigger lazer built-in detection");
Check(RulesetEffects.BuiltInComboColours("lazer", "my edited argon skin").Length == 0, "custom skins never silently assume Argon colours");
var maniaMap = Beatmap.Parse(Map("0,192,1000,1,0\n512,192,1000,128,0,1500:0:0:0:0:", mode: 3).Replace("[Metadata]", "[Difficulty]\nCircleSize:4\n[Metadata]"));
Check(maniaMap.Hits.Length == 1 && maniaMap.Hits[0].SimultaneousColours!.SequenceEqual(new[] { 0, 3 }), "mania chords include notes and hold heads, clamping the right edge");
var maniaPalette = RulesetEffects.ManiaColours(4, true);
Check(maniaPalette.SequenceEqual(new[] { new Rgb(255, 197, 40), new Rgb(252, 109, 1), new Rgb(213, 35, 90), new Rgb(203, 60, 236) }), "Argon 4K column accents match the official layout");
var chordPulse = Pulse.At(maniaMap, maniaPalette, 1000, 1, 0, 180, .5);
Check(chordPulse.Colour == new Rgb(229, 128, 138) && chordPulse.Brightness == .5, "mania chord mixes its columns into one bounded pulse");
var taikoMap = Beatmap.Parse(Map("64,64,1000,1,0\n64,64,1500,1,2\n64,64,2000,1,8\n64,64,2500,1,4\n64,64,3000,2,0,L|100:100,1,100", mode: 1));
Check(taikoMap.Hits.Select(x => x.ColourIndex).SequenceEqual(new[] { 0, 1, 1, 0 }), "taiko whistle and clap are Kat, finish alone stays Don; rolls do not invent hits");
Check(Beatmap.Parse(Map(objects, mode: 2)).Hits.Length == 5, "catch emits fruit and juice-stream head pulses");
Throws(() => Beatmap.Parse(sample, 3), "reject unimplemented standard-to-mania conversion instead of inventing lanes");
Throws(() => Beatmap.Parse(Map("NaN,192,1000,1,0", mode: 3)), "reject invalid mania coordinates");
Check(RulesetEffects.ManiaColours(14, true).Take(7).SequenceEqual(RulesetEffects.ManiaColours(14, true).Skip(7)), "14K Argon repeats the 7K stage accents");
Throws(() => RulesetEffects.ManiaColours(19, true), "reject unsupported mania key counts");
Check(Beatmap.Parse(Map("0,192,1000,1,0", mode: 3)).Keys == 5, "mania without difficulty settings uses osu's 5K default");


Throws(() => TosuClient.ValidateUrl("http://example.com"), "reject remote tosu hosts");
Throws(() => TosuClient.ValidateUrl("http://127.0.0.1:24050/foo"), "reject invalid tosu paths");
Throws(() => TosuClient.ValidateUrl("https://127.0.0.1:24050"), "reject unsupported tosu protocol");
Check(TosuClient.ValidateUrl("http://localhost:24050").IsLoopback, "accept localhost tosu address");
Throws(() => HueSession.ValidateIp("8.8.8.8"), "Hue keys are restricted to local network IPs");
HueSession.ValidateIp("192.168.1.10");
Check(true, "private bridge addresses accepted");
using (var bridgeConfig = JsonDocument.Parse("{\"name\":\"Hue Bridge\",\"modelid\":\"BSB002\"}"))
    Check(HueSession.ParseBridgeInfo("192.168.1.20", bridgeConfig.RootElement).Model == "BSB002", "manual IP probe recognises a Hue v2 bridge");
using (var bridgeConfig = JsonDocument.Parse("{\"modelid\":\"BSB001\"}"))
    Throws(() => HueSession.ParseBridgeInfo("192.168.1.20", bridgeConfig.RootElement), "manual IP probe explains unsupported v1 bridge");
using (var bridgeConfig = JsonDocument.Parse("{\"name\":\"Printer\"}"))
    Throws(() => HueSession.ParseBridgeInfo("192.168.1.20", bridgeConfig.RootElement), "manual IP probe rejects unrelated network devices");
var settings = new Settings();
settings.SetCredentials("test-app-key", "test-client-key");
Check(!settings.ProtectedCredentials.Contains("test-app-key") && settings.GetCredentials()?.AppKey == "test-app-key", "DPAPI protects and recovers paired credentials");
settings.ProtectedCredentials = "broken";
Check(settings.GetCredentials() is null, "corrupt credentials trigger re-pairing");
string settingsPath = Path.Combine(Path.GetTempPath(), "OsuHue-QA-" + Guid.NewGuid().ToString("N") + ".json");
try
{
    File.WriteAllText(settingsPath, "{\"SkinFile\":null,\"BridgeIp\":null,\"TosuUrl\":\"http://example.com\",\"Brightness\":900,\"Fade\":-1,\"Offset\":999}");
    var repaired = Settings.Load(settingsPath);
    Check(repaired.SkinFile == "" && repaired.BridgeIp == "", "null settings cannot crash startup");
    Check(repaired.Brightness == 100 && repaired.Fade == 40 && repaired.Offset == 300, "out-of-range effects are normalized");
    Check(repaired.TosuUrl == "http://127.0.0.1:24050" && repaired.LoadWarning is not null, "invalid persisted URL falls back with explanation");
    repaired.Save(settingsPath);
    Check(Settings.Load(settingsPath).Brightness == 100, "atomic settings save round-trips");
    File.WriteAllText(settingsPath, "{invalid json");
    Check(Settings.Load(settingsPath).LoadWarning is not null, "corrupt settings produce a visible warning");
}
finally { File.Delete(settingsPath); }

await using (var lifecycle = new SyncEngine())
{
    var firstSession = new FakeHueSession();
    await lifecycle.StartAsync(firstSession);
    var rejected = new FakeHueSession();
    try
    {
        await lifecycle.StartAsync(rejected);
        Check(false, "reject duplicate stream");
    }
    catch (InvalidOperationException) { Check(!rejected.Started, "reject duplicate stream without replacing owner"); }
    await lifecycle.StopAsync();
    Check(firstSession.Disposals == 1 && !lifecycle.IsStreaming, "stop restores the active session once");
    var failedStart = new FakeHueSession { FailStart = true };
    try
    {
        await lifecycle.StartAsync(failedStart);
        Check(false, "failed start");
    }
    catch (IOException) { Check(failedStart.Disposals == 1, "failed stream startup always cleans up"); }
    var failedSend = new FakeHueSession { FailSend = true };
    await lifecycle.StartAsync(failedSend);
    var deadline = DateTime.UtcNow.AddSeconds(2);
    while (lifecycle.IsStreaming && DateTime.UtcNow < deadline)
        await Task.Delay(20);
    await Task.WhenAll(lifecycle.DisposeAsync().AsTask(), lifecycle.DisposeAsync().AsTask());
    Check(failedSend.Disposals == 1 && lifecycle.Error is not null, "send failure and concurrent close restore once without deadlock");
    try
    {
        await lifecycle.StartAsync(new FakeHueSession());
        Check(false, "disposed engine");
    }
    catch (ObjectDisposedException) { Check(true, "disposed engine cannot start another stream"); }
}
using (var timeline = JsonDocument.Parse(TimelineJson("timeline-map", 14)))
{
    var fromApi = Beatmap.FromTimeline(timeline.RootElement, "API map");
    Check(fromApi.Hits[0] == new HitCue(1000, 1, 1), "new timeline API uses the same combo progression");
    Check(fromApi.Colours[1] == new Rgb(128, 0, 255), "new timeline API reads RGB palette");
}
using (var timeline = JsonDocument.Parse(TimelineJson("timeline-map", 4)))
    Check(Beatmap.FromTimeline(timeline.RootElement, "old map").Hits[0].Time == 1024, "new timeline API applies v4 playback offset once");

// Real local HTTP + fragmented WebSocket integration, without osu! or Hue hardware.
var builder = WebApplication.CreateBuilder();
builder.Logging.ClearProviders();
builder.WebHost.UseUrls("http://127.0.0.1:0");
await using var server = builder.Build();
server.UseWebSockets();
string stateJson = FrameJson(1100, false, 2, "map-a");
bool transmit = true;
string mapResponse = sample;
bool serveTimeline = false;
string apiTimeline = TimelineJson("map-d", 14);
int fileRequests = 0;
int mapRedirect = 0;
int delayNextTimeline = 0;
var delayedRequest = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
server.MapGet("/files/beatmap/file", async context =>
{
    Interlocked.Increment(ref fileRequests);
    int redirect = Volatile.Read(ref mapRedirect);
    if (redirect != 0)
    {
        context.Response.Redirect(redirect == 1 ? "/mock/map.osu" : "http://example.com/map.osu", permanent: true);
        return;
    }
    context.Response.ContentType = "text/plain";
    await context.Response.WriteAsync(Volatile.Read(ref mapResponse));
});
server.MapGet("/mock/map.osu", async context => await context.Response.WriteAsync(Volatile.Read(ref mapResponse)));
server.MapGet("/api/v2/beatmap/timeline", async context =>
{
    if (!Volatile.Read(ref serveTimeline))
    {
        context.Response.StatusCode = 404;
        return;
    }
    string response = Volatile.Read(ref apiTimeline);
    if (Interlocked.Exchange(ref delayNextTimeline, 0) == 1)
    {
        delayedRequest.TrySetResult();
        await Task.Delay(5000, context.RequestAborted);
    }
    context.Response.ContentType = "application/json";
    await context.Response.WriteAsync(response);
});
server.MapGet("/files/skin/skin.ini", () => "[Colours]\nCombo1:255,0,0\nCombo2:0,255,0\nCombo3:0,0,255\nCombo4:255,255,0");
server.Map("/websocket/v2", async context =>
{
    using var ws = await context.WebSockets.AcceptWebSocketAsync();
    try
    {
        while (!context.RequestAborted.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            if (Volatile.Read(ref transmit))
            {
                var data = Encoding.UTF8.GetBytes(Volatile.Read(ref stateJson));
                int half = data.Length / 2;
                await ws.SendAsync(data.AsMemory(0, half), WebSocketMessageType.Text, false, context.RequestAborted);
                await ws.SendAsync(data.AsMemory(half), WebSocketMessageType.Text, true, context.RequestAborted);
            }
            await Task.Delay(30, context.RequestAborted);
        }
    }
    catch (Exception ex) when (ex is WebSocketException or OperationCanceledException) { }
});
await server.StartAsync();
string url = server.Urls.First();
await using var engine = new SyncEngine();
await engine.Tosu.ConnectAsync(url);
async Task WaitFor(Func<bool> predicate, string name, int milliseconds = 6000)
{
    var until = DateTime.UtcNow.AddMilliseconds(milliseconds);
    while (!predicate() && DateTime.UtcNow < until)
        await Task.Delay(20);
    Check(predicate(), name);
}
await WaitFor(() => engine.Current.Playing && engine.Current.Pulse.Brightness > 0, "fragmented WebSocket → HTTP beatmap → live preview");
Check(engine.Current.Pulse.Colour == new Rgb(0, 255, 0), "end-to-end first hit is green");
Volatile.Write(ref stateJson, FrameJson(1100, true, 2, "map-a"));
await WaitFor(() => !engine.Current.Playing && engine.Current.Pulse.Brightness == 0, "pause blacks out impulses");
Volatile.Write(ref stateJson, FrameJson(2050, false, 2, "map-a", 1.5));
await WaitFor(() => engine.Current.Playing && engine.Current.Pulse.HitIndex == 2, "resume/seek selects current slider head");
Check(engine.Tosu.Frame!.Rate == 1.5, "DT rate parsed from current tosu v2 play.mods");
Volatile.Write(ref stateJson, FrameJson(1100, false, 0, "map-a"));
await WaitFor(() => !engine.Current.Playing && engine.Current.Pulse.Brightness == 0, "menu state disables pulses");
Volatile.Write(ref stateJson, FrameJson(1100, false, 2, "map-a"));
await WaitFor(() => engine.Current.Playing, "gameplay restarts preview");
Volatile.Write(ref transmit, false);
await WaitFor(() => engine.Current.Pulse.Brightness == 0 && !engine.Current.Playing, "stale data (>500 ms) stops light output");
Volatile.Write(ref transmit, true);
await WaitFor(() => engine.Current.Playing, "preview recovers after data resumes");
engine.Tosu.SetPaletteOptions("", "Skin");
Volatile.Write(ref stateJson, FrameJson(2050, false, 2, "map-a"));
await WaitFor(() => engine.Current.Source == "Skin-Farben" && engine.Current.Pulse.Colour == new Rgb(0, 0, 255), "palette option reloads current map; ignores combo skips");
Volatile.Write(ref stateJson, FrameJson(2050, false, 2, "map-b"));
Volatile.Write(ref mapResponse, Map("64,64,2000,5,0", "Combo1:255,255,255\nCombo2:128,0,255"));
engine.Tosu.SetPaletteOptions("", "Beatmap");
await WaitFor(() => engine.Current.Pulse.Colour == new Rgb(128, 0, 255), "map switch loads new palette");
Volatile.Write(ref mapResponse, "<html>not a beatmap</html>");
Volatile.Write(ref stateJson, FrameJson(2050, false, 2, "map-c"));
await WaitFor(() => engine.Tosu.Status.StartsWith("Map nicht lesbar"), "invalid map has visible error");
Check(engine.Current.Pulse.Brightness == 0, "failed map never reuses previous map lights");
Volatile.Write(ref mapResponse, sample);
await WaitFor(() => engine.Current.Playing, "map fetch retries after transient failure", 8000);
string sampleChecksum = Convert.ToHexString(System.Security.Cryptography.MD5.HashData(Encoding.UTF8.GetBytes(sample))).ToLowerInvariant();
Volatile.Write(ref mapRedirect, 1);
Volatile.Write(ref stateJson, FrameJson(1100, false, 2, sampleChecksum));
await WaitFor(() => engine.Tosu.Frame?.Checksum == sampleChecksum && engine.Current.Playing,
    "fallback follows local redirects and verifies original bytes against the osu checksum");
Volatile.Write(ref stateJson, FrameJson(1100, false, 2, new string('0', 32)));
await WaitFor(() => engine.Tosu.Status.StartsWith("Map nicht lesbar"), "fallback rejects a stale raw beatmap checksum");
Check(engine.Current.Pulse.Brightness == 0, "stale raw beatmap cannot drive lights");
Volatile.Write(ref mapRedirect, 2);
Volatile.Write(ref stateJson, FrameJson(1100, false, 2, "external-redirect"));
await WaitFor(() => engine.Tosu.Status.Contains("derselben lokalen Adresse"), "reject redirects outside the local tosu origin");
Check(engine.Current.Pulse.Brightness == 0, "rejected redirects cannot drive lights");
Volatile.Write(ref serveTimeline, true);
int beforeApi = Volatile.Read(ref fileRequests);
Volatile.Write(ref mapResponse, "this raw file must not be fetched");
Volatile.Write(ref stateJson, FrameJson(1100, false, 2, "map-d"));
await WaitFor(() => engine.Current.Playing && engine.Current.Title == " —  []", "new timeline endpoint feeds the live engine");
Check(engine.Current.Pulse.Colour == new Rgb(128, 0, 255), "new endpoint colour reaches preview");
Check(Volatile.Read(ref fileRequests) == beforeApi, "new tosu endpoint avoids a second raw map download");
Volatile.Write(ref stateJson, FrameJson(1100, false, 2, "map-e"));
await WaitFor(() => engine.Tosu.Frame?.Checksum == "map-e" && engine.Tosu.Status.StartsWith("Map nicht lesbar"), "mismatched timeline checksum is rejected");
await WaitFor(() => engine.Current.Pulse.Brightness == 0, "stale timeline never lights the new map");
Volatile.Write(ref apiTimeline, TimelineJson("map-e", 14));
await WaitFor(() => engine.Current.Playing, "timeline recovers after checksum matches", 8000);
Volatile.Write(ref delayNextTimeline, 1);
Volatile.Write(ref apiTimeline, TimelineJson("slow-map", 14));
Volatile.Write(ref stateJson, FrameJson(1100, false, 2, "slow-map"));
await delayedRequest.Task.WaitAsync(TimeSpan.FromSeconds(3));
Volatile.Write(ref apiTimeline, TimelineJson("fast-map", 14));
Volatile.Write(ref stateJson, FrameJson(1100, false, 2, "fast-map"));
await WaitFor(() => engine.Tosu.Frame?.Checksum == "fast-map" && engine.Current.Playing,
    "map switch cancels slow HTTP loading instead of waiting for timeout", 1500);
Volatile.Write(ref serveTimeline, false);
Volatile.Write(ref mapRedirect, 0);
engine.Tosu.SetPaletteOptions("", "Automatisch");
Volatile.Write(ref mapResponse, Map("64,64,1000,5,0", ""));
Volatile.Write(ref stateJson, FrameJson(1100, false, 2, "lazer-argon", clientName: "lazer", skinName: "osu! \"argon\" (2022)"));
await WaitFor(() => engine.Current.Playing && engine.Current.Pulse.Colour == new Rgb(0, 241, 53), "lazer released-tosu fallback loads Argon and emits real engine pulses");
Volatile.Write(ref serveTimeline, true);
for (int ruleset = 1; ruleset <= 3; ruleset++)
{
    Volatile.Write(ref mapResponse, Map("64,64,1000,1,0", mode: ruleset));
    Volatile.Write(ref stateJson, FrameJson(1100, false, 2, "lazer-mode-" + ruleset, clientName: "lazer", skinName: "osu! \"argon\" (2022)", mode: ruleset));
    int expected = ruleset;
    await WaitFor(() => engine.Current.Playing && engine.Tosu.Loaded?.Map.Mode == expected && engine.Current.Pulse.Brightness > 0,
        $"lazer {RulesetEffects.Name(ruleset)} flows through WebSocket, raw map and pulse engine");
}
Volatile.Write(ref stateJson, FrameJson(1100, true, 2, "lazer-mode-3", clientName: "lazer", skinName: "osu! \"argon\" (2022)", mode: 3));
await WaitFor(() => !engine.Current.Playing && engine.Current.Pulse.Brightness == 0, "lazer mania pause stops pulses");
Volatile.Write(ref stateJson, FrameJson(1100, false, 2, "lazer-mode-3", clientName: "lazer", skinName: "osu! \"argon\" (2022)", mode: 3));
await WaitFor(() => engine.Current.Playing, "lazer mania resume recovers pulses");
await Task.WhenAll(engine.Tosu.ConnectAsync(url), engine.Tosu.ConnectAsync(url));
await WaitFor(() => engine.Current.Playing, "concurrent reconnects retain one healthy connection");
await engine.DisposeAsync();
await engine.DisposeAsync();
Check(true, "regression: repeated disposal completes without disposed-token error");
await server.StopAsync();
Console.WriteLine($"\n{checks.Count} checks passed. Hue hardware was not used.");

static string FrameJson(double time, bool paused, int state, string checksum, double rate = 1, string clientName = "stable", string skinName = "Test Skin", int mode = 0) => JsonSerializer.Serialize(new
{
    game = new
    {
        paused
    },
    client = clientName,
    state = new
    {
        number = state
    },
    settings = new
    {
        skin = new
        {
            name = skinName,
            ignoreBeatmapSkins = false
        }
    },
    beatmap = new
    {
        checksum,
        time = new
        {
            live = time
        },
        mode = new
        {
            number = mode
        }
    },
    play = new
    {
        mods = new
        {
            rate
        },
        mode = new
        {
            number = mode
        }
    },
    directPath = new
    {
        beatmapFile = checksum + ".osu"
    }
});

static string TimelineJson(string checksum, int fileFormat) => JsonSerializer.Serialize(new
{
    checksum,
    fileFormat,
    mode = 0,
    comboColours = new[] { new { red = 255, green = 255, blue = 255 }, new { red = 128, green = 0, blue = 255 } },
    breaks = Array.Empty<object>(),
    hitObjects = new[] { new { startTime = 1000, type = 5 } }
});

sealed class FakeHueSession : IHueSession
{
    public bool FailStart
    {
        get; init;
    }
    public bool FailSend
    {
        get; init;
    }
    public bool Started
    {
        get; private set;
    }
    public int Disposals;
    public Pulse? LastPulse;
    public Task StartAsync()
    {
        Started = true;
        return FailStart ? Task.FromException(new IOException("Test startup failure")) : Task.CompletedTask;
    }
    public void Send(Pulse pulse)
    {
        if (FailSend)
            throw new IOException("Test send failure");
        Volatile.Write(ref LastPulse, pulse);
    }
    public ValueTask DisposeAsync()
    {
        Interlocked.Increment(ref Disposals);
        return ValueTask.CompletedTask;
    }
}
