using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;

namespace OsuHue;

public static class TosuInstaller
{
    public const string Version = "4.26.2";
    private const string Download = "https://github.com/tosuapp/tosu/releases/download/v4.26.2/tosu-windows-v4.26.2.zip";
    private const string ExpectedHash = "d34c2bfc495959fcc79ff01f10b7dc9aea77eb508ed23c8f0d0fb8342773faa3";
    public static async Task InstallAndStartAsync(IProgress<string> progress, CancellationToken cancel)
    {
        if (Process.GetProcessesByName("tosu").Length > 0)
        {
            progress.Report("tosu läuft bereits · öffne jetzt osu! stable.");
            return;
        }
        var folder = Path.Combine(Settings.DataFolder, "tools", "tosu-" + Version);
        var exe = Directory.Exists(folder) ? Directory.GetFiles(folder, "tosu.exe", SearchOption.AllDirectories).FirstOrDefault() : null;
        if (exe is null)
        {
            Directory.CreateDirectory(folder);
            string archive = Path.Combine(folder, "download.zip");
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("OsuHue/0.1");
            try
            {
                using var response = await http.GetAsync(Download, HttpCompletionOption.ResponseHeadersRead, cancel);
                response.EnsureSuccessStatusCode();
                long total = response.Content.Headers.ContentLength ?? 45491440;
                using (var input = await response.Content.ReadAsStreamAsync(cancel))
                await using (var output = File.Create(archive))
                {
                    var buffer = new byte[65536];
                    long sum = 0;
                    int read;
                    while ((read = await input.ReadAsync(buffer, cancel)) > 0)
                    {
                        await output.WriteAsync(buffer.AsMemory(0, read), cancel);
                        sum += read;
                        progress.Report($"tosu wird geladen … {Math.Min(100, sum * 100 / total)} %");
                    }
                }
                progress.Report("tosu: Download wird geprüft …");
                await using (var input = File.OpenRead(archive))
                {
                    var hash = Convert.ToHexString(await SHA256.HashDataAsync(input, cancel));
                    if (!hash.Equals(ExpectedHash, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("Die Download-Prüfsumme stimmt nicht. Der Download wird nicht gestartet.");
                }
                ZipFile.ExtractToDirectory(archive, folder, overwriteFiles: true);
                File.Delete(archive);
                exe = Directory.GetFiles(folder, "tosu.exe", SearchOption.AllDirectories).FirstOrDefault() ?? throw new IOException("tosu.exe fehlt im Download.");
            }
            catch
            {
                if (File.Exists(archive))
                    File.Delete(archive);
                throw;
            }
        }
        cancel.ThrowIfCancellationRequested();
        Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = Path.GetDirectoryName(exe), UseShellExecute = false, CreateNoWindow = true });
        progress.Report("tosu gestartet · öffne jetzt osu! stable.");
    }
}
