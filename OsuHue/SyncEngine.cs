using System.Diagnostics;

namespace OsuHue;

public sealed record EffectOptions(double Brightness = 0.45, double Fade = 180, double Offset = 60);
public sealed record Preview(Pulse Pulse, Rgb[] Palette, string Title, string Source, string Status, bool Playing, double Time);

public interface IHueSession : IAsyncDisposable
{
    Task StartAsync();
    void Send(Pulse pulse);
}

public sealed class SyncEngine : IAsyncDisposable
{
    public TosuClient Tosu { get; } = new();
    private readonly object hueLock = new();
    private IHueSession? hue;
    private readonly SemaphoreSlim sessionGate = new(1, 1);
    private bool disposed;
    private readonly CancellationTokenSource lifetime = new();
    private readonly Task loop;
    private EffectOptions options = new();
    private Preview preview = new(Pulse.Off, Beatmap.DefaultColours, "Warte auf eine Beatmap", "Noch keine Farbpalette", "tosu noch nicht verbunden", false, 0);
    private bool demo;
    private string? error;
    private Task? disposeTask;
    private readonly object disposeLock = new();
    private long demoStart = Stopwatch.GetTimestamp();
    private int demoBpm;
    public bool IsStreaming
    {
        get
        {
            lock (hueLock)
                return hue is not null;
        }
    }
    public bool Demo
    {
        get => Volatile.Read(ref demo);
        set
        {
            if (value)
                Volatile.Write(ref demoStart, Stopwatch.GetTimestamp());
            Volatile.Write(ref demo, value);
        }
    }
    public void SetDemoTempo(int bpm)
    {
        DemoSequence.ValidateTempo(bpm);
        Volatile.Write(ref demoBpm, bpm);
        Volatile.Write(ref demoStart, Stopwatch.GetTimestamp());
    }
    public Preview Current => Volatile.Read(ref preview);
    public string? Error => Volatile.Read(ref error);
    public void SetOptions(EffectOptions next) => Volatile.Write(ref options, next);
    public SyncEngine()
    {
        loop = Task.Run(RenderLoop);
    }

    public async Task StartAsync(IHueSession session, bool demo = false)
    {
        await sessionGate.WaitAsync();
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (IsStreaming)
                throw new InvalidOperationException("Ein Licht-Stream läuft bereits.");
            error = null;
            try
            {
                await session.StartAsync();
            }
            catch (Exception startError)
            {
                try
                {
                    await session.DisposeAsync();
                }
                catch (Exception cleanupError)
                {
                    AppLog.Error("Cleanup after stream start failure", cleanupError);
                    throw new AggregateException("Licht-Sync konnte nicht gestartet und nicht vollständig zurückgesetzt werden.", startError, cleanupError);
                }
                throw;
            }
            Demo = demo;
            lock (hueLock)
                hue = session;
        }
        finally { sessionGate.Release(); }
    }

    public async Task StopAsync()
    {
        await sessionGate.WaitAsync();
        try
        {
            await StopCoreAsync();
        }
        finally { sessionGate.Release(); }
    }

    private async Task StopCoreAsync()
    {
        Demo = false;
        IHueSession? previous;
        lock (hueLock)
        {
            previous = hue;
            hue = null;
        }
        if (previous is not null)
            await previous.DisposeAsync();
    }

    private async Task RenderLoop()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(20));
        while (await timer.WaitForNextTickAsync(lifetime.Token))
        {
            var settings = Volatile.Read(ref options);
            var frame = Tosu.Frame;
            var loaded = Tosu.Loaded;
            Pulse pulse = Pulse.Off;
            Preview next;
            if (Demo)
            {
                var time = Stopwatch.GetElapsedTime(Volatile.Read(ref demoStart)).TotalMilliseconds;
                next = DemoSequence.GetPreview(time, Volatile.Read(ref demoBpm), settings, IsStreaming);
                pulse = next.Pulse;
            }
            else
            {
                bool fresh = frame is { Age: < 500 };
                bool matched = fresh && loaded is not null && frame!.MapKey == loaded.Key;
                bool playing = matched && frame!.Playing && !frame.Paused && loaded!.Map.Mode == 0;
                if (playing)
                    pulse = Pulse.At(loaded!.Map, loaded.Palette, frame!.CurrentTime, frame.Rate, settings.Offset, settings.Fade, settings.Brightness, loaded.UseMapOffsets);
                var state = GetPlayStatus(frame, loaded, fresh, matched);
                next = new(pulse, loaded?.Palette ?? [], loaded?.Map.Title ?? "Warte auf eine Beatmap", loaded?.Source ?? "Wähle eine Map in osu!", state, playing, frame?.CurrentTime ?? 0);
            }
            Volatile.Write(ref preview, next);
            IHueSession? failed = null;
            lock (hueLock)
            {
                try
                {
                    hue?.Send(pulse);
                }
                catch (Exception ex) { AppLog.Error("Send Hue pulse", ex); error = "Hue-Stream unterbrochen: " + ex.Message; failed = hue; }
            }
            if (failed is not null)
            {
                await sessionGate.WaitAsync();
                try
                {
                    bool stillOwned;
                    lock (hueLock)
                    {
                        stillOwned = ReferenceEquals(hue, failed);
                        if (stillOwned)
                        {
                            hue = null;
                            Demo = false;
                        }
                    }
                    if (stillOwned)
                        await failed.DisposeAsync();
                }
                catch (Exception ex) { AppLog.Error("Restore failed Hue stream", ex); error += " · Rücksetzen fehlgeschlagen: " + ex.Message; }
                finally { sessionGate.Release(); }
            }
        }
    }

    private string GetPlayStatus(TosuFrame? frame, LoadedMap? loaded, bool fresh, bool matched)
    {
        if (!fresh || frame is null)
            return Tosu.Status;
        if (frame.Paused && frame.Playing)
            return "Pausiert · Lichtimpulse angehalten";
        if (loaded?.Map.Mode is > 0)
            return "Diese Version unterstützt osu!standard.";
        if (!frame.Playing)
            return "Warte auf den Map-Start";
        return matched ? "Spielt · Impulse folgen der Beatmap" : "Beatmap wird geladen …";
    }

    public ValueTask DisposeAsync()
    {
        lock (disposeLock)
            return new(disposeTask ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        await sessionGate.WaitAsync();
        try
        {
            disposed = true;
            lifetime.Cancel();
        }
        finally { sessionGate.Release(); }
        // The render loop may itself be waiting for the session gate to restore
        // a failed transport. Never hold that gate while awaiting its completion.
        try
        {
            await loop;
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        finally
        {
            try
            {
                await StopAsync();
            }
            finally
            {
                try
                {
                    await Tosu.DisposeAsync();
                }
                finally { lifetime.Dispose(); }
            }
        }
    }
}
