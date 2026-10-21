using System.Globalization;

namespace OsuHue;

internal static class TaikoConversion
{
    private readonly record struct Timing(double Time, double BeatLength, double Speed);
    private const int MaximumCues = 1_000_000;

    // Mirrors the short-slider branch of ppy/osu's TaikoBeatmapConverter.
    // Long sliders become drum rolls, which intentionally have no light pulses.
    public static HitCue[] Build(string text, List<string[]> objects, int version)
    {
        var points = new List<(double Time, double Length, bool Red)>();
        double multiplier = 1.4, tickRate = 1;
        string section = "";
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim('\uFEFF', ' ', '\t', '\r');
            if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal))
                continue;
            if (line.StartsWith('['))
            {
                section = line;
                continue;
            }
            if (section == "[Difficulty]")
            {
                int colon = line.IndexOf(':');
                if (colon < 0)
                    continue;
                string key = line[..colon].Trim();
                if (key == "SliderMultiplier")
                    multiplier = Number(line[(colon + 1)..]);
                if (key == "SliderTickRate")
                    tickRate = Number(line[(colon + 1)..]);
            }
            else if (section == "[TimingPoints]")
            {
                string[] parts = line.Split(',');
                if (parts.Length < 2)
                    throw InvalidTiming();
                double time = Number(parts[0]);
                bool red = parts.Length < 7 || parts[6].Trim() == "1";
                if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double length)
                    || double.IsInfinity(length) || red && double.IsNaN(length))
                    throw InvalidTiming();
                points.Add((time, length, red));
            }
        }
        if (multiplier <= 0 || tickRate <= 0)
            throw InvalidTiming();
        var timeline = new List<Timing>();
        double initialBeat = points.Where(p => p.Red).OrderBy(p => p.Time).Select(p => Math.Clamp(p.Length, 6, 60000)).FirstOrDefault(1000);
        double beat = initialBeat;
        foreach (var group in points.OrderBy(p => p.Time).GroupBy(p => p.Time))
        {
            var red = group.Where(p => p.Red).ToArray();
            if (red.Length > 0)
                beat = Math.Clamp(red[0].Length, 6, 60000);
            // At the same timestamp, an inherited point overrides the red point,
            // regardless of their order in the file (LegacyBeatmapDecoder).
            var active = group.LastOrDefault(p => !p.Red, group.First());
            double speed = active.Length < 0 ? Math.Clamp(100 / -active.Length, .1, 10) : 1;
            timeline.Add(new(group.Key, beat, speed));
        }
        var cues = new List<HitCue>();
        int pointIndex = -1;
        var timing = new Timing(double.NegativeInfinity, initialBeat, 1);
        foreach (string[] parts in objects.OrderBy(p => Number(p[2])))
        {
            double time = Number(parts[2]);
            while (pointIndex + 1 < timeline.Count && timeline[pointIndex + 1].Time <= time)
                timing = timeline[++pointIndex];
            int type = Integer(parts[3]);
            int sound = Integer(parts[4]);
            if ((type & 1) != 0)
            {
                Add(time, sound);
                continue;
            }
            if ((type & 2) == 0)
                continue;
            if (parts.Length < 8)
                throw InvalidSlider();
            int spans = Math.Max(1, Integer(parts[6]));
            if (spans > 9000)
                throw InvalidSlider();
            double distance = Number(parts[7]);
            if (distance < 0)
                throw InvalidSlider();

            // Preserve the upstream float promotion and operation order here:
            // simplifying the arithmetic changes boundary cases in old maps.
            const float velocityMultiplier = 1.4f;
            distance *= velocityMultiplier;
            distance *= spans;
            double speedAsLength = -100 / timing.Speed;
            double adjustedBeat = timing.BeatLength * (Math.Clamp((float)-speedAsLength, 10, 10000) / 100.0);
            double scoringDistance = 100 * (multiplier * velocityMultiplier) / tickRate;
            double velocity = scoringDistance * tickRate;
            double rawDuration = distance / velocity * adjustedBeat;
            if (!double.IsFinite(rawDuration) || rawDuration > int.MaxValue)
                throw InvalidSlider();
            int duration = (int)rawDuration;
            double osuVelocity = velocity * (1000f / adjustedBeat);
            double conversionBeat = version >= 8 ? timing.BeatLength : adjustedBeat;
            double spacing = Math.Min(conversionBeat / tickRate, (double)duration / spans);
            if (!(spacing > 0 && distance / osuVelocity * 1000 < 2 * conversionBeat))
                continue;
            if (time + spacing == time || (duration + spacing / 8) / spacing > MaximumCues - cues.Count)
                throw InvalidSlider();
            int[] sounds = Enumerable.Repeat(sound, spans + 1).ToArray();
            if (parts.Length > 8 && parts[8].Length > 0)
            {
                var nodes = parts[8].Split('|');
                for (int i = 0; i < Math.Min(nodes.Length, sounds.Length); i++)
                    sounds[i] = int.TryParse(nodes[i], out int node) && node >= 0 ? node : 0;
            }
            int nodeIndex = 0;
            for (double t = time; t <= time + duration + spacing / 8; t += spacing)
                Add(t, sounds[nodeIndex++ % sounds.Length]);
        }
        return RulesetEffects.GroupSimultaneous(cues);

        void Add(double time, int sound)
        {
            if (cues.Count >= MaximumCues)
                throw InvalidSlider();
            int colour = (sound & (2 | 8)) != 0 ? 1 : 0;
            cues.Add(new(time + (version < 5 ? 24 : 0), colour, colour));
        }
    }

    private static double Number(string value) => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number)
        && double.IsFinite(number) ? number : throw InvalidTiming();
    private static int Integer(string value) => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number)
        && number >= 0 ? number : throw InvalidSlider();
    private static FormatException InvalidTiming() => new("Invalid timing data for Taiko conversion.");
    private static FormatException InvalidSlider() => new("Invalid or excessively large slider in Taiko conversion.");
}
