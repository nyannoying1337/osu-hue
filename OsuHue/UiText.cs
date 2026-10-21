using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;

namespace OsuHue;

public static class UiText
{
    private sealed record Entry(string Key, string En, string De, bool Nested);
    private sealed record Template(Entry Entry, Regex Pattern);
    private static readonly Entry[] entries = Load();
    private static readonly Dictionary<string, Entry> literals = entries.Where(e => !e.De.Contains("{0}"))
        .SelectMany(e => new[] { (Text: e.De, Entry: e), (Text: e.En, Entry: e) })
        .GroupBy(e => e.Text).ToDictionary(g => g.Key, g => g.First().Entry, StringComparer.Ordinal);
    private static readonly Template[] templates = entries.Where(e => e.De.Contains("{0}"))
        .SelectMany(e => new[] { new Template(e, Compile(e.De)), new Template(e, Compile(e.En)) }).ToArray();
    private static readonly ConcurrentDictionary<(string Language, string Text), string> cache = new();
    private static string language = "en";
    public static string Language => Volatile.Read(ref language);

    public static void SetLanguage(string value)
    {
        if (value is not ("en" or "de"))
            throw new ArgumentException("Supported languages: en, de.", nameof(value));
        Application.Current?.Dispatcher.VerifyAccess();
        Volatile.Write(ref language, value);
        cache.Clear();
        if (Application.Current is { } app)
            foreach (var entry in entries)
                app.Resources[entry.Key] = value == "de" ? entry.De : entry.En;
    }

    // Translation stays at the presentation boundary. Map titles, file paths,
    // protocol values and persisted colour-source IDs are never translated.
    public static string Translate(string text)
    {
        var key = (Language, text);
        if (cache.TryGetValue(key, out string? cached))
            return cached;
        string result = TranslateCore(text, key.Item1, 0);
        if (cache.Count < 512)
            cache.TryAdd(key, result);
        return result;
    }

    private static string TranslateCore(string text, string target, int depth)
    {
        if (literals.TryGetValue(text, out var literal))
            return target == "de" ? literal.De : literal.En;
        if (depth >= 4)
            return text;
        foreach (var template in templates)
        {
            var match = template.Pattern.Match(text);
            if (!match.Success)
                continue;
            string result = target == "de" ? template.Entry.De : template.Entry.En;
            return Regex.Replace(result, @"\{(\d+)\}", placeholder =>
            {
                int index = int.Parse(placeholder.Groups[1].Value) + 1;
                string argument = match.Groups[index].Value;
                return template.Entry.Nested ? TranslateCore(argument, target, depth + 1) : argument;
            });
        }
        return text;
    }

    private static Regex Compile(string text)
    {
        string expression = string.Concat(Regex.Split(text, @"(\{\d+\})")
            .Select(part => Regex.IsMatch(part, @"^\{\d+\}$") ? "(.*?)" : Regex.Escape(part)));
        return new("\\A" + expression + "\\z", RegexOptions.Singleline | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    }

    private static Entry[] Load()
    {
        using var stream = typeof(UiText).Assembly.GetManifestResourceStream("OsuHue.Translations.json")
            ?? throw new InvalidOperationException("Translation resource is missing.");
        return JsonSerializer.Deserialize<Entry[]>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("Translation resource is empty.");
    }
}
