namespace OsuHue;

public static class DemoSequence
{
    private static readonly int[] tempos = [90, 120, 180, 240, 300];
    private static readonly Beatmap map = new("Demo", 0,
        Enumerable.Range(0, 64).Select(i => new HitCue(i * 500, i / 4 + 1)).ToArray(),
        [new(255, 95, 149), new(98, 196, 255), new(183, 236, 109), new(191, 147, 255)]);

    public static void ValidateTempo(int bpm)
    {
        if (bpm != 0 && (bpm < 60 || bpm > 360))
            throw new ArgumentOutOfRangeException(nameof(bpm), "Demo tempo must be automatic (0) or 60–360 BPM.");
    }

    public static Preview GetPreview(double elapsedMilliseconds, int selectedBpm, EffectOptions options, bool streaming)
    {
        ValidateTempo(selectedBpm);
        if (!double.IsFinite(elapsedMilliseconds) || elapsedMilliseconds < 0)
            throw new ArgumentOutOfRangeException(nameof(elapsedMilliseconds));

        bool automatic = selectedBpm == 0;
        int bpm = automatic ? tempos[(int)(elapsedMilliseconds % 40000 / 8000)] : selectedBpm;
        double rate = bpm / 120d;
        // Each automatic stage begins on a downbeat. Scale the reference map's
        // clock and rate together so the fade duration stays in real milliseconds.
        double stageTime = automatic ? elapsedMilliseconds % 8000 : elapsedMilliseconds;
        double mapTime = stageTime * rate % 32000;
        var pulse = Pulse.At(map, map.Colours, mapTime, rate, 0, options.Fade, options.Brightness);
        string status = streaming ? "Licht-Demo · Vorschau und Lampen laufen gemeinsam" : "Vorschau läuft ohne Lampen";
        if (automatic)
            status += " · Tempo wechselt alle 8 s";
        return new(pulse, map.Colours, $"Demo · {bpm} BPM", "Demo-Farben · Farbwechsel alle 4 Schläge", status, true, elapsedMilliseconds);
    }
}
