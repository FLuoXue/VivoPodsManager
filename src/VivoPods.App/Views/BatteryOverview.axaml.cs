using Avalonia;
using Avalonia.Controls;

namespace VivoPods.App.Views;

public partial class BatteryOverview : UserControl
{
    public static readonly StyledProperty<double> ArtHeightProperty = AvaloniaProperty.Register<BatteryOverview, double>(nameof(ArtHeight), 110);
    public static readonly StyledProperty<double> BatteryFontSizeProperty = AvaloniaProperty.Register<BatteryOverview, double>(nameof(BatteryFontSize), 24);
    public double ArtHeight { get => GetValue(ArtHeightProperty); set => SetValue(ArtHeightProperty, value); }
    public double BatteryFontSize { get => GetValue(BatteryFontSizeProperty); set => SetValue(BatteryFontSizeProperty, value); }
    public BatteryOverview() => InitializeComponent();
}
