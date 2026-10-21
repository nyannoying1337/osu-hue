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
    private const string ExpectedExecutableHash = "0b365384f245424529d07f1f0ef0a6fe1e808197b9969007e54bbaee7eb39436";
    internal static async Task<bool> IsVerifiedExecutableAsync(string path, CancellationToken cancel)
    {
        if (!File.Exists(path))
            return false;
        await using var input = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(input, cancel)).Equals(ExpectedExecutableHash, StringComparison.OrdinalIgnoreCase);
    }

    public static async Task InstallAndStartAsync(IProgress<string> progress, CancellationToken cancel)
    {
        if (Process.GetProcessesByName("tosu").Length > 0)
        {
            progress.Report("tosu läuft bereits · öffne jetzt osu! stable oder lazer.");
            return;
        }
        var folder = Path.Combine(Settings.DataFolder, "tools", "tosu-" + Version);
        var exe = Path.Combine(folder, "tosu.exe");
        if (!await IsVerifiedExecutableAsync(exe, cancel))
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
                if (total is <= 0 or > 96 * 1024 * 1024)
                    throw new IOException("Unexpected tosu download size.");
                using (var input = await response.Content.ReadAsStreamAsync(cancel))
                await using (var output = File.Create(archive))
                {
                    var buffer = new byte[65536];
                    long sum = 0;
                    int read;
                    while ((read = await input.ReadAsync(buffer, cancel)) > 0)
                    {
                        sum += read;
                        if (sum > 96 * 1024 * 1024)
                            throw new IOException("The tosu download exceeds its size limit.");
                        await output.WriteAsync(buffer.AsMemory(0, read), cancel);
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
                using (var zip = ZipFile.OpenRead(archive))
                {
                    if (zip.Entries.Sum(entry => entry.Length) > 256 * 1024 * 1024)
                        throw new IOException("The tosu archive exceeds its size limit.");
                }
                ZipFile.ExtractToDirectory(archive, folder, overwriteFiles: true);
                File.Delete(archive);
                if (!await IsVerifiedExecutableAsync(exe, cancel))
                    throw new IOException("The extracted tosu executable failed verification.");
            }
            catch
            {
                if (File.Exists(archive))
                    File.Delete(archive);
                throw;
            }
        }
        cancel.ThrowIfCancellationRequested();
        Process.Start(new ProcessStartInfo(exe) { Arguments = "--update=false", WorkingDirectory = Path.GetDirectoryName(exe), UseShellExecute = false, CreateNoWindow = true });
        progress.Report("tosu gestartet · öffne jetzt osu! stable oder lazer.");
    }
}
