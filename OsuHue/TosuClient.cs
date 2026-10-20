using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OsuHue;

public sealed record TosuFrame(string MapKey, double Time, double Rate, bool Playing, bool Paused, string Client, bool IgnoreMapColours, string SkinName, long ReceivedAt)
{
    public string Checksum { get; init; } = "";
    public string Title { get; init; } = "Beatmap";
    public double Age => Stopwatch.GetElapsedTime(ReceivedAt).TotalMilliseconds;
    public double CurrentTime => Time + (Paused ? 0 : Math.Min(Age, 200) * Rate);
}
public sealed record LoadedMap(string Key, Beatmap Map, Rgb[] Palette, string Source, bool UseMapOffsets);

public sealed class TosuClient : IAsyncDisposable
{
    public TosuFrame? Frame => Volatile.Read(ref frame);
    public LoadedMap? Loaded => Volatile.Read(ref loaded);
    public string Status => Volatile.Read(ref status);
    private TosuFrame? frame;
    private LoadedMap? loaded;
    private string status = "tosu noch nicht verbunden";
    private string? mapError;
    private long lastMapAttempt;
    private readonly HttpClient http = new(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(4) };
    private CancellationTokenSource? lifetime;
    private Task? worker;
    private sealed record PaletteOptions(string File, string Mode, int Generation);
    private PaletteOptions paletteOptions = new("", "Automatisch", 0);
    private readonly SemaphoreSlim connectionGate = new(1, 1);
    private bool disposed;

    public static Uri ValidateUrl(string input)
    {
        if (!Uri.TryCreate(input.Trim().TrimEnd('/') + "/", UriKind.Absolute, out var uri) || uri.Scheme != "http" || !uri.IsLoopback || uri.UserInfo.Length > 0 || uri.AbsolutePath != "/" || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            throw new ArgumentException("tosu muss lokal laufen, z. B. http://127.0.0.1:24050.");
        return uri;
    }

    public void SetPaletteOptions(string file, string mode)
    {
        PaletteOptions previous, next;
        do
        {
            previous = Volatile.Read(ref paletteOptions);
            next = new(file, mode, previous.Generation + 1);
        } while (Interlocked.CompareExchange(ref paletteOptions, next, previous) != previous);
    }

    public async Task ConnectAsync(string address)
    {
        var uri = ValidateUrl(address);
        await connectionGate.WaitAsync();
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            await DisconnectCoreAsync();
            lifetime = new();
            var token = lifetime.Token;
            worker = Task.Run(() => RunAsync(uri, token));
        }
        finally { connectionGate.Release(); }
    }

    private async Task RunAsync(Uri uri, CancellationToken cancel)
    {
        string? requestedKey = null;
        Task? loading = null;
        CancellationTokenSource? mapLifetime = null;
        try
        {
            while (!cancel.IsCancellationRequested)
            {
                try
                {
                    status = "Verbinde mit tosu …";
                    using var socket = new ClientWebSocket();
                    socket.Options.Proxy = null;
                    var ws = new UriBuilder(uri) { Scheme = "ws", Path = "/websocket/v2" }.Uri;
                    using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                    connectTimeout.CancelAfter(TimeSpan.FromSeconds(4));
                    await socket.ConnectAsync(ws, connectTimeout.Token);
                    var buffer = new byte[32768];
                    while (!cancel.IsCancellationRequested && socket.State == WebSocketState.Open)
                    {
                        using var message = new MemoryStream();
                        using var receiveTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                        receiveTimeout.CancelAfter(TimeSpan.FromSeconds(5));
                        WebSocketReceiveResult received;
                        do
                        {
                            received = await socket.ReceiveAsync(buffer, receiveTimeout.Token);
                            if (received.MessageType == WebSocketMessageType.Close)
                                throw new IOException("tosu hat die Verbindung geschlossen.");
                            message.Write(buffer, 0, received.Count);
                            if (message.Length > 4 * 1024 * 1024)
                                throw new IOException("tosu-Nachricht ist zu groß.");
                        } while (!received.EndOfMessage);
                        if (received.MessageType != WebSocketMessageType.Text)
                            continue;
                        using var json = JsonDocument.Parse(message.ToArray());
                        var root = json.RootElement;
                        if (root.TryGetProperty("error", out _))
                        {
                            mapLifetime?.Cancel();
                            frame = null;
                            loaded = null;
                            requestedKey = null;
                            status = "tosu läuft. Starte osu! und wähle eine Map.";
                            continue;
                        }
                        var options = Volatile.Read(ref paletteOptions);
                        var next = ParseFrame(root, options.Generation);
                        frame = next;
                        bool retry = loaded is null && Stopwatch.GetElapsedTime(lastMapAttempt).TotalMilliseconds > 3000;
                        if (next.MapKey != requestedKey || (retry && (loading is null || loading.IsCompleted)))
                        {
                            mapLifetime?.Cancel();
                            if (loading is not null)
                                await loading;
                            mapLifetime?.Dispose();
                            mapLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                            requestedKey = next.MapKey;
                            mapError = null;
                            lastMapAttempt = Stopwatch.GetTimestamp();
                            loaded = null;
                            loading = LoadMapAsync(uri, next, options.File, options.Mode, mapLifetime.Token);
                        }
                        status = mapError ?? $"tosu verbunden · osu! {next.Client}";
                    }
                }
                catch (OperationCanceledException) when (cancel.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    AppLog.Error("Read tosu data", ex);
                    mapLifetime?.Cancel();
                    frame = null;
                    loaded = null;
                    requestedKey = null;
                    status = ex is WebSocketException or HttpRequestException or OperationCanceledException
                        ? "tosu nicht erreichbar. Starte tosu; erneuter Versuch in 3 s."
                        : "tosu: " + ex.Message;
                    try
                    {
                        await Task.Delay(3000, cancel);
                    }
                    catch (OperationCanceledException) { break; }
                }
            }
        }
        finally
        {
            mapLifetime?.Cancel();
            if (loading is not null)
                await loading;
            mapLifetime?.Dispose();
        }
    }

    public static TosuFrame ParseFrame(JsonElement root, int generation = 0)
    {
        string checksum = Text(root, "beatmap", "checksum");
        string path = Text(root, "directPath", "beatmapFile");
        string skin = Text(root, "settings", "skin", "name");
        bool ignore = Flag(root, "settings", "skin", "ignoreBeatmapSkins");
        double rate = Number(root, "play", "mods", "rate");
        if (rate <= 0)
            rate = Number(root, "gameplay", "mods", "rate");
        if (rate <= 0)
            rate = 1;
        string client = Text(root, "client");
        string installation = Text(root, "folders", "game");
        string skinPath = Text(root, "folders", "skin");
        string key = $"{checksum}|{path}|{skin}|{ignore}|{client}|{installation}|{skinPath}|{generation}";
        int state = (int)Number(root, "state", "number");
        bool standard = Number(root, "play", "mode", "number") == 0 && Number(root, "beatmap", "mode", "number") == 0;
        return new(key, Number(root, "beatmap", "time", "live"), rate, state == 2 && standard, Flag(root, "game", "paused"), Text(root, "client"), ignore, skin, Stopwatch.GetTimestamp())
        {
            Checksum = checksum,
            Title = $"{Text(root, "beatmap", "artist")} — {Text(root, "beatmap", "title")} [{Text(root, "beatmap", "version")}]"
        };
    }

    private async Task LoadMapAsync(Uri uri, TosuFrame requested, string file, string mode, CancellationToken cancel)
    {
        try
        {
            Beatmap map;
            using (var response = await GetLocalAsync(uri, "api/v2/beatmap/timeline", cancel))
            {
                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    using var rawResponse = await GetLocalAsync(uri, "files/beatmap/file", cancel);
                    rawResponse.EnsureSuccessStatusCode();
                    var bytes = await rawResponse.Content.ReadAsByteArrayAsync(cancel);
                    if (requested.Checksum.Length == 32 && requested.Checksum.All(Uri.IsHexDigit)
                        && !Convert.ToHexString(MD5.HashData(bytes)).Equals(requested.Checksum, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("Die Map wurde während des Ladens gewechselt.");
                    map = Beatmap.Parse(Encoding.UTF8.GetString(bytes));
                }
                else
                {
                    response.EnsureSuccessStatusCode();
                    using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancel));
                    if (json.RootElement.GetProperty("checksum").GetString() != requested.Checksum)
                        throw new IOException("Die Map wurde während des Ladens gewechselt.");
                    map = Beatmap.FromTimeline(json.RootElement, requested.Title);
                }
            }
            string skinText = "";
            if (!string.IsNullOrWhiteSpace(file))
                skinText = await File.ReadAllTextAsync(file, cancel);
            else if (!requested.Client.Equals("lazer", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    using var skinResponse = await GetLocalAsync(uri, "files/skin/skin.ini", cancel);
                    skinResponse.EnsureSuccessStatusCode();
                    skinText = await skinResponse.Content.ReadAsStringAsync(cancel);
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { }
            }
            var skinPalette = Beatmap.ReadColours(skinText);
            var (palette, source) = SelectPalette(map.Colours, skinPalette, requested.IgnoreMapColours, mode);
            // A map/skin switch while the HTTP requests were running invalidates this result.
            if (!cancel.IsCancellationRequested && Frame?.MapKey == requested.MapKey)
                loaded = new(requested.MapKey, map, palette, source, ReferenceEquals(palette, map.Colours));
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
        catch (Exception ex)
        {
            AppLog.Error("Load beatmap", ex);
            if (Frame?.MapKey == requested.MapKey)
                mapError = "Map nicht lesbar: " + ex.Message + " · Neuer Versuch in 3 s.";
        }
    }

    public static (Rgb[] Palette, string Source) SelectPalette(Rgb[] map, Rgb[] skin, bool ignoreMap, string mode)
    {
        if (mode == "Beatmap" && map.Length > 0)
            return (map, "Beatmap-Farben");
        if (mode != "Skin" && mode != "Beatmap" && !ignoreMap && map.Length > 0)
            return (map, "Beatmap-Farben");
        if (skin.Length > 0)
            return (skin, "Skin-Farben");
        return (Beatmap.DefaultColours, "Classic-Standardfarben · ggf. skin.ini wählen");
    }

    private async Task<HttpResponseMessage> GetLocalAsync(Uri origin, string path, CancellationToken cancel)
    {
        var address = new Uri(origin, path);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeout.CancelAfter(TimeSpan.FromSeconds(4));
        for (int redirects = 0; redirects <= 3; redirects++)
        {
            var response = await http.GetAsync(address, timeout.Token);
            if ((int)response.StatusCode is not (301 or 302 or 303 or 307 or 308))
                return response;
            var location = response.Headers.Location;
            response.Dispose();
            if (location is null || redirects == 3)
                throw new IOException("Ungültige tosu-Weiterleitung.");
            address = new Uri(address, location);
            if (address.Scheme != origin.Scheme || address.Host != origin.Host || address.Port != origin.Port
                || address.UserInfo.Length > 0)
                throw new IOException("tosu darf Dateien nur von derselben lokalen Adresse laden.");
        }
        throw new IOException("Zu viele tosu-Weiterleitungen.");
    }

    public async Task DisconnectAsync()
    {
        await connectionGate.WaitAsync();
        try
        {
            await DisconnectCoreAsync();
        }
        finally { connectionGate.Release(); }
    }

    private async Task DisconnectCoreAsync()
    {
        if (lifetime is null)
            return;
        lifetime.Cancel();
        if (worker is not null)
            await worker;
        lifetime.Dispose();
        lifetime = null;
        frame = null;
        loaded = null;
        status = "tosu nicht verbunden";
    }

    public async ValueTask DisposeAsync()
    {
        await connectionGate.WaitAsync();
        try
        {
            if (disposed)
                return;
            disposed = true;
            try
            {
                await DisconnectCoreAsync();
            }
            finally { http.Dispose(); }
        }
        finally { connectionGate.Release(); }
    }
    private static JsonElement Property(JsonElement element, params string[] path)
    {
        foreach (var key in path)
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(key, out element))
                return default;
        return element;
    }
    private static string Text(JsonElement root, params string[] path)
    {
        var e = Property(root, path);
        return e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : "";
    }
    private static double Number(JsonElement root, params string[] path)
    {
        var e = Property(root, path);
        return e.ValueKind == JsonValueKind.Number && e.TryGetDouble(out var v) && double.IsFinite(v) ? v : 0;
    }
    private static bool Flag(JsonElement root, params string[] path) => Property(root, path).ValueKind == JsonValueKind.True;
}
