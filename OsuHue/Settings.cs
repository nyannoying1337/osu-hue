using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OsuHue;

public sealed class Settings
{
    public string Language { get; set; } = "en";
    public string BridgeIp { get; set; } = "";
    public string ProtectedCredentials { get; set; } = "";
    public string AreaId { get; set; } = "";
    public string TosuUrl { get; set; } = "http://127.0.0.1:24050";
    public string SkinFile { get; set; } = "";
    public string ColourSource { get; set; } = "Automatisch";
    public double Brightness { get; set; } = 45;
    public double Fade { get; set; } = 180;
    public double Offset { get; set; } = 60;
    public static string DataFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OsuHue");
    public static string FilePath => Path.Combine(DataFolder, "settings.json");
    [System.Text.Json.Serialization.JsonIgnore]
    public string? LoadWarning
    {
        get; private set;
    }

    public static Settings Load(string? path = null)
    {
        try
        {
            var result = JsonSerializer.Deserialize<Settings>(File.ReadAllText(path ?? FilePath))
                ?? throw new JsonException("Empty settings document.");
            result.Normalize();
            return result;
        }
        catch (FileNotFoundException) { return new(); }
        catch (DirectoryNotFoundException) { return new(); }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            AppLog.Error("Load settings", ex);
            return new()
            {
                LoadWarning = "Einstellungen konnten nicht gelesen werden. Standardwerte sind aktiv; kopple die Bridge bei Bedarf erneut."
            };
        }
    }

    private void Normalize()
    {
        Language = Language == "de" ? "de" : "en";
        BridgeIp ??= "";
        ProtectedCredentials ??= "";
        AreaId ??= "";
        SkinFile ??= "";
        ColourSource = ColourSource is "Skin" or "Beatmap" ? ColourSource : "Automatisch";
        Brightness = double.IsFinite(Brightness) ? Math.Clamp(Brightness, 1, 100) : 45;
        Fade = double.IsFinite(Fade) ? Math.Clamp(Fade, 40, 600) : 180;
        Offset = double.IsFinite(Offset) ? Math.Clamp(Offset, -300, 300) : 60;
        try
        {
            TosuUrl = TosuClient.ValidateUrl(TosuUrl ?? "").ToString().TrimEnd('/');
        }
        catch (ArgumentException)
        {
            TosuUrl = "http://127.0.0.1:24050";
            LoadWarning = "Ungültige tosu-Adresse verworfen. Die lokale Standardadresse ist aktiv.";
        }
    }

    public void Save(string? path = null)
    {
        path = Path.GetFullPath(path ?? FilePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, path, true);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }
    public void SetCredentials(string appKey, string clientKey)
    {
        ProtectedCredentials = Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(appKey + "\n" + clientKey), null, DataProtectionScope.CurrentUser));
    }
    public (string AppKey, string ClientKey)? GetCredentials()
    {
        try
        {
            var parts = Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(ProtectedCredentials), null, DataProtectionScope.CurrentUser)).Split('\n');
            return parts.Length == 2 ? (parts[0], parts[1]) : null;
        }
        catch { return null; }
    }
}
