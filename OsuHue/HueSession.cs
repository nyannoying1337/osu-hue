using HueApi.ColorConverters;
using HueApi.Entertainment;
using HueApi.Entertainment.Models;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OsuHue;

public sealed record HueArea(Guid Id, string Name, int Channels)
{
    public override string ToString() => $"{Name} · {Channels} Lichtkanäle";
}

public sealed record HueBridgeInfo(string Ip, string Name, string Model);

public sealed class HueSession : IHueSession
{
    private readonly HttpClient rest;
    private readonly StreamingHueClient client;
    private readonly Guid areaId;
    private StreamingGroup? group;
    private EntertainmentLayer? layer;
    private readonly List<(Guid Id, JsonObject State)> before = [];
    private bool activated;
    private Task? disposeTask;
    private readonly object disposeGate = new();
    public int ChannelCount => layer?.Count ?? 0;

    public HueSession(string ip, string appKey, string clientKey, Guid area)
    {
        ValidateIp(ip);
        rest = Rest(ip, appKey);
        client = new StreamingHueClient(ip, appKey, clientKey);
        areaId = area;
    }

    public static void ValidateIp(string ip)
    {
        if (!IPAddress.TryParse(ip, out var address) || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            throw new ArgumentException("Bitte die lokale IPv4-Adresse der Hue Bridge eingeben, z. B. 192.168.1.20.");
        var b = address.GetAddressBytes();
        if (!(b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254)))
            throw new ArgumentException("Die Bridge muss eine lokale Netzwerkadresse haben.");
    }

    private static HttpClient Rest(string ip, string? key = null)
    {
        ValidateIp(ip);
        // Hue bridges use local/self-signed certificates. This exception is scoped
        // to this single bridge client; internet requests keep normal validation.
        var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator, UseProxy = false, AllowAutoRedirect = false };
        var http = new HttpClient(handler) { BaseAddress = new Uri($"https://{ip}/"), Timeout = TimeSpan.FromSeconds(6) };
        if (key is not null)
            http.DefaultRequestHeaders.Add("hue-application-key", key);
        return http;
    }

    public static async Task<string[]> DiscoverAsync()
    {
        // Public discovery can return an empty list even when the local bridge
        // is reachable. Search the LAN independently instead of relying on it.
        var results = await Task.WhenAll(FindCloudAsync(), FindLocalAsync());
        return results.SelectMany(x => x).Distinct().ToArray();
    }

    private static async Task<string[]> FindCloudAsync()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var json = await http.GetFromJsonAsync<JsonElement>("https://discovery.meethue.com/");
            return json.EnumerateArray().Select(x => x.GetProperty("internalipaddress").GetString() ?? "").Where(x => x.Length > 0).ToArray();
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException) { return []; }
    }

    private static async Task<string[]> FindLocalAsync()
    {
        try
        {
            var bridges = await new HueApi.BridgeLocator.SsdpBridgeLocator().LocateBridgesAsync(TimeSpan.FromSeconds(4));
            return bridges.Select(x => x.IpAddress).Where(x => !string.IsNullOrEmpty(x)).ToArray();
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or HttpRequestException or OperationCanceledException) { return []; }
    }

    public static HueBridgeInfo ParseBridgeInfo(string ip, JsonElement config)
    {
        ValidateIp(ip);
        if (!config.TryGetProperty("modelid", out var model) || model.ValueKind != JsonValueKind.String ||
            !(model.GetString() ?? "").StartsWith("BSB", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Unter {ip} antwortet keine erkennbare Philips Hue Bridge.");
        if (model.GetString() == "BSB001")
            throw new InvalidOperationException("Die runde Hue Bridge v1 unterstützt Hue Entertainment nicht. Dafür wird eine neuere Bridge benötigt.");
        var name = config.TryGetProperty("name", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        return new(ip, name ?? "Hue Bridge", model.GetString()!);
    }

    public static async Task<HueBridgeInfo> ProbeAsync(string ip)
    {
        using var http = Rest(ip);
        using var response = await http.GetAsync("api/config");
        response.EnsureSuccessStatusCode();
        using var config = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return ParseBridgeInfo(ip, config.RootElement);
    }

    public static async Task<(string AppKey, string ClientKey)> PairAsync(string ip)
    {
        using var http = Rest(ip);
        using var response = await http.PostAsJsonAsync("api", new
        {
            devicetype = "OsuHue#Windows",
            generateclientkey = true
        });
        response.EnsureSuccessStatusCode();
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        var result = json?[0];
        if (result?["error"] is JsonNode error)
        {
            if (error["type"]?.GetValue<int>() == 101)
                throw new InvalidOperationException("Drücke zuerst den runden Knopf auf der Bridge. Klicke danach innerhalb von 30 Sekunden auf „Koppeln“.");
            throw new InvalidOperationException(error["description"]?.GetValue<string>() ?? "Die Bridge hat die Kopplung abgelehnt.");
        }
        return (result?["success"]?["username"]?.GetValue<string>() ?? throw new InvalidOperationException("Kein App-Schlüssel erhalten."),
            result?["success"]?["clientkey"]?.GetValue<string>() ?? throw new InvalidOperationException("Kein Streaming-Schlüssel erhalten."));
    }

    public static async Task<HueArea[]> GetAreasAsync(string ip, string key)
    {
        using var http = Rest(ip, key);
        var data = await GetData(http, "entertainment_configuration");
        return data.Select(x => new HueArea(Guid.Parse(x!["id"]!.GetValue<string>()), x["metadata"]?["name"]?.GetValue<string>() ?? "Entertainment-Bereich", x["channels"]?.AsArray().Count ?? 0)).ToArray();
    }

    private static async Task<JsonArray> GetData(HttpClient http, string path)
    {
        using var response = await http.GetAsync("clip/v2/resource/" + path);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new InvalidOperationException("Die Hue-Kopplung ist nicht mehr gültig. Bitte erneut koppeln.");
        response.EnsureSuccessStatusCode();
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        if (json["errors"] is JsonArray { Count: > 0 } errors)
            throw new InvalidOperationException("Hue: " + errors[0]?["description"]?.GetValue<string>());
        return json["data"]?.AsArray() ?? throw new InvalidOperationException("Die Bridge unterstützt diese Entertainment-Schnittstelle nicht.");
    }

    private async Task PutAsync(string path, object body)
    {
        using var response = await rest.PutAsJsonAsync("clip/v2/resource/" + path, body);
        response.EnsureSuccessStatusCode();
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        if (json?["errors"] is JsonArray { Count: > 0 } errors)
            throw new InvalidOperationException("Hue: " + errors[0]?["description"]?.GetValue<string>());
    }

    public async Task StartAsync()
    {
        var data = await GetData(rest, $"entertainment_configuration/{areaId}");
        var area = data.FirstOrDefault() ?? throw new InvalidOperationException("Der Entertainment-Bereich wurde nicht gefunden.");
        if (area["status"]?.GetValue<string>() == "active")
            throw new InvalidOperationException("Dieser Bereich wird bereits verwendet. Beende Hue Sync bzw. die andere Licht-Synchronisierung zuerst.");
        var channels = area["channels"]?.Deserialize<List<HueApi.Models.EntertainmentChannel>>() ?? [];
        if (channels.Count == 0)
            throw new InvalidOperationException("Der Entertainment-Bereich enthält keine Lampen.");
        group = new StreamingGroup(channels);
        layer = group.GetNewLayer(isBaseLayer: true);
        await SnapshotAsync(area);
        activated = true; // Also clean up if REST activation succeeds but DTLS fails.
        var connect = Task.Run(() => client.ConnectAsync(areaId));
        try
        {
            await connect.WaitAsync(TimeSpan.FromSeconds(12));
        }
        catch (HueEntertainmentException)
        {
            // HueApi 3.3.0 uses this exception when REST activation is rejected.
            // Do not stop a stream another application acquired in the meantime.
            activated = false;
            throw;
        }
        catch
        {
            client.Dispose();
            _ = connect.ContinueWith(task => AppLog.Error("Hue handshake after timeout", task.Exception!),
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            throw;
        }
        Send(Pulse.Off);
    }

    private async Task SnapshotAsync(JsonNode area)
    {
        var serviceIds = area["channels"]!.AsArray().SelectMany(x => x!["members"]!.AsArray()).Select(x => x!["service"]!["rid"]!.GetValue<string>()).ToHashSet();
        var services = await GetData(rest, "entertainment");
        var devices = services.Where(x => serviceIds.Contains(x!["id"]!.GetValue<string>())).Select(x => x!["owner"]!["rid"]!.GetValue<string>()).ToHashSet();
        var lights = await GetData(rest, "light");
        foreach (var light in lights.Where(x => devices.Contains(x!["owner"]!["rid"]!.GetValue<string>())))
        {
            var state = new JsonObject { ["on"] = light!["on"]?.DeepClone() };
            if (light["dimming"]?["brightness"] is JsonNode brightness)
                state["dimming"] = new JsonObject { ["brightness"] = brightness.DeepClone() };
            if (light["color_temperature"]?["mirek_valid"]?.GetValue<bool>() == true && light["color_temperature"]?["mirek"] is JsonNode mirek)
                state["color_temperature"] = new JsonObject { ["mirek"] = mirek.DeepClone() };
            else if (light["color"]?["xy"] is JsonNode xy)
                state["color"] = new JsonObject { ["xy"] = xy.DeepClone() };
            before.Add((Guid.Parse(light["id"]!.GetValue<string>()), state));
        }
        if (before.Count == 0)
            throw new InvalidOperationException("Der Lampenzustand konnte nicht gesichert werden. Aktualisiere den Entertainment-Bereich in der Hue App.");
    }

    // Called only on the render loop, avoiding concurrent layer mutation and send.
    public void Send(Pulse pulse)
    {
        if (layer is null || group is null)
            return;
        var colour = new RGBColor(pulse.Colour.R / 255d, pulse.Colour.G / 255d, pulse.Colour.B / 255d);
        foreach (var light in layer)
        {
            light.State.SetRGBColor(colour);
            light.State.SetBrightness(pulse.Brightness);
        }
        client.ManualUpdate(group);
    }

    public ValueTask DisposeAsync()
    {
        lock (disposeGate)
            return new(disposeTask ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        try
        {
            client.Dispose();
        }
        catch (Exception ex) { AppLog.Error("Close Hue transport", ex); }
        try
        {
            if (activated)
            {
                await PutAsync($"entertainment_configuration/{areaId}", new
                {
                    action = "stop"
                });
                var failures = new List<string>();
                foreach (var (id, state) in before)
                {
                    try
                    {
                        await PutAsync($"light/{id}", state);
                    }
                    catch (Exception ex) { failures.Add(ex.Message); }
                }
                if (failures.Count > 0)
                    throw new InvalidOperationException("Einige Lampen konnten nicht zurückgesetzt werden: " + failures[0]);
            }
        }
        finally { rest.Dispose(); }
    }
}
