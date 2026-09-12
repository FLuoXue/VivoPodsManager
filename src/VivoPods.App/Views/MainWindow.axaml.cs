using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using VivoPods.App.ViewModels;
using VivoPods.Core.Models;

namespace VivoPods.App.Views;

public partial class MainWindow : Window
{
    public MainViewModel ViewModel => (MainViewModel)DataContext!;
    public bool AllowClose { get; set; }
    public MainWindow()
    {
        InitializeComponent();
        Closing += (_, e) => { if (!AllowClose && DataContext is MainViewModel vm && vm.CloseToTray) { e.Cancel = true; Hide(); } };
    }
    private void Navigate(object? sender, RoutedEventArgs e) { if (sender is Button { Tag: string page }) ViewModel.Navigate(page); }
    private async void Scan(object? sender, RoutedEventArgs e) => await ViewModel.ScanAsync();
    private async void Connect(object? sender, RoutedEventArgs e) => await ViewModel.ConnectAsync();
    private async void Demo(object? sender, RoutedEventArgs e) => await ViewModel.DemoAsync();
    private async void Disconnect(object? sender, RoutedEventArgs e) => await ViewModel.DisconnectAsync();
    private async void Refresh(object? sender, RoutedEventArgs e) => await ViewModel.RefreshAsync();
    private void BluetoothSettings(object? sender, RoutedEventArgs e) => ViewModel.OpenBluetooth();
    private async void Noise(object? sender, RoutedEventArgs e) => await ViewModel.SetNoiseAsync(((sender as Button)?.Tag as string) switch { "anc" => NoiseMode.Anc, "trans" => NoiseMode.Transparency, _ => NoiseMode.Off });
    private async void AncLevelClick(object? sender, RoutedEventArgs e) => await ViewModel.SetAncLevelAsync(
        (sender as Button)?.Tag as string == "mild" ? AncLevel.Mild : AncLevel.Balanced);
    private async void ApplyEq(object? sender, RoutedEventArgs e) => await ViewModel.SetEqAsync();
    private async void ApplyTap(object? sender, RoutedEventArgs e) => await ViewModel.SetTapAsync((sender as Button)?.Tag as string == "right");
    private async void ApplyCycles(object? sender, RoutedEventArgs e) => await ViewModel.SetCyclesAsync();
    private async void ToggleFeature(object? sender, RoutedEventArgs e) => await ViewModel.ToggleAsync((sender as Button)?.Tag as string ?? "wear");
    private async void StopFind(object? sender, RoutedEventArgs e) => await ViewModel.FindAsync(false);
    private async void SwitchPeer(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: PeerDevice peer }) await ViewModel.SwitchPeerAsync(peer);
    }
    private async void Dual(object? sender, RoutedEventArgs e)
    {
        bool enable = (sender as Button)?.Tag as string == "enable";
        if (enable || await ConfirmAsync("关闭双设备连接", "耳机可能断开另一台设备的连接。", "关闭双设备连接"))
            await ViewModel.SetDualAsync(enable);
    }
    private async void Find(object? sender, RoutedEventArgs e)
    {
        if (await ConfirmAsync("查找耳机", "耳机会发出较响的提示音。请先从耳朵中取下左右耳机，再开始查找。", "已取下，开始响铃"))
            await ViewModel.FindAsync(true);
    }
    public async Task<bool> ConfirmAsync(string title, string description, string action)
    {
        var dialog = new Window { Title = title, Width = 410, Height = 210, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var yes = new Button { Content = action, Classes = { "primary" } };
        var no = new Button { Content = "取消" };
        yes.Click += (_, _) => dialog.Close(true); no.Click += (_, _) => dialog.Close(false);
        dialog.Content = new StackPanel { Margin = new(24), Spacing = 22, Children =
        {
            new TextBlock { Text = description, TextWrapping = Avalonia.Media.TextWrapping.Wrap, FontSize = 14 },
            new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 10, Children = { no, yes } }
        } };
        return await dialog.ShowDialog<bool>(this);
    }
    private async void ExportLog(object? sender, RoutedEventArgs e)
    {
        try
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { Title = "导出诊断日志", SuggestedFileName = $"VivoPods-{DateTime.Now:yyyyMMdd-HHmmss}.txt", DefaultExtension = "txt" });
            if (file == null) return;
            await using var stream = await file.OpenWriteAsync();
            using var writer = new StreamWriter(stream);
            await writer.WriteAsync(ViewModel.DiagnosticText());
            ViewModel.Message = "诊断日志已导出。";
        }
        catch (Exception ex) { ViewModel.Message = $"导出失败：{ex.Message}"; }
    }
    private void OpenLink(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string url }) Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }
}
