using Avalonia;
using Avalonia.Controls;

namespace VivoPods.App.Views;

public enum EarbudPart { Left, Case, Right }

public partial class EarbudsArt : UserControl
{
    public static readonly StyledProperty<EarbudPart> PartProperty =
        AvaloniaProperty.Register<EarbudsArt, EarbudPart>(nameof(Part));

    public EarbudPart Part { get => GetValue(PartProperty); set => SetValue(PartProperty, value); }

    public EarbudsArt()
    {
        InitializeComponent();
        UpdatePart();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == PartProperty) UpdatePart();
    }

    private void UpdatePart()
    {
        if (LeftArt == null || CaseArt == null || RightArt == null) return;
        LeftArt.IsVisible = Part == EarbudPart.Left;
        CaseArt.IsVisible = Part == EarbudPart.Case;
        RightArt.IsVisible = Part == EarbudPart.Right;
    }
}
