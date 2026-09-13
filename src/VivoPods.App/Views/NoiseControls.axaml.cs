using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using VivoPods.App.ViewModels;
using VivoPods.Core.Models;

namespace VivoPods.App.Views;

public partial class NoiseControls : UserControl
{
    public static readonly StyledProperty<double> ModeHeightProperty = AvaloniaProperty.Register<NoiseControls, double>(nameof(ModeHeight), 62);
    public double ModeHeight { get => GetValue(ModeHeightProperty); set => SetValue(ModeHeightProperty, value); }
    public NoiseControls() => InitializeComponent();
    private async void SetNoise(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
            await vm.SetNoiseAsync(((sender as Button)?.Tag as string) switch { "anc" => NoiseMode.Anc, "trans" => NoiseMode.Transparency, _ => NoiseMode.Off });
    }
    private async void SetAncLevel(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
            await vm.SetAncLevelAsync((sender as Button)?.Tag as string == "mild" ? AncLevel.Mild : AncLevel.Balanced);
    }
}
