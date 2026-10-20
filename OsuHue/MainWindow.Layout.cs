using System.Windows;
using System.Windows.Controls;

namespace OsuHue;

public partial class MainWindow
{
    private bool? compactLayout;

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e) => ApplyResponsiveLayout(e.NewSize.Width);

    private void ApplyResponsiveLayout(double width)
    {
        if (MainLayout is null || ActionLayout is null)
            return;
        bool compact = width < 940;
        if (compactLayout == compact)
            return;
        compactLayout = compact;

        MainLayout.ColumnDefinitions[0].Width = compact ? new(1, GridUnitType.Star) : new(340);
        MainLayout.ColumnDefinitions[1].Width = new(compact ? 0 : 20);
        MainLayout.ColumnDefinitions[2].Width = compact ? new(0) : new(1, GridUnitType.Star);
        Grid.SetColumn(PreviewPane, compact ? 0 : 2);
        Grid.SetRow(PreviewPane, 0);
        Grid.SetColumn(SetupPane, 0);
        Grid.SetRow(SetupPane, compact ? 1 : 0);
        Grid.SetRowSpan(SetupPane, compact ? 1 : 2);
        Grid.SetColumn(EffectsPane, compact ? 0 : 2);
        Grid.SetRow(EffectsPane, compact ? 2 : 1);

        ActionLayout.ColumnDefinitions[0].Width = new(1, GridUnitType.Star);
        ActionLayout.ColumnDefinitions[1].Width = compact ? new(1, GridUnitType.Star) : GridLength.Auto;
        ActionLayout.ColumnDefinitions[2].Width = compact ? new(0) : GridLength.Auto;
        Grid.SetColumnSpan(TempoPane, compact ? 2 : 1);
        TempoPane.Margin = compact ? new(0, 0, 0, 12) : new(0, 0, 16, 0);
        Grid.SetRow(StartButton, compact ? 1 : 0);
        Grid.SetColumn(StartButton, compact ? 0 : 1);
        Grid.SetRow(DemoButton, compact ? 1 : 0);
        Grid.SetColumn(DemoButton, compact ? 1 : 2);
    }
}
