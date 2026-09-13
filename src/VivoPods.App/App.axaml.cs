using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using VivoPods.App.Services;
using VivoPods.App.ViewModels;
using VivoPods.App.Views;
using VivoPods.Windows;

namespace VivoPods.App;

public partial class App : Application
{
    private TrayIcon? _tray;
    private MainViewModel? _vm;
    private MainWindow? _window;
    private SmallWindow? _smallWindow;
    private readonly DispatcherTimer _trayClickTimer = new();
    private readonly DispatcherTimer _dismissSmallTimer = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private bool _trayClickPending, _hideSmallOnSingle;
    private PixelPoint _smallAnchor;
    private EventWaitHandle? _showSignal;
    private RegisteredWaitHandle? _registeredWait;
    private bool _exiting;
    public override void Initialize() => AvaloniaXamlLoader.Load(this);
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            bool smoke = Program.Arguments.Contains("--smoke");
            bool demo = smoke || Program.Arguments.Contains("--demo");
            _vm = new MainViewModel(smoke);
            _window = new MainWindow { DataContext = _vm };
            if (!Program.Arguments.Contains("--minimized")) desktop.MainWindow = _window;
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _window.Closed += async (_, _) => await ExitAsync();
            _window.OpenQuickRequested += ToggleSmallWindow;
            SetupTray();
            if (!smoke)
            {
                _showSignal = new(false, EventResetMode.AutoReset, "Local\\VivoPodsManager.Show");
                _registeredWait = ThreadPool.RegisterWaitForSingleObject(_showSignal, (_, _) => Dispatcher.UIThread.Post(ShowWindow), null, Timeout.Infinite, false);
                _vm.Notification += ShowNotification;
            }
            _vm.PropertyChanged += (_, _) =>
            {
                if (_tray != null) _tray.ToolTipText = $"Vivo Pods · {_vm.ConnectionLabel}\n左 {_vm.LeftBattery} / 右 {_vm.RightBattery} / 盒 {_vm.CaseBattery}";
            };
            // Background initialization also runs for a silent startup; showing the window again never reconnects.
            Dispatcher.UIThread.Post(async () =>
            {
                await _vm.InitializeAsync(demo);
                if (smoke) await SmokeAsync();
            }, DispatcherPriority.Background);
        }
        base.OnFrameworkInitializationCompleted();
    }
    private void SetupTray()
    {
        var canvas = new Canvas { Width = 32, Height = 32 };
        canvas.Children.Add(new Ellipse { Width = 32, Height = 32, Fill = new SolidColorBrush(Color.Parse("#526BEE")) });
        var mark = new TextBlock { Text = "v", FontFamily = new FontFamily("Segoe UI"), FontSize = 31, FontWeight = FontWeight.Bold, Foreground = Brushes.White };
        Canvas.SetLeft(mark, 7); Canvas.SetTop(mark, -6); canvas.Children.Add(mark);
        canvas.Measure(new(32, 32)); canvas.Arrange(new(0, 0, 32, 32));
        using var bmp = new RenderTargetBitmap(new(32, 32), new(96, 96)); bmp.Render(canvas);
        using var stream = new MemoryStream(); bmp.Save(stream); stream.Position = 0;
        var menu = new NativeMenu();
        var show = new NativeMenuItem("打开主窗口"); show.Click += (_, _) => ShowWindow(); menu.Add(show);
        var quick = new NativeMenuItem("快捷小窗"); quick.Click += (_, _) => ToggleSmallWindow(); menu.Add(quick);
        var refresh = new NativeMenuItem("刷新电量"); refresh.Click += async (_, _) => { if (_vm?.CanControl == true) await _vm.RefreshAsync(); }; menu.Add(refresh);
        var reconnect = new NativeMenuItem("重新识别耳机"); reconnect.Click += async (_, _) => { if (_vm?.CanScan == true) await _vm.ConnectAsync(); }; menu.Add(reconnect);
        menu.Add(new NativeMenuItemSeparator());
        var exit = new NativeMenuItem("退出"); exit.Click += async (_, _) => await ExitAsync(); menu.Add(exit);
        _tray = new TrayIcon { Icon = new WindowIcon(stream), ToolTipText = "Vivo Pods Manager", Menu = menu, IsVisible = true };
        _trayClickTimer.Interval = DesktopInteraction.DoubleClickInterval;
        _trayClickTimer.Tick += (_, _) =>
        {
            _trayClickTimer.Stop(); _trayClickPending = false;
            if (_hideSmallOnSingle) _smallWindow?.Hide(); else ShowSmallWindow();
        };
        _dismissSmallTimer.Tick += (_, _) =>
        {
            _dismissSmallTimer.Stop();
            if (!_exiting && !_trayClickPending && _smallWindow?.IsActive == false) _smallWindow.Hide();
        };
        _tray.Clicked += OnTrayClicked;
        TrayIcon.SetIcons(this, [_tray]);
    }
    private void ShowWindow()
    {
        if (_window == null || _exiting) return;
        CancelTrayClick(); _dismissSmallTimer.Stop(); _smallWindow?.Hide();
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) desktop.MainWindow = _window;
        _window.Show(); _window.WindowState = WindowState.Normal; _window.Activate();
    }
    private void CancelTrayClick() { _trayClickTimer.Stop(); _trayClickPending = false; }
    private void OnTrayClicked(object? sender, EventArgs e)
    {
        if (_exiting) return;
        _dismissSmallTimer.Stop();
        if (_trayClickPending) { CancelTrayClick(); ShowWindow(); return; }
        _hideSmallOnSingle = _smallWindow?.IsVisible == true;
        _trayClickPending = true; _trayClickTimer.Start();
    }
    private void ToggleSmallWindow()
    {
        CancelTrayClick(); _dismissSmallTimer.Stop();
        if (_smallWindow?.IsVisible == true) _smallWindow.Hide(); else ShowSmallWindow();
    }
    private void ShowSmallWindow()
    {
        if (_exiting || _vm == null) return;
        if (_smallWindow == null)
        {
            _smallWindow = new SmallWindow { DataContext = _vm };
            _smallWindow.OpenMainRequested += ShowWindow;
            _smallWindow.Deactivated += (_, _) => { if (!_exiting) { _dismissSmallTimer.Stop(); _dismissSmallTimer.Start(); } };
            _smallWindow.PropertyChanged += (_, change) =>
            {
                if (change.Property == Window.BoundsProperty && _smallWindow?.IsVisible == true) PositionSmallWindow();
            };
        }
        var cursor = DesktopInteraction.CursorPosition;
        _smallAnchor = new(cursor.X, cursor.Y);
        _smallWindow.Show(); PositionSmallWindow(); _smallWindow.Activate();
    }
    private void PositionSmallWindow()
    {
        if (_smallWindow == null) return;
        var screen = _smallWindow.Screens.ScreenFromPoint(_smallAnchor) ?? _smallWindow.Screens.Primary;
        if (screen == null) return;
        var area = screen.WorkingArea;
        _smallWindow.MaxHeight = Math.Max(240, area.Height / screen.Scaling - 24);
        int width = (int)Math.Ceiling((_smallWindow.FrameSize?.Width ?? _smallWindow.Bounds.Width) * screen.Scaling);
        int height = (int)Math.Ceiling((_smallWindow.FrameSize?.Height ?? _smallWindow.Bounds.Height) * screen.Scaling);
        _smallWindow.Position = new(Math.Max(area.X + 8, area.Right - width - 12), Math.Max(area.Y + 8, area.Bottom - height - 12));
    }
    private void ShowNotification(string title, string message)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var toast = new Window
            {
                Width = 350, Height = 135, CanResize = false, ShowInTaskbar = false,
                SystemDecorations = SystemDecorations.None, Topmost = true,
                Content = new Border { Padding = new(22), Child = new StackPanel { Spacing = 10, Children =
                {
                    new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeight.SemiBold },
                    new TextBlock { Text = message, FontSize = 12, TextWrapping = TextWrapping.Wrap }
                } } }
            };
            var screen = _window?.Screens.Primary;
            if (screen != null) toast.Position = new(screen.WorkingArea.Right - (int)(366 * screen.Scaling), screen.WorkingArea.Bottom - (int)(151 * screen.Scaling));
            toast.Show();
            DispatcherTimer.RunOnce(toast.Close, TimeSpan.FromSeconds(5));
        });
    }
    private async Task ExitAsync()
    {
        if (_exiting) return;
        _exiting = true;
        CancelTrayClick(); _dismissSmallTimer.Stop();
        if (_smallWindow != null) { _smallWindow.AllowClose = true; _smallWindow.Close(); }
        if (_vm != null) await _vm.DisposeAsync();
        _tray?.Dispose(); _registeredWait?.Unregister(null); _showSignal?.Dispose();
        if (_window != null) _window.AllowClose = true;
        (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
    }
    private async Task SmokeAsync()
    {
        string output = Program.Arguments.SkipWhile(a => a != "--output").Skip(1).FirstOrDefault() ?? "artifacts/ui";
        async Task CaptureAsync(string name, Window? target = null)
        {
            await Task.Delay(160);
            target ??= _window!;
            using var bitmap = new RenderTargetBitmap(new((int)target.Bounds.Width, (int)target.Bounds.Height), new(96, 96));
            bitmap.Render(target);
            bitmap.Save(System.IO.Path.Combine(output, name + ".png"));
        }
        async Task PreviewAsync(string modelName, bool officialArtwork = true)
        {
            await _vm!.RunAsync(() => _vm.Manager.ConnectAsync(
                new("demo-" + modelName, modelName, 0, true, Core.Models.TransportKind.Demo),
                Core.Models.DeviceProfile.Resolve(modelName), CancellationToken.None));
            if (!_vm.Manager.Ready || _vm.ShowOfficialDeviceArt != officialArtwork)
                throw new InvalidOperationException($"{modelName} 的演示设备或图片未正确切换");
            _vm.Message = "正在预览演示设备，所有操作只作用于模拟数据。";
        }
        async Task WaitForTrayClickAsync() => await Task.Delay(_trayClickTimer.Interval + TimeSpan.FromMilliseconds(180));
        async Task CheckTrayAsync()
        {
            _window!.Hide();
            OnTrayClicked(null, EventArgs.Empty);
            await WaitForTrayClickAsync();
            if (_smallWindow?.IsVisible != true || _window.IsVisible)
                throw new InvalidOperationException("托盘单击未独立打开小窗");
            await CaptureAsync("quick-light", _smallWindow);

            var noise = _smallWindow.FindControl<NoiseControls>("QuickNoise")!;
            noise.FindControl<Button>("TransparencyButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var deadline = DateTimeOffset.UtcNow.AddSeconds(3);
            while ((!_vm!.NoiseTransparency || _vm.Busy) && DateTimeOffset.UtcNow < deadline) await Task.Delay(40);
            if (!_vm.NoiseTransparency || !ReferenceEquals(_smallWindow.DataContext, _vm))
                throw new InvalidOperationException("小窗操作未同步到主窗口的耳机状态");
            OnTrayClicked(null, EventArgs.Empty);
            await Task.Delay(60);
            OnTrayClicked(null, EventArgs.Empty);
            await WaitForTrayClickAsync();
            if (!_window.IsVisible || _smallWindow.IsVisible)
                throw new InvalidOperationException("托盘双击未打开主窗口，或延迟单击误弹小窗");

            _vm.Theme = "深色";
            OnTrayClicked(null, EventArgs.Empty);
            await WaitForTrayClickAsync();
            await CaptureAsync("quick-dark", _smallWindow);
            OnTrayClicked(null, EventArgs.Empty);
            await WaitForTrayClickAsync();
            if (_smallWindow.IsVisible) throw new InvalidOperationException("再次单击托盘未收起小窗");
            ToggleSmallWindow();
            _smallWindow.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
            if (_smallWindow.IsVisible) throw new InvalidOperationException("Esc 未收起小窗");
            ToggleSmallWindow();
            _smallWindow.Close();
            if (_smallWindow.IsVisible || !_vm.Manager.Ready) throw new InvalidOperationException("关闭小窗影响了耳机连接");
            ToggleSmallWindow();
            _window.Activate();
            await Task.Delay(350);
            if (_smallWindow.IsVisible && !_smallWindow.IsActive) throw new InvalidOperationException("小窗失焦后未收起");
            ShowWindow();
            _vm.Theme = "浅色";
            await _vm.SetNoiseAsync(Core.Models.NoiseMode.Anc);
        }
        try
        {
            Directory.CreateDirectory(output);
            if (Program.Arguments.Contains("--minimized"))
            {
                if (_window!.IsVisible || _vm!.Manager.Ready != true)
                    throw new InvalidOperationException("静默启动未在隐藏主窗口的情况下初始化耳机");
                ShowWindow();
            }
            _vm!.Theme = "浅色";
            await Task.Delay(400);
            if (_vm?.Manager.Ready != true || _vm.LeftBattery != "86%") throw new InvalidOperationException("演示初始化未同步电量");
            using (var artwork = new DeviceArtworkProvider())
            {
                foreach (var profile in Core.Models.DeviceProfile.All.Where(p => p.Name.Contains(" TWS ", StringComparison.Ordinal)))
                {
                    var images = artwork.Get(profile.Name) ?? throw new InvalidOperationException($"缺少 {profile.Name} 的官方图片");
                    if (images.Left.PixelSize.Width <= 0 || images.Right.PixelSize.Width <= 0 || images.Case.PixelSize.Width <= 0)
                        throw new InvalidOperationException($"{profile.Name} 的图片解码失败");
                }
            }
            await _vm.SetNoiseAsync(Core.Models.NoiseMode.Transparency);
            if (!_vm.NoiseTransparency) throw new InvalidOperationException("降噪交互未同步");
            await _vm.SetNoiseAsync(Core.Models.NoiseMode.Anc);
            // Unrelated battery reports must not erase an in-progress EQ selection.
            _vm.SelectedEq = _vm.EqOptions[2];
            await _vm.Manager.RefreshAsync();
            await Task.Delay(100);
            if (_vm.SelectedEq?.Value != 2) throw new InvalidOperationException("后台刷新覆盖了尚未提交的 EQ 选择");
            await _vm.SetEqAsync();
            if (_vm.Manager.State.Eq != 2) throw new InvalidOperationException("EQ 交互未同步");
            _vm.SelectedLeftTap = _vm.TapOptions[2]; await _vm.SetTapAsync(false);
            if (_vm.Manager.State.LeftTap != 2) throw new InvalidOperationException("左耳手势交互未同步");
            _vm.SelectedLeftCycle = _vm.CycleOptions[1]; _vm.SelectedRightCycle = _vm.CycleOptions[2];
            await _vm.SetCyclesAsync();
            if (_vm.Manager.State.LeftCycle != 10 || _vm.Manager.State.RightCycle != 8) throw new InvalidOperationException("长按交互未同步");
            await _vm.SwitchPeerAsync(_vm.Peers[1]);
            if (_vm.Manager.State.Peers[1].Status != 2) throw new InvalidOperationException("多设备切换未同步");
            foreach (string page in new[] { "overview", "gestures", "devices", "settings", "about" })
            {
                _vm.Navigate(page);
                await CaptureAsync(page);
            }
            _vm.Theme = "深色"; _vm.Navigate("overview");
            await CaptureAsync("overview-dark");

            // Exercise the embedded model images in a simulated session, including model changes and stale readings.
            await PreviewAsync("vivo TWS Air3 Pro");
            if (!_vm.Manager.Ready || _vm.LeftBattery != "86%" || _vm.RightBattery != "92%" || _vm.CaseBattery != "64%")
                throw new InvalidOperationException("Air3 Pro 演示初始化未同步左右耳及充电盒电量");
            await CaptureAsync("overview-air3-pro-dark");
            _vm.Theme = "浅色";
            await CaptureAsync("overview-air3-pro");
            await CheckTrayAsync();
            double width = _window!.Width, height = _window.Height;
            _window.Width = _window.MinWidth; _window.Height = _window.MinHeight;
            await CaptureAsync("overview-air3-pro-compact");
            _window.Width = width; _window.Height = height;
            await _vm.DisconnectAsync();
            await CaptureAsync("overview-air3-pro-disconnected");
            ToggleSmallWindow();
            await CaptureAsync("quick-disconnected", _smallWindow);
            ShowWindow();
            await _vm.DemoAsync();
            await CaptureAsync("overview-tws5-restored");
            await PreviewAsync("vivo TWS 1");
            await CaptureAsync("overview-tws1");
            await PreviewAsync("vivo TWS 4 HiFi");
            await CaptureAsync("overview-tws4-hifi");
            await PreviewAsync("vivo TWS 未识别型号", officialArtwork: false);
            await CaptureAsync("overview-generic-fallback");
            await using (var empty = new MainViewModel(isolated: true))
            {
                _window.DataContext = empty;
                await CaptureAsync("overview-waiting");
                _smallWindow!.DataContext = empty;
                ToggleSmallWindow();
                await CaptureAsync("quick-waiting", _smallWindow);
                ShowWindow();
                _window.DataContext = _vm; _smallWindow.DataContext = _vm;
            }
            await File.WriteAllTextAsync(System.IO.Path.Combine(output, "result.txt"), "PASS: desktop startup, demo handshake, battery, noise changes, five pages, light/dark render, all TWS artwork resources, compact layout, disconnect, model switch, unknown fallback, tray single/double click, shared quick controls, popup close/dismiss, waiting state");
        }
        catch (Exception ex)
        {
            Directory.CreateDirectory(output);
            await File.WriteAllTextAsync(System.IO.Path.Combine(output, "result.txt"), "FAIL: " + ex);
            Environment.ExitCode = 1;
        }
        finally { await ExitAsync(); }
    }
}
