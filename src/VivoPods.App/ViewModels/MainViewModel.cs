using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using VivoPods.App.Services;
using VivoPods.Core.Models;
using VivoPods.Core.Protocol;
using VivoPods.Core.Services;
using VivoPods.Core.Transport;
using VivoPods.Windows;

namespace VivoPods.App.ViewModels;

public sealed record Option(byte Value, string Label) { public override string ToString() => Label; }

public sealed class MainViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    private readonly DeviceDiscovery _discovery = new();
    private readonly SettingsStore _settings;
    private readonly DeviceArtworkProvider _artwork;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DispatcherTimer _reconnect = new() { Interval = TimeSpan.FromSeconds(10) };
    private bool _busy, _manualDisconnect, _disposed;
    private string _message = "选择耳机，开始你的聆听。", _page = "overview";
    private PodDevice? _selectedDevice;
    private readonly Queue<string> _logs = new();
    private readonly object _logGate = new();
    private PodState _displayedState = new();
    public PodManager Manager { get; }
    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action<string, string>? Notification;
    public ObservableCollection<PodDevice> Devices { get; } = [];
    public ObservableCollection<PeerDevice> Peers { get; } = [];
    public IReadOnlyList<string> ModelOptions { get; } = ["自动识别", .. DeviceProfile.All.Select(p => p.Name)];
    public IReadOnlyList<string> Themes { get; } = ["跟随系统", "浅色", "深色"];
    public IReadOnlyList<Option> EqOptions { get; } = [new(0, "标准"), new(1, "清澈人声"), new(2, "超重低音"), new(3, "清亮高音"), new(5, "悠扬听书")];
    public IReadOnlyList<Option> TapOptions { get; } = [new(0, "语音助手"), new(1, "播放 / 暂停"), new(2, "上一首"), new(3, "下一首"), new(5, "翻译"), new(6, "无操作")];
    public IReadOnlyList<Option> CycleOptions { get; } = [new(11, "降噪 / 通透 / 关闭"), new(10, "降噪 / 通透"), new(8, "降噪 / 关闭"), new(9, "通透 / 关闭"), new(255, "无操作")];
    public Option? SelectedEq { get; set; }
    public Option? SelectedLeftTap { get; set; }
    public Option? SelectedRightTap { get; set; }
    public Option? SelectedLeftCycle { get; set; }
    public Option? SelectedRightCycle { get; set; }
    public MainViewModel(bool isolated = false)
    {
        _settings = new(isolated);
        _artwork = new(AddLog);
        Manager = new(device => device.Transport switch
        {
            TransportKind.Demo => new DemoTransport(), TransportKind.Gatt => new GattTransport(), _ => new RfcommTransport()
        }) { Experimental = Experimental };
        Manager.Changed += OnManagerChanged;
        Manager.Log += AddLog;
        ApplyTheme();
        _reconnect.Tick += async (_, _) =>
        {
            if (!_busy && !_manualDisconnect && AutoReconnect && !Manager.Ready && Manager.Phase == ConnectionPhase.Disconnected && SelectedDevice != null && !IsDemo)
                await ConnectAsync();
        };
        _reconnect.Start();
        if (_settings.LoadError != null) Message = _settings.LoadError;
    }
    public PodDevice? SelectedDevice
    {
        get => _selectedDevice;
        set { _selectedDevice = value; Raise(); Raise(nameof(ModelOverride)); }
    }
    public bool Busy { get => _busy; private set { _busy = value; RaiseAll(); } }
    public bool Idle => !Busy;
    public bool CanControl => Manager.Ready && !Busy;
    public bool IsDemo => Manager.Device?.Transport == TransportKind.Demo;
    public string Message { get => _message; set { _message = value; Raise(); } }
    public string DeviceName => Manager.Device?.Name ?? "你的下一段好声音";
    public string DeviceSubtitle => IsDemo ? "演示模式 · 所有数据均为模拟" : Manager.Device == null ? "先在 Windows 蓝牙设置中配对你的 vivo / iQOO 耳机" : $"{Manager.Profile.Name}  ·  {Manager.TransportLabel}";
    public string ConnectionLabel => Manager.Phase switch
    {
        ConnectionPhase.Ready => IsDemo ? "演示中" : "已连接", ConnectionPhase.Connecting => "正在连接", ConnectionPhase.Handshaking => "正在同步", _ => "未连接"
    };
    public string ConnectionDetail => Manager.State.UpdatedAt is { } time && Manager.Ready ? $"最近同步 {time:HH:mm:ss}" : "等待耳机连接";
    public string Address => Manager.Device?.AddressText ?? "—";
    public string ProfileLabel => $"{Manager.Profile.Name} · GAIA v{Manager.Profile.Version}";
    public string Firmware => Manager.State.Firmware;
    public string ModelId => Manager.State.ModelId;
    public string LeftBattery => Manager.State.Left.Text;
    public string RightBattery => Manager.State.Right.Text;
    public string CaseBattery => Manager.State.Case.Text;
    public int LeftLevel => Manager.State.Left.Level ?? 0;
    public int RightLevel => Manager.State.Right.Level ?? 0;
    public int CaseLevel => Manager.State.Case.Level ?? 0;
    public double LeftOpacity => Manager.State.Left.Stale ? .4 : 1;
    public double RightOpacity => Manager.State.Right.Stale ? .4 : 1;
    public double CaseOpacity => Manager.State.Case.Stale ? .4 : 1;
    public string LeftStatus => BatteryStatus(Manager.State.Left, Manager.State.LeftWear);
    public string RightStatus => BatteryStatus(Manager.State.Right, Manager.State.RightWear);
    public string CaseStatus => BatteryStatus(Manager.State.Case, "充电盒");
    private static string BatteryStatus(BatteryReading b, string status) => b.Stale ? b.Level == null ? "暂无数据" : "上次读数" : b.Charging ? "正在充电" : status;
    public bool ShowNoise => Manager.Profile.Has(Features.Noise);
    public bool ShowFind => Manager.Profile.Has(Features.Find);
    public bool ShowWear => Manager.Profile.Has(Features.Wear);
    public bool ShowDual => Manager.Profile.Has(Features.Dual) && !Manager.Profile.DualAlwaysOn;
    public bool ShowForcedDual => Manager.Profile.DualAlwaysOn;
    public bool ShowGame => Experimental && Manager.Profile.Has(Features.Game);
    public bool ShowSpatial => Experimental && Manager.Profile.Has(Features.Spatial);
    public bool HasDevice => Manager.Device != null;
    private DeviceArtwork? Artwork => _artwork.Get(Manager.Profile.Name);
    public Bitmap? LeftImage => Artwork?.Left;
    public Bitmap? RightImage => Artwork?.Right;
    public Bitmap? CaseImage => Artwork?.Case;
    public bool ShowOfficialDeviceArt => Artwork != null;
    public bool ShowGenericDeviceArt => !ShowOfficialDeviceArt;
    public bool NoDevice => !HasDevice;
    public bool NoiseOff => Manager.State.Noise == NoiseMode.Off;
    public bool NoiseAnc => Manager.State.Noise == NoiseMode.Anc;
    public bool NoiseTransparency => Manager.State.Noise == NoiseMode.Transparency;
    public bool ShowAncLevels => Manager.Profile.SupportsAncLevels && NoiseAnc;
    public bool AncBalanced => NoiseAnc && Manager.Profile.SupportsAncLevels && Manager.State.NoiseLevel == (byte)AncLevel.Balanced;
    public bool AncMild => NoiseAnc && Manager.Profile.SupportsAncLevels && Manager.State.NoiseLevel == (byte)AncLevel.Mild;
    public string AncLevelLabel => Manager.State.NoiseLevel switch
    {
        (byte)AncLevel.Balanced => "均衡降噪", (byte)AncLevel.Mild => "轻度降噪",
        null => "档位尚未回报", var value => $"未知档位（0x{value:X2}）"
    };
    public string NoiseLabel => Manager.State.Noise switch { NoiseMode.Anc => Manager.Profile.SupportsAncLevels ? $"降噪已开启 · {AncLevelLabel}" : "降噪已开启", NoiseMode.Off => "降噪已关闭", NoiseMode.Transparency => "通透已开启", _ => "等待耳机同步" };
    public string GameLabel => SwitchLabel(Manager.State.Gaming);
    public string SpatialLabel => SwitchLabel(Manager.State.Spatial);
    public string WearLabel => SwitchLabel(Manager.State.WearDetection);
    private static string SwitchLabel(bool? value) => value switch { true => "已开启", false => "已关闭", _ => "未回报状态" };
    public string FindingLabel => Manager.State.Finding == true ? "响铃中 · 请及时停止" : "让左右耳机同时发出提示音";
    public string DualLabel => Manager.State.MultiStatus is { } state ? $"{Peers.Count} 台设备 · 状态 {state}" : "等待设备列表";
    public bool OverviewPage => _page == "overview";
    public bool GesturesPage => _page == "gestures";
    public bool DevicesPage => _page == "devices";
    public bool SettingsPage => _page == "settings";
    public bool AboutPage => _page == "about";
    public string PageTitle => _page switch { "gestures" => "让操作更顺手", "devices" => "设备连接", "settings" => "按你的习惯来", "about" => "关于 Vivo Pods", _ => "耳机概览" };
    public string PageSubtitle => _page switch { "gestures" => "自定义左右耳手势，音乐尽在指间。", "devices" => "在你的设备之间，自在切换。", "settings" => "为你的桌面，留一份舒适。", "about" => "让好声音，也属于 Windows。", _ => "好声音，从这里开始。" };
    public void Navigate(string page) { _page = page; RaiseAll(); }
    public string Theme { get => _settings.Settings.Theme; set { _settings.Settings.Theme = value; ApplyTheme(); Save(); Raise(); } }
    public bool CloseToTray { get => _settings.Settings.CloseToTray; set { _settings.Settings.CloseToTray = value; Save(); Raise(); } }
    public bool AutoReconnect { get => _settings.Settings.AutoReconnect; set { _settings.Settings.AutoReconnect = value; Save(); Raise(); } }
    public bool Notifications { get => _settings.Settings.Notifications; set { _settings.Settings.Notifications = value; Save(); Raise(); } }
    public bool Experimental { get => _settings.Settings.Experimental; set { _settings.Settings.Experimental = value; Manager.Experimental = value; Save(); RaiseAll(); } }
    public bool Startup { get => SettingsStore.StartupEnabled; set { try { SettingsStore.SetStartup(value); } catch (Exception ex) { Message = ex.Message; } Raise(); } }
    public string ModelOverride
    {
        get => SelectedDevice != null && _settings.Settings.Profiles.TryGetValue(SelectedDevice.Id, out string? name) ? name : "自动识别";
        set
        {
            if (SelectedDevice == null) return;
            if (value == "自动识别") _settings.Settings.Profiles.Remove(SelectedDevice.Id); else _settings.Settings.Profiles[SelectedDevice.Id] = value;
            Save(); Message = "型号设置已保存，重新连接后生效。"; Raise();
        }
    }
    public string SettingsPath => _settings.DirectoryPath;
    private void Save() { try { _settings.Save(); } catch (Exception ex) { Message = $"保存设置失败：{ex.Message}"; } }
    private void ApplyTheme() { if (Application.Current != null) Application.Current.RequestedThemeVariant = Theme switch { "深色" => ThemeVariant.Dark, "浅色" => ThemeVariant.Light, _ => ThemeVariant.Default }; }

    public async Task InitializeAsync(bool demo)
    {
        if (demo) { await DemoAsync(); return; }
        await ScanAsync();
        if (AutoReconnect && SelectedDevice?.IsConnected == true) await ConnectAsync();
    }
    public Task ScanAsync() => RunAsync(async () =>
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        var found = await _discovery.FindAsync(timeout.Token);
        string? selected = SelectedDevice?.Id ?? _settings.Settings.LastDevice;
        Devices.Clear(); foreach (var device in found) Devices.Add(device);
        SelectedDevice = Devices.FirstOrDefault(x => x.Id == selected) ?? Devices.FirstOrDefault();
        Message = Devices.Count == 0 ? "未发现已配对的 vivo / iQOO 耳机。请打开蓝牙设置完成配对。" : $"发现 {Devices.Count} 个耳机连接入口，选择后连接。";
        if (_discovery.LastWarning is { } warning) AddLog(warning);
    });
    public Task ConnectAsync() => RunAsync(async () =>
    {
        if (SelectedDevice == null) { Message = "请先选择耳机"; return; }
        _manualDisconnect = false;
        var profile = ModelOverride == "自动识别" ? null : DeviceProfile.Resolve(ModelOverride);
        await Manager.ConnectAsync(SelectedDevice, profile, _lifetime.Token);
        _settings.Settings.LastDevice = SelectedDevice.Id; Save();
        Message = "耳机已连接，状态会自动同步。";
        Notify("耳机已连接", $"{DeviceName}  ·  左 {LeftBattery} / 右 {RightBattery} / 盒 {CaseBattery}");
    });
    public Task DemoAsync() => RunAsync(async () =>
    {
        _manualDisconnect = true;
        await Manager.ConnectAsync(new("demo", "vivo TWS 5", 0, true, TransportKind.Demo), DeviceProfile.Resolve("vivo TWS 5"), _lifetime.Token);
        Message = "正在预览演示设备，所有操作只作用于模拟数据。";
    });
    public Task DisconnectAsync() => RunAsync(async () => { _manualDisconnect = true; await Manager.DisconnectAsync(); Message = "已断开管理连接。"; });
    public Task RefreshAsync() => RunAsync(async () => { await Manager.RefreshAsync(); Message = "已请求刷新耳机状态。"; });
    public Task SetNoiseAsync(NoiseMode mode) => RunAsync(async () => { await Manager.SetNoiseAsync(mode); Message = "耳机已确认降噪模式。"; });
    public Task SetAncLevelAsync(AncLevel level) => RunAsync(async () =>
    {
        await Manager.SetNoiseAsync(NoiseMode.Anc, level);
        Message = "耳机已确认降噪档位。";
    });
    public Task SetEqAsync() => RunAsync(async () =>
    {
        if (SelectedEq == null) return;
        byte value = SelectedEq.Value;
        await Manager.ChangeAsync(Cmd(0x0118, value), Cmd(0x0218), s => s.Eq == value);
        Message = "耳机已确认音效预设。";
    });
    public Task SetTapAsync(bool right) => RunAsync(async () =>
    {
        var option = right ? SelectedRightTap : SelectedLeftTap;
        if (option == null) return;
        byte value = (byte)(option.Value + (right ? 16 : 0));
        await Manager.ChangeAsync(Cmd(0x0102, value), Cmd(0x0202), s => (right ? s.RightTap : s.LeftTap) == value);
        Message = "耳机已确认双击手势。";
    });
    public Task SetCyclesAsync() => RunAsync(async () =>
    {
        if (SelectedLeftCycle == null || SelectedRightCycle == null) { Message = "请先选择左右耳的长按循环。"; return; }
        byte left = SelectedLeftCycle.Value, right = SelectedRightCycle.Value;
        await Manager.ChangeAsync(Cmd(0x0131, 5, left, right), Cmd(0x0231, 5), s => s.LeftCycle == left && s.RightCycle == right);
        Message = "耳机已确认长按循环设置。";
    });
    public Task ToggleAsync(string feature) => RunAsync(async () =>
    {
        (ushort set, ushort query, bool value) = feature switch
        {
            "wear" => ((ushort)0x0103, (ushort)0x0203, Manager.State.WearDetection != true),
            "game" => ((ushort)0x0151, (ushort)0x0251, Manager.State.Gaming != true),
            _ => ((ushort)0x0139, (ushort)0x0239, Manager.State.Spatial != true)
        };
        await Manager.ChangeAsync(Cmd(set, value ? (byte)1 : (byte)0), Cmd(query), s => (feature == "wear" ? s.WearDetection : feature == "game" ? s.Gaming : s.Spatial) == value);
        Message = "耳机已确认设置。";
    });
    public Task FindAsync(bool start) => RunAsync(async () =>
    {
        await Manager.FindAsync(start);
        Message = start ? "耳机正在响铃，30 秒后自动停止；找到后也可立即点击停止。" : "耳机已停止响铃。";
    });
    public Task SwitchPeerAsync(PeerDevice selected) => RunAsync(async () =>
    {
        var table = Manager.State.Peers.Select(p => p with { Status = (byte)(p.Address == selected.Address ? 2 : p.Status == 2 ? 1 : p.Status) }).ToArray();
        await Manager.ChangeAsync(VivoProtocol.MultiDeviceTable(Manager.Profile, table), Cmd(0x0249), s => s.Peers.Any(p => p.Address == selected.Address && p.Status == 2));
        Message = "耳机已确认活动设备切换。";
    });
    public Task SetDualAsync(bool enable) => RunAsync(async () =>
    {
        byte[] local = IsDemo ? Convert.FromHexString("AABBCC112233") : await DeviceDiscovery.LocalAddressAsync();
        await Manager.AcknowledgeAsync(Cmd(0x014C, [.. local, enable ? (byte)1 : (byte)0]));
        await Manager.RefreshAsync();
        Message = enable ? "耳机已接受开启双设备连接。" : "耳机已接受关闭双设备连接。";
    });
    private GaiaFrame Cmd(ushort command, params byte[] payload) => VivoProtocol.Command(Manager.Profile, command, payload);
    public async Task RunAsync(Func<Task> action)
    {
        if (Busy || _disposed) return;
        Busy = true;
        try { await action(); }
        catch (OperationCanceledException) { Message = "操作已取消或连接超时，请检查耳机连接。"; }
        catch (Exception ex) { Message = ex.Message; AddLog($"ERROR {ex}"); }
        finally { Busy = false; UpdateState(true); }
    }
    private void OnManagerChanged() => Dispatcher.UIThread.Post(() => UpdateState());
    private int _lastLowBucket = 100;
    private void UpdateState(bool resetSelections = false)
    {
        if (_disposed) return;
        var state = Manager.State;
        if (resetSelections || state.Eq != _displayedState.Eq) SelectedEq = EqOptions.FirstOrDefault(x => x.Value == state.Eq);
        if (resetSelections || state.LeftTap != _displayedState.LeftTap) SelectedLeftTap = TapOptions.FirstOrDefault(x => x.Value == state.LeftTap);
        if (resetSelections || state.RightTap != _displayedState.RightTap) SelectedRightTap = TapOptions.FirstOrDefault(x => x.Value + 16 == state.RightTap);
        if (resetSelections || state.LeftCycle != _displayedState.LeftCycle) SelectedLeftCycle = CycleOptions.FirstOrDefault(x => x.Value == state.LeftCycle);
        if (resetSelections || state.RightCycle != _displayedState.RightCycle) SelectedRightCycle = CycleOptions.FirstOrDefault(x => x.Value == state.RightCycle);
        _displayedState = state;
        if (!Peers.SequenceEqual(state.Peers)) { Peers.Clear(); foreach (var p in state.Peers) Peers.Add(p); }
        int lowest = new[] { state.Left, state.Right }.Where(x => !x.Stale && !x.Charging && x.Level != null).Select(x => x.Level!.Value).DefaultIfEmpty(100).Min();
        int bucket = lowest <= 10 ? 10 : lowest <= 20 ? 20 : 100;
        if (bucket < _lastLowBucket && Manager.Ready) Notify("耳机电量提醒", $"耳机剩余电量 {lowest}%，记得充电。 ");
        _lastLowBucket = bucket;
        RaiseAll();
    }
    private void Notify(string title, string body) { if (Notifications && !IsDemo) Notification?.Invoke(title, body); }
    private void AddLog(string text)
    {
        lock (_logGate) { _logs.Enqueue($"{DateTimeOffset.Now:HH:mm:ss.fff} {text}"); while (_logs.Count > 500) _logs.Dequeue(); }
    }
    public string DiagnosticText()
    {
        lock (_logGate) return $"VivoPodsManager 0.1.0\n{ProfileLabel}\n{Manager.TransportLabel}\n\n{string.Join(Environment.NewLine, _logs)}";
    }
    public void OpenBluetooth() => Process.Start(new ProcessStartInfo("ms-settings:bluetooth") { UseShellExecute = true });
    private void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    private void RaiseAll() => PropertyChanged?.Invoke(this, new(null));
    public async ValueTask DisposeAsync()
    {
        _disposed = true; _reconnect.Stop(); _lifetime.Cancel();
        Manager.Changed -= OnManagerChanged;
        await Manager.DisposeAsync();
        _artwork.Dispose();
        _lifetime.Dispose();
    }
}
