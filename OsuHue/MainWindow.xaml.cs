using Microsoft.Win32;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace OsuHue;

public partial class MainWindow : Window
{
    private readonly Settings settings;
    private readonly SyncEngine engine = new();
    private readonly DispatcherTimer refresh = new() { Interval = TimeSpan.FromMilliseconds(80) };
    private readonly CancellationTokenSource lifetime = new();
    private readonly bool screenshot;
    private readonly bool lifecycleTest;
    private bool ready, busy, closing, allowClose;
    private string pairedIp = "", lastPalette = "", lastError = "";

    public MainWindow(bool screenshot = false, bool lifecycleTest = false)
    {
        this.screenshot = screenshot;
        this.lifecycleTest = lifecycleTest;
        settings = screenshot || lifecycleTest ? new() : Settings.Load();
        UiText.SetLanguage(settings.Language);
        InitializeComponent();
        LanguageChoice.SelectedValue = settings.Language;
        BridgeIp.Text = settings.BridgeIp;
        pairedIp = settings.GetCredentials() is not null ? settings.BridgeIp : "";
        TosuUrl.Text = settings.TosuUrl;
        BrightnessSlider.Value = Math.Clamp(settings.Brightness, 1, 100);
        FadeSlider.Value = Math.Clamp(settings.Fade, 40, 600);
        OffsetSlider.Value = Math.Clamp(settings.Offset, -300, 300);
        ColourMode.SelectedIndex = settings.ColourSource switch
        {
            "Skin" => 1,
            "Beatmap" => 2,
            _ => 0
        };
        SkinLabel.Text = settings.SkinFile.Length == 0 ? UiText.Translate("osu!/Catch: Skin automatisch; Mania/Taiko: Moduspalette.") : settings.SkinFile;
        BridgeStatus.Text = UiText.Translate(pairedIp.Length > 0 ? "Kopplung gespeichert · Bereich wird geladen" : "Noch nicht gekoppelt");
        ready = true;
        if (settings.LoadWarning is string warning)
            Notice.Text = UiText.Translate(warning);
        UpdateEffects();
        UpdatePalette();
        refresh.Tick += (_, _) => RefreshPreview();
        refresh.Start();
        Loaded += Window_Loaded;
        if (!screenshot)
            Closing += Window_Closing;
    }

    private void Language_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!ready || LanguageChoice.SelectedValue is not string language)
            return;
        settings.Language = language;
        UiText.SetLanguage(language);
        BridgeStatus.Text = UiText.Translate(BridgeStatus.Text);
        Notice.Text = UiText.Translate(Notice.Text);
        if (settings.SkinFile.Length == 0)
            SkinLabel.Text = UiText.Translate(SkinLabel.Text);
        UpdateDiscoveryButton();
        try
        {
            settings.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Error("Save language preference", ex);
            Notice.Text = UiText.Translate("Sprache konnte nicht gespeichert werden: " + ex.Message);
        }
        RefreshPreview();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (screenshot || lifecycleTest)
            return;
        await Run(async () =>
        {
            await engine.Tosu.ConnectAsync(settings.TosuUrl);
            if (settings.GetCredentials() is not null)
                await LoadAreas();
        });
    }

    private async Task Run(Func<Task> operation)
    {
        if (busy || closing)
            return;
        busy = true;
        UpdateControls();
        try
        {
            await operation();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) { AppLog.Error("UI operation", ex); Notice.Text = UiText.Translate(FriendlyError(ex)); }
        finally { busy = false; UpdateControls(); }
    }

    private static string FriendlyError(Exception ex) => ex switch
    {
        System.Net.Http.HttpRequestException => "Verbindung fehlgeschlagen. Prüfe Bridge-IP, Netzwerk und ob die Bridge eingeschaltet ist. " + ex.Message,
        TaskCanceledException => "Die Verbindung hat nicht rechtzeitig geantwortet. Prüfe Bridge-IP und Netzwerk.",
        _ => ex.Message
    };

    private void UpdateControls()
    {
        LanguageChoice.IsEnabled = !busy;
        bool streaming = engine.IsStreaming;
        bool comboPalette = engine.Tosu.Frame?.Mode is not (1 or 3);
        ColourMode.IsEnabled = SkinFileButton.IsEnabled = ClearSkinButton.IsEnabled = comboPalette && !busy;
        ColourMode.ToolTip = UiText.Translate(comboPalette ? "Combo-Farben für osu! und Catch" : "Taiko und Mania verwenden eine eigene Moduspalette.");
        DiscoverButton.IsEnabled = PairButton.IsEnabled = RefreshAreasButton.IsEnabled = BridgeIp.IsEnabled = AreaBox.IsEnabled = !busy && !streaming;
        InstallButton.IsEnabled = ReconnectButton.IsEnabled = TosuUrl.IsEnabled = !busy && !streaming;
        DemoButton.IsEnabled = !busy && (!streaming || engine.Demo);
        StartButton.IsEnabled = !busy;
        StartButton.Content = UiText.Translate(busy ? "Bitte warten …" : streaming ? engine.Demo ? "Demo stoppen" : "Licht-Sync stoppen" : "Licht-Sync starten");
        StreamBadge.Text = UiText.Translate(streaming ? engine.Demo ? "LICHT-DEMO AKTIV" : "LICHT-SYNC AKTIV" : "VORSCHAU");
        DemoButton.Content = UiText.Translate(engine.Demo ? "Demo beenden" : pairedIp.Length > 0 ? "Licht-Demo" : "Vorschau-Demo");
        DemoButton.ToolTip = UiText.Translate(pairedIp.Length > 0 ? "Testet Vorschau und Lampen im gewählten Bereich. osu! und tosu sind dafür nicht nötig." : "Ohne Kopplung läuft die Demo nur auf dem Bildschirm.");
    }

    private async void Discover_Click(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        try
        {
            string ip = BridgeIp.Text.Trim();
            BridgeStatus.Text = UiText.Translate(ip.Length > 0 ? $"Prüfe {ip} …" : "Suche im lokalen Netzwerk …");
            if (ip.Length == 0)
            {
                var bridges = await HueSession.DiscoverAsync();
                if (bridges.Length == 0)
                    throw new InvalidOperationException("Keine Bridge gefunden. Trage die IP aus der Hue App ein und klicke auf „IP prüfen“.");
                BridgeIp.Text = ip = bridges[0];
            }
            var bridge = await HueSession.ProbeAsync(ip);
            BridgeStatus.Text = UiText.Translate($"{bridge.Name} gefunden · {bridge.Ip} · {bridge.Model}");
            Notice.Text = UiText.Translate("Bridge erreichbar. Drücke ihren runden Knopf und klicke danach auf „Koppeln“.");
        }
        catch { BridgeStatus.Text = UiText.Translate("Bridge-Prüfung nicht erfolgreich"); throw; }
    });

    private void BridgeIp_TextChanged(object sender, TextChangedEventArgs e) => UpdateDiscoveryButton();

    private void UpdateDiscoveryButton()
    {
        if (DiscoverButton is not null)
            DiscoverButton.Content = UiText.Translate(string.IsNullOrWhiteSpace(BridgeIp.Text) ? "Suchen" : "IP prüfen");
    }

    private async void Pair_Click(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        var ip = BridgeIp.Text.Trim();
        var bridge = await HueSession.ProbeAsync(ip);
        BridgeStatus.Text = UiText.Translate($"{bridge.Name} erreichbar · Kopplung läuft …");
        var credentials = await HueSession.PairAsync(ip);
        settings.BridgeIp = pairedIp = ip;
        settings.SetCredentials(credentials.AppKey, credentials.ClientKey);
        settings.Save();
        BridgeStatus.Text = UiText.Translate("Bridge gekoppelt · Schlüssel lokal geschützt");
        await LoadAreas();
        Notice.Text = UiText.Translate("Kopplung gespeichert. Wähle deinen Entertainment-Bereich.");
    });

    private async void RefreshAreas_Click(object sender, RoutedEventArgs e) => await Run(LoadAreas);
    private async Task LoadAreas()
    {
        var credentials = settings.GetCredentials() ?? throw new InvalidOperationException("Bitte zuerst die Bridge koppeln.");
        if (BridgeIp.Text.Trim() != pairedIp)
            throw new InvalidOperationException("Die Bridge-IP wurde geändert. Bitte diese Bridge zuerst koppeln.");
        var areas = await HueSession.GetAreasAsync(pairedIp, credentials.AppKey);
        AreaBox.ItemsSource = areas;
        AreaBox.SelectedItem = areas.FirstOrDefault(x => x.Id.ToString() == settings.AreaId) ?? areas.FirstOrDefault();
        BridgeStatus.Text = UiText.Translate(areas.Length > 0 ? $"Bridge bereit · {areas.Length} Entertainment-Bereich(e)" : "Gekoppelt · noch kein Entertainment-Bereich");
        if (areas.Length == 0)
            Notice.Text = UiText.Translate("Erstelle in der Hue App einen Entertainment-Bereich mit deinen farbfähigen Lampen. Danach hier auf ↻ klicken.");
    }

    private async void Reconnect_Click(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        var address = TosuClient.ValidateUrl(TosuUrl.Text).ToString().TrimEnd('/');
        settings.TosuUrl = address;
        settings.Save();
        await engine.Tosu.ConnectAsync(settings.TosuUrl);
        Notice.Text = UiText.Translate("Verbindung zu tosu wird neu aufgebaut.");
    });

    private async void Install_Click(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        var progress = new Progress<string>(text => Notice.Text = UiText.Translate(text));
        await TosuInstaller.InstallAndStartAsync(progress, lifetime.Token);
        await engine.Tosu.ConnectAsync(TosuUrl.Text);
    });

    private async void Start_Click(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        if (engine.IsStreaming)
        {
            await engine.StopAsync();
            Notice.Text = UiText.Translate("Licht-Sync gestoppt · vorherige Lampeneinstellungen wiederhergestellt.");
            return;
        }
        if (engine.Tosu.Frame is not { Age: < 500 })
            throw new InvalidOperationException("Starte zuerst tosu und osu! stable oder lazer. Eine gekoppelte Bridge allein reicht noch nicht.");
        await engine.StartAsync(CreateHueSession());
        Notice.Text = UiText.Translate("Licht-Sync aktiv. Starte eine Map im gewählten Spielmodus. Zum Zurücksetzen auf „Licht-Sync stoppen“ klicken.");
    });

    private HueSession CreateHueSession()
    {
        var credentials = settings.GetCredentials() ?? throw new InvalidOperationException("Bitte zuerst die Bridge koppeln.");
        if (BridgeIp.Text.Trim() != pairedIp)
            throw new InvalidOperationException("Die Bridge-IP wurde geändert. Bitte erneut koppeln.");
        if (AreaBox.SelectedItem is not HueArea area)
            throw new InvalidOperationException("Wähle einen Entertainment-Bereich. Falls keiner vorhanden ist, erstelle ihn in der Hue App.");
        settings.AreaId = area.Id.ToString();
        settings.Save();
        return new HueSession(pairedIp, credentials.AppKey, credentials.ClientKey, area.Id);
    }

    private async void Demo_Click(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        if (engine.Demo)
        {
            bool wasStreaming = engine.IsStreaming;
            await engine.StopAsync();
            Notice.Text = UiText.Translate(wasStreaming ? "Demo beendet · vorherige Lampeneinstellungen wiederhergestellt." : "Demo beendet · Vorschau folgt wieder osu!.");
        }
        else if (pairedIp.Length > 0)
        {
            await engine.StartAsync(CreateHueSession(), demo: true);
            Notice.Text = UiText.Translate("Licht-Demo aktiv · zum Zurücksetzen auf „Demo beenden“ klicken.");
        }
        else
        {
            engine.Demo = true;
            Notice.Text = UiText.Translate("Nur Bildschirmvorschau. Kopple die Bridge und wähle einen Bereich, um die Lampen mitzutesten.");
        }
    });

    private void DemoTempo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (ready && DemoTempo.SelectedItem is ComboBoxItem item && int.TryParse(item.Tag?.ToString(), out int bpm))
            engine.SetDemoTempo(bpm);
    }

    private void Effect_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ready)
            UpdateEffects();
    }
    private void UpdateEffects()
    {
        BrightnessValue.Text = $"{BrightnessSlider.Value:0} %";
        FadeValue.Text = $"{FadeSlider.Value:0} ms";
        OffsetValue.Text = $"{OffsetSlider.Value:+0;-0;0} ms";
        settings.Brightness = BrightnessSlider.Value;
        settings.Fade = FadeSlider.Value;
        settings.Offset = OffsetSlider.Value;
        engine.SetOptions(new(settings.Brightness / 100, settings.Fade, settings.Offset));
    }

    private void Palette_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (ready)
            UpdatePalette();
    }
    private void UpdatePalette()
    {
        settings.ColourSource = (ColourMode.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Automatisch";
        engine.Tosu.SetPaletteOptions(settings.SkinFile, settings.ColourSource);
    }
    private void Skin_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { Filter = UiText.Translate("osu!-Skin (skin.ini)|skin.ini|INI-Dateien (*.ini)|*.ini"), Title = UiText.Translate("skin.ini auswählen") };
        if (picker.ShowDialog(this) == true)
        {
            settings.SkinFile = picker.FileName;
            SkinLabel.Text = picker.FileName;
            UpdatePalette();
        }
    }
    private void ClearSkin_Click(object sender, RoutedEventArgs e)
    {
        settings.SkinFile = "";
        SkinLabel.Text = UiText.Translate("osu!/Catch: Skin automatisch; Mania/Taiko: Moduspalette.");
        UpdatePalette();
    }

    private void RefreshPreview()
    {
        var state = engine.Current;
        var colour = state.Pulse.Colour;
        var brush = new SolidColorBrush(Color.FromRgb(colour.R, colour.G, colour.B));
        CircleRing.Stroke = brush;
        CircleFill.Fill = brush;
        CircleFill.Opacity = state.Pulse.Brightness * 0.7;
        CircleRing.Opacity = Math.Max(0.16, Math.Sqrt(state.Pulse.Brightness));
        ColourHex.Text = UiText.Translate(state.Pulse.Brightness > 0 ? colour.Hex : state.Playing ? "ZWISCHEN IMPULSEN" : "BEREIT");
        HitNumber.Text = state.Pulse.HitIndex >= 0 ? (state.Pulse.HitIndex + 1).ToString() : "—";
        MapTitle.Text = engine.Tosu.Frame is null && !engine.Demo ? UiText.Translate(state.Title) : state.Title;
        PaletteSource.Text = UiText.Translate(state.Source);
        PlayStatus.Text = UiText.Translate(state.Status);
        TosuStatus.Text = UiText.Translate(engine.Tosu.Status);
        var paletteKey = string.Join(",", state.Palette.Select(x => x.Hex));
        if (lastPalette != paletteKey)
        {
            lastPalette = paletteKey;
            PaletteSwatches.Children.Clear();
            foreach (var c in state.Palette)
                PaletteSwatches.Children.Add(new Ellipse { Width = 19, Height = 19, Fill = new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B)), Margin = new Thickness(0, 0, 8, 0), ToolTip = c.Hex });
        }
        if (engine.Error is string error && error != lastError)
        {
            lastError = error;
            Notice.Text = UiText.Translate(error);
        }
        if (!busy)
            UpdateControls();
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (allowClose)
            return;
        e.Cancel = true;
        if (closing)
            return;
        closing = true;
        lifetime.Cancel();
        refresh.Stop();
        IsEnabled = false;
        Notice.Text = UiText.Translate("Beenden · Licht-Sync wird gestoppt …");
        // Let an in-progress pairing/start operation finish before disposing shared state.
        while (busy)
            await Task.Delay(50);
        var failures = new List<Exception>();
        try
        {
            if (!screenshot && !lifecycleTest)
                settings.Save();
        }
        catch (Exception ex) { AppLog.Error("Save settings on close", ex); failures.Add(ex); }
        try
        {
            await engine.DisposeAsync();
        }
        catch (Exception ex) { AppLog.Error("Stop app", ex); failures.Add(ex); }
        if (failures.Count > 0)
        {
            if (lifecycleTest)
                throw new AggregateException(failures);
            MessageBox.Show(UiText.Translate("Beim Beenden ist ein Fehler aufgetreten: " + string.Join(" · ", failures.Select(x => UiText.Translate(x.Message)))), "osu!hue");
        }
        allowClose = true;
        // Disposal may complete synchronously. Queue Close after the original
        // Closing event returns, so WPF never receives a reentrant close.
        _ = Dispatcher.BeginInvoke(() => Close());
    }

    internal void CapturePreview(bool compact)
    {
        refresh.Stop();
        Width = compact ? 680 : 1100;
        Height = compact ? 760 : 900;
        ApplyResponsiveLayout(Width);
        MapTitle.Text = "onoken — Sakimori Souka [Hyper]";
        PaletteSource.Text = UiText.Translate("Skin-Farben");
        PlayStatus.Text = UiText.Translate("Spielt · Impulse folgen der Beatmap");
        TosuStatus.Text = UiText.Translate("tosu verbunden · osu! stable");
        BridgeIp.Text = UiText.Translate("192.168.1.20");
        BridgeStatus.Text = UiText.Translate("Bridge bereit · 1 Entertainment-Bereich");
        AreaBox.Items.Add(new HueArea(Guid.Empty, "Desk", 3));
        AreaBox.SelectedIndex = 0;
        CircleRing.Stroke = new SolidColorBrush(Color.FromRgb(255, 120, 168));
        CircleFill.Fill = CircleRing.Stroke;
        CircleFill.Opacity = 0.34;
        ColourHex.Text = UiText.Translate("#FF78A8");
        HitNumber.Text = UiText.Translate("24");
        foreach (var c in new[] { "#FF78A8", "#62C4FF", "#B7EC6D", "#BF93FF" })
            PaletteSwatches.Children.Add(new Ellipse { Width = 19, Height = 19, Fill = (Brush)new BrushConverter().ConvertFromString(c)!, Margin = new Thickness(0, 0, 8, 0) });
        var root = (FrameworkElement)Content;
        root.Width = Width - 56;
        root.Height = Height - 56;
        root.Measure(new Size(Width, Height));
        root.Arrange(new Rect(0, 0, Width, Height));
        root.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Width, (int)Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(System.IO.Path.Combine(Environment.CurrentDirectory, compact ? "work/preview-compact.png" : "work/preview.png"));
        encoder.Save(output);
        engine.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
