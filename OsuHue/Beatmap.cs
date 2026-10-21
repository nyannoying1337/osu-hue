using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OsuHue;

public readonly record struct Rgb(byte R, byte G, byte B)
{
    public string Hex => $"#{R:X2}{G:X2}{B:X2}";
    public static readonly Rgb Black = new(0, 0, 0);
}

public sealed record HitCue(double Time, int ColourIndex, int SkinColourIndex = -1)
{
    public int[]? SimultaneousColours
    {
        get; init;
    }
}
public sealed record Beatmap(string Title, int Mode, HitCue[] Hits, Rgb[] Colours)
{
    public int Keys { get; init; } = 4;

    // osu! classic's fallback palette. Custom skin/beatmap palettes take precedence.
    public static readonly Rgb[] DefaultColours = [new(255, 192, 0), new(0, 202, 0), new(18, 124, 255), new(242, 24, 57)];

    public static Rgb[] ReadColours(string text)
    {
        var colours = new SortedDictionary<int, Rgb>();
        var section = "";
        foreach (var raw in text.Split('\n'))
        {
            var line = Clean(raw);
            if (line.StartsWith('['))
            {
                section = line;
                continue;
            }
            if (!section.Equals("[Colours]", StringComparison.OrdinalIgnoreCase))
                continue;
            var match = Regex.Match(line, @"^Combo(\d+)\s*:\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)", RegexOptions.IgnoreCase);
            if (!match.Success || !int.TryParse(match.Groups[1].Value, out int index) || index is < 1 or > 8)
                continue;
            if (byte.TryParse(match.Groups[2].Value, out byte r) && byte.TryParse(match.Groups[3].Value, out byte g) && byte.TryParse(match.Groups[4].Value, out byte b))
                colours[index] = new(r, g, b);
        }
        return colours.Values.ToArray();
    }

    public static Beatmap Parse(string text, int? playMode = null)
    {
        if (!text.TrimStart('\uFEFF', ' ', '\r', '\n').StartsWith("osu file format v", StringComparison.Ordinal))
            throw new FormatException("Die Antwort ist keine osu!-Beatmap.");
        var section = "";
        string title = "Beatmap", artist = "", difficulty = "";
        int mode = 0, version = 14, keys = 5;
        var records = new List<string[]>();
        var rawObjects = new List<(double time, int type)>();
        var breaks = new List<double>();
        foreach (var raw in text.Split('\n'))
        {
            var line = Clean(raw);
            if (line.Length == 0)
                continue;
            if (line.StartsWith("osu file format v"))
                int.TryParse(line[17..], out version);
            if (line.StartsWith('['))
            {
                section = line;
                continue;
            }
            if (section == "[HitObjects]")
            {
                var parts = line.Split(',');
                if (parts.Length >= 5 && double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double time)
                    && double.IsFinite(time) && int.TryParse(parts[3], out int type) && type is >= 0 and <= 255)
                {
                    rawObjects.Add((time, type));
                    records.Add(parts);
                }
                else
                    throw new FormatException("Die Beatmap enthält ein ungültiges Hitobject.");
            }
            else if (section == "[Events]")
            {
                var parts = line.Split(',');
                if (parts.Length >= 3 && (parts[0] == "2" || parts[0] == "Break") && double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double end))
                    breaks.Add(end);
            }
            else
            {
                int colon = line.IndexOf(':');
                if (colon < 0)
                    continue;
                var key = line[..colon].Trim();
                var value = line[(colon + 1)..].Trim();
                if (section == "[General]" && key == "Mode")
                    int.TryParse(value, out mode);
                if (section == "[Difficulty]" && key == "CircleSize" && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double cs) && double.IsFinite(cs))
                    keys = (int)Math.Clamp(Math.Round(cs), 0, 19);
                if (section == "[Metadata]")
                {
                    if (key == "Title")
                        title = value;
                    if (key == "Artist")
                        artist = value;
                    if (key == "Version")
                        difficulty = value;
                }
            }
        }
        if (rawObjects.Count == 0)
            throw new FormatException("Die Beatmap enthält keine Hitobjects.");
        if (version <= 0 || mode is < 0 or > 3)
            throw new FormatException("Ungültige Beatmap-Version oder ungültiger Spielmodus.");
        int target = playMode ?? mode;
        if (target != mode && !(mode == 0 && target is 1 or 2))
            throw new FormatException("Diese Modus-Konvertierung wird noch nicht unterstützt. Wähle eine native Map für den Spielmodus.");
        if (target == 3 && keys > 10)
            keys = keys / 2 * 2;
        var cues = mode == 0 && target == 1 ? TaikoConversion.Build(text, records, version)
            : target is 1 or 3 ? RulesetEffects.BuildCues(records, target, keys, version) : BuildCues(rawObjects, breaks, version);
        return new($"{artist} — {title} [{difficulty}]", target, cues, ReadColours(text))
        {
            Keys = keys
        };
    }

    public static Beatmap FromTimeline(JsonElement root, string title)
    {
        var objects = root.GetProperty("hitObjects").EnumerateArray().Select(x => (time: x.GetProperty("startTime").GetDouble(), type: x.GetProperty("type").GetInt32())).ToList();
        var breaks = root.GetProperty("breaks").EnumerateArray().Select(x => x.GetProperty("endTime").GetDouble()).ToList();
        var palette = root.GetProperty("comboColours").EnumerateArray().Select(x => new Rgb(x.GetProperty("red").GetByte(), x.GetProperty("green").GetByte(), x.GetProperty("blue").GetByte())).ToArray();
        if (objects.Count == 0)
            throw new FormatException("Die Beatmap enthält keine Hitobjects.");
        return new(title, root.GetProperty("mode").GetInt32(), BuildCues(objects, breaks, root.GetProperty("fileFormat").GetInt32()), palette);
    }

    private static HitCue[] BuildCues(List<(double time, int type)> rawObjects, List<double> breaks, int version)
    {
        if (rawObjects.Any(x => !double.IsFinite(x.time) || x.type is < 0 or > 255) || breaks.Any(x => !double.IsFinite(x)) || version <= 0)
            throw new FormatException("Die Beatmap enthält ungültige Zeit- oder Versionsangaben.");
        var cues = new List<HitCue>();
        // osu! starts at palette index 1 (Combo2), not index 0 (Combo1).
        int colour = 0, skinColour = 0, breakIndex = 0;
        bool previousSpinner = false, first = true;
        var orderedBreaks = breaks.Order().ToArray();
        foreach (var (rawTime, type) in rawObjects.OrderBy(x => x.time))
        {
            bool afterBreak = false;
            while (breakIndex < orderedBreaks.Length && rawTime > orderedBreaks[breakIndex])
            {
                breakIndex++;
                afterBreak = true;
            }
            bool spinner = (type & 8) != 0;
            if (!spinner && (first || previousSpinner || afterBreak || (type & 4) != 0))
            {
                colour += 1 + ((type & 4) != 0 ? ((type >> 4) & 7) : 0);
                skinColour++;
            }
            // Spinners do not consume a colour, even if marked as a new combo.
            if (!spinner && (type & 3) != 0)
                cues.Add(new(rawTime + (version < 5 ? 24 : 0), colour, skinColour));
            previousSpinner = spinner;
            first = false;
        }
        return cues.ToArray();
    }

    public int FindHit(double time)
    {
        int lo = 0, hi = Hits.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (Hits[mid].Time <= time)
                lo = mid + 1;
            else
                hi = mid;
        }
        return lo - 1;
    }

    private static string Clean(string line)
    {
        var trimmed = line.Trim('\uFEFF', ' ', '\t', '\r');
        return trimmed.StartsWith("//", StringComparison.Ordinal) ? "" : trimmed;
    }
}

public sealed record Pulse(Rgb Colour, double Brightness, int HitIndex)
{
    public static readonly Pulse Off = new(Rgb.Black, 0, -1);
    public static Pulse At(Beatmap map, Rgb[] palette, double mapTime, double rate, double offset, double fade, double maximum, bool useMapOffsets = true)
    {
        if (!double.IsFinite(mapTime) || !double.IsFinite(rate) || !double.IsFinite(offset)
            || !double.IsFinite(fade) || !double.IsFinite(maximum))
            return Off;
        rate = Math.Clamp(rate, 0.1, 5);
        int i = map.FindHit(mapTime + offset * rate);
        if (i < 0 || palette.Length == 0)
            return Off;
        double age = (mapTime + offset * rate - map.Hits[i].Time) / rate;
        double level = Math.Exp(-age / Math.Clamp(fade, 40, 1000));
        int index = !useMapOffsets && map.Hits[i].SkinColourIndex >= 0 ? map.Hits[i].SkinColourIndex : map.Hits[i].ColourIndex;
        var colour = palette[index % palette.Length];
        if (map.Hits[i].SimultaneousColours is { Length: > 1 } chord)
        {
            int r = 0, g = 0, b = 0;
            foreach (int component in chord)
            {
                var part = palette[component % palette.Length];
                r += part.R;
                g += part.G;
                b += part.B;
            }
            colour = new((byte)(r / chord.Length), (byte)(g / chord.Length), (byte)(b / chord.Length));
        }
        return new(colour, level < 0.008 ? 0 : level * Math.Clamp(maximum, 0, 1), i);
    }
}
