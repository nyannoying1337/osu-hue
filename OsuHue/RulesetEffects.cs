using System.Globalization;

namespace OsuHue;

public static class RulesetEffects
{
    public static bool IsArgon(string name) => name is "osu! \"argon\" (2022)" or "osu! \"argon\" pro (2022)";

    public static Rgb[] BuiltInComboColours(string client, string name) =>
        client.Equals("lazer", StringComparison.OrdinalIgnoreCase) && IsArgon(name)
            ? [new(241, 116, 0), new(0, 241, 53), new(0, 82, 241), new(241, 0, 0), new(232, 235, 0), new(92, 0, 241)]
            : [];

    public static string Name(int mode) => mode switch
    {
        0 => "osu!",
        1 => "osu!taiko",
        2 => "osu!catch",
        3 => "osu!mania",
        _ => "Unbekannter Modus"
    };

    public static Rgb[] ManiaColours(int keys, bool argon)
    {
        if (keys is < 1 or > 18)
            throw new FormatException("Mania benötigt zwischen 1 und 18 Spalten.");
        if (keys > 10)
        {
            var stage = ManiaColours(keys / 2, argon);
            return Enumerable.Range(0, keys).Select(i => stage[i % stage.Length]).ToArray();
        }
        // Argon's column layout, from ppy/osu ManiaArgonSkinTransformer. These
        // are column accents; custom note textures can have different colours.
        Rgb[] colours = [new(255, 197, 40), new(252, 109, 1), new(213, 35, 90), new(203, 60, 236), new(72, 198, 255), new(100, 192, 92), new(169, 106, 255)];
        int[][] layouts = [[], [0], [5, 4], [5, 6, 4], [0, 1, 2, 3], [2, 1, 0, 5, 4], [2, 1, 5, 4, 1, 2], [2, 1, 2, 6, 2, 1, 2], [3, 2, 1, 5, 4, 1, 2, 3], [3, 2, 1, 0, 6, 0, 1, 2, 3], [3, 2, 1, 0, 5, 4, 0, 1, 2, 3]];
        if (!argon)
            return Enumerable.Range(0, keys).Select(i => keys % 2 == 1 && i == keys / 2
                ? new Rgb(255, 197, 40) : i % 2 == 0 ? new Rgb(255, 255, 255) : new Rgb(72, 198, 255)).ToArray();
        return Enumerable.Range(0, keys).Select(i => colours[keys < layouts.Length ? layouts[keys][i]
            : keys % 2 == 1 && i == keys / 2 ? 6 : i % 6]).ToArray();
    }

    public static HitCue[] BuildCues(List<string[]> objects, int mode, int keys, int version)
    {
        if (mode == 3 && keys is < 1 or > 18)
            throw new FormatException("Mania benötigt zwischen 1 und 18 Spalten.");
        var cues = new List<HitCue>();
        foreach (var parts in objects)
        {
            int type = int.Parse(parts[3], CultureInfo.InvariantCulture);
            double time = double.Parse(parts[2], CultureInfo.InvariantCulture) + (version < 5 ? 24 : 0);
            if (mode == 1)
            {
                if ((type & 1) == 0)
                    continue;
                if (!int.TryParse(parts[4], out int sound) || sound < 0)
                    throw new FormatException("Ungültiger Taiko-Hitsound.");
                int colour = (sound & (2 | 8)) != 0 ? 1 : 0;
                cues.Add(new(time, colour, colour));
            }
            else
            {
                if ((type & (1 | 128)) == 0)
                    throw new FormatException("Diese alte Mania-Map benötigt eine Slider-/Spinner-Konvertierung.");
                if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double x) || !double.IsFinite(x))
                    throw new FormatException("Ungültige Mania-Spalte.");
                int column = (int)Math.Floor(Math.Clamp(x, 0, 512) * keys / 512);
                column = Math.Min(column, keys - 1);
                cues.Add(new(time, column, column));
            }
        }
        // One pulse per chord avoids letting the last object in file order
        // arbitrarily replace all other simultaneous notes.
        return cues.OrderBy(x => x.Time).GroupBy(x => x.Time).Select(group =>
        {
            var first = group.First();
            return first with
            {
                SimultaneousColours = group.Select(x => x.ColourIndex).Distinct().ToArray()
            };
        }).ToArray();
    }
}
