using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using VivoPods.App.ViewModels;

namespace VivoPods.App.Views;

public partial class SmallWindow : Window
{
    public event Action? OpenMainRequested;
    public bool AllowClose { get; set; }
    public SmallWindow()
    {
        InitializeComponent();
        Closing += (_, e) => { if (!AllowClose) { e.Cancel = true; Hide(); } };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) { Hide(); e.Handled = true; } };
    }
    private void OpenMain(object? sender, RoutedEventArgs e) => OpenMainRequested?.Invoke();
    private void OpenMainFromHeader(object? sender, TappedEventArgs e) => OpenMainRequested?.Invoke();
    private void HideWindow(object? sender, RoutedEventArgs e) => Hide();
    private async void Reconnect(object? sender, RoutedEventArgs e) { if (DataContext is MainViewModel vm) await vm.ConnectAsync(); }
}
