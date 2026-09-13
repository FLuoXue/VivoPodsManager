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
    private readonly DeviceConnectionCoordinator _connection;
    private readonly BluetoothDeviceWatcher _watcher = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DispatcherTimer _reconnect = new() { Interval = TimeSpan.FromSeconds(10) };
    private readonly DispatcherTimer _deviceChange = new() { Interval = TimeSpan.FromMilliseconds(450) };
    private bool _busy, _disposed, _initialized, _refreshing, _refreshPending;
    private string? _lastConnectionError;
    private string _message = "自动识别 Windows 已连接的耳机。", _page = "overview";
    private PodDevice? _selectedDevice;
    private readonly Queue<string> _logs = new();
    private readonly object _logGate = new();
    private PodState _displayedState = new();
    public PodManager Manager { get; }
    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action<string, string>? Notification;
    public ObservableCollection<DeviceChoice> Devices { get; } = [];
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
        _connection = new(_discovery, Manager, ResolveProfile)
        {
            Automatic = AutoReconnect, PreferredKey = _settings.Settings.LastDeviceKey,
            PreferredEndpointId = _settings.Settings.LastDevice
        };
        _connection.Changed += OnConnectionChanged;
        _connection.Log += AddLog;
        _connection.Connected += device =>
        {
            SelectedDevice = device;
            _settings.Settings.LastDevice = device.Id;
            _settings.Settings.LastDeviceKey = DiscoveredDevice.KeyFor(device);
            Save();
            Message = $"已自动连接 {device.Name}，状态会持续同步。";
            Notify("耳机已连接", $"{device.Name}  ·  左 {LeftBattery} / 右 {RightBattery} / 盒 {CaseBattery}");
        };
        ApplyTheme();
        _reconnect.Tick += (_, _) => ScheduleDeviceRefresh();
        _deviceChange.Tick += async (_, _) => { _deviceChange.Stop(); await CheckDevicesAsync(); };
        _watcher.Changed += OnBluetoothChanged;
        _watcher.Warning += AddLog;
        if (_settings.LoadError != null) Message = _settings.LoadError;
    }
    public PodDevice? SelectedDevice
    {
        get => _selectedDevice;
        set { _selectedDevice = value; Raise(); Raise(nameof(ModelOverride)); }
    }
    public bool Busy { get => _busy || _connection.IsConnecting; private set { _busy = value; RaiseAll(); } }
    public bool Idle => !Busy;
    public bool IsScanning => _connection.IsScanning;
    public bool CanScan => !Busy && !IsScanning;
    public bool CanControl => Manager.Ready && !Busy;
    public bool IsDemo => Manager.Device?.Transport == TransportKind.Demo;
    public string Message { get => _message; set { _message = value; Raise(); } }
    public string DeviceName => Manager.Device?.Name ?? "等待耳机上线";
    public string DeviceSubtitle => IsDemo ? "演示设备 · 模拟数据" : Manager.Ready ? "电量、佩戴与控制状态持续同步" : "连接 Windows 后自动识别并同步";
    public string ConnectionLabel => Manager.Phase switch
    {
        ConnectionPhase.Ready => IsDemo ? "演示中" : "已连接", ConnectionPhase.Connecting => "正在连接", ConnectionPhase.Handshaking => "正在同步",
        _ => IsScanning ? "正在查找" : _connection.Paused && !IsDemo ? "已暂停" : "未连接"
    };
    public bool HasDiscoveredDevices => Devices.Count > 0;
    public bool NoDiscoveredDevices => !HasDiscoveredDevices;
    public bool ShowConnectionHelp => !Manager.Ready;
    public bool IsConnected => Manager.Ready;
    public string ConnectionAction => _connection.Paused || IsDemo ? "继续连接" : "重新连接";
    public string ConnectionHint => IsDemo ? "正在体验模拟设备，可随时切回真实耳机。" : _connection.LastError ??
        (_connection.Paused ? "自动连接已暂停，点击继续连接即可恢复。" :
        !AutoReconnect ? "自动连接已关闭，可点击重新连接识别在线耳机。" :
        Manager.Ready ? "多副耳机在线时，可在左侧切换。" : "在 Windows 中配对并连接耳机，程序会自动识别，无需选择连接入口。");
    public string DeviceListHint => IsDemo ? "演示设备 · 不连接真实蓝牙" : Devices.Count == 0 ? "等待发现耳机" : $"{Devices.Count(device => device.IsOnline)} 副在线 · {Devices.Count} 副已配对";
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
    public int QuickSettingsColumn => ShowNoise ? 1 : 0;
    public int QuickSettingsSpan => ShowNoise ? 1 : 2;
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
    public string PageTitle => _page switch { "gestures" => "手势控制", "devices" => "设备连接", "settings" => "应用设置", "about" => "关于 Vivo Pods", _ => "耳机概览" };
    public string PageSubtitle => _page switch { "gestures" => "分别设置左右耳的操作习惯", "devices" => "自动识别、切换耳机与管理多设备连接", "settings" => "外观、自动连接与桌面偏好", "about" => "让好声音，也属于 Windows", _ => "电量与常用控制，随手可达" };
    public void Navigate(string page) { _page = page; RaiseAll(); }
    public string Theme { get => _settings.Settings.Theme; set { _settings.Settings.Theme = value; ApplyTheme(); Save(); Raise(); } }
    public bool CloseToTray { get => _settings.Settings.CloseToTray; set { _settings.Settings.CloseToTray = value; Save(); Raise(); } }
    public bool AutoReconnect
    {
        get => _settings.Settings.AutoReconnect;
        set
        {
            _settings.Settings.AutoReconnect = value; _connection.Automatic = value; Save();
            if (!value) _connection.Pause();
            else if (_initialized && !IsDemo) { _connection.Resume(); ScheduleDeviceRefresh(); }
            RaiseAll();
        }
    }
    public bool Notifications { get => _settings.Settings.Notifications; set { _settings.Settings.Notifications = value; Save(); Raise(); } }
    public bool Experimental { get => _settings.Settings.Experimental; set { _settings.Settings.Experimental = value; Manager.Experimental = value; Save(); RaiseAll(); } }
    public bool Startup { get => SettingsStore.StartupEnabled; set { try { SettingsStore.SetStartup(value); } catch (Exception ex) { Message = ex.Message; } Raise(); } }
    public string ModelOverride
    {
        get => SelectedDevice != null ? ResolveProfile(SelectedDevice)?.Name ?? "自动识别" : "自动识别";
        set
        {
            if (SelectedDevice == null) return;
            string key = DiscoveredDevice.KeyFor(SelectedDevice);
            if (value == "自动识别") { _settings.Settings.Profiles.Remove(key); _settings.Settings.Profiles.Remove(SelectedDevice.Id); }
            else _settings.Settings.Profiles[key] = value;
            Save(); Message = "型号设置已保存，重新连接后生效。"; Raise();
        }
    }
    public string SettingsPath => _settings.DirectoryPath;
    private DeviceProfile? ResolveProfile(PodDevice device)
    {
        if (_settings.Settings.Profiles.TryGetValue(DiscoveredDevice.KeyFor(device), out string? name) ||
            _settings.Settings.Profiles.TryGetValue(device.Id, out name)) return DeviceProfile.Resolve(name);
        return null;
    }
    private void Save() { try { _settings.Save(); } catch (Exception ex) { Message = $"保存设置失败：{ex.Message}"; } }
    private void ApplyTheme() { if (Application.Current != null) Application.Current.RequestedThemeVariant = Theme switch { "深色" => ThemeVariant.Dark, "浅色" => ThemeVariant.Light, _ => ThemeVariant.Default }; }

    public async Task InitializeAsync(bool demo)
    {
        if (_initialized) return;
        _initialized = true;
        if (demo) { await DemoAsync(); return; }
        _watcher.Start(); _reconnect.Start();
        await CheckDevicesAsync();
    }
    private void OnBluetoothChanged() => Dispatcher.UIThread.Post(ScheduleDeviceRefresh);
    private void ScheduleDeviceRefresh()
    {
        if (_disposed || !_initialized || IsDemo) return;
        _refreshPending = true;
        if (!_deviceChange.IsEnabled) _deviceChange.Start();
    }
    private async Task CheckDevicesAsync()
    {
        if (_disposed || IsDemo) return;
        if (Busy || _refreshing) { ScheduleDeviceRefresh(); return; }
        _refreshing = true; _refreshPending = false;
        try
        {
            await _connection.RefreshAsync(_lifetime.Token);
            if (_discovery.LastWarning is { } warning) AddLog(warning);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) { Message = "暂时无法检查蓝牙设备。"; AddLog(ex.ToString()); }
        finally { _refreshing = false; if (_refreshPending) ScheduleDeviceRefresh(); }
    }
    public Task ScanAsync() => CheckDevicesAsync();
    public Task ConnectAsync() => RunAsync(async () =>
    {
        if (IsDemo) await _connection.DisconnectAsync(_lifetime.Token);
        _watcher.Start(); _reconnect.Start();
        await _connection.RetryAsync(_lifetime.Token);
        if (!Manager.Ready) Message = ConnectionHint;
    });
    public Task SelectDeviceAsync(DeviceChoice choice) => RunAsync(async () =>
    {
        if (choice.IsDemo) { Navigate("overview"); return; }
        if (!choice.IsOnline) { Message = "请先在 Windows 蓝牙设置中连接这副耳机。"; return; }
        _watcher.Start(); _reconnect.Start();
        await _connection.SelectAsync(choice.Key, _lifetime.Token);
        Navigate("overview");
        if (!Manager.Ready) Message = ConnectionHint;
    });
    public Task DemoAsync() => RunAsync(async () =>
    {
        _reconnect.Stop(); _deviceChange.Stop();
        await _connection.DemoAsync(ct: _lifetime.Token);
        Message = "正在预览演示设备，所有操作只作用于模拟数据。";
    });
    public Task DisconnectAsync() => RunAsync(async () => { await _connection.DisconnectAsync(_lifetime.Token); Message = "已暂停自动连接，耳机在 Windows 中的音频连接不受影响。"; });
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
        finally { Busy = false; UpdateState(true); if (_refreshPending) ScheduleDeviceRefresh(); }
    }
    private void OnConnectionChanged() => Dispatcher.UIThread.Post(() =>
    {
        if (_disposed) return;
        UpdateDeviceChoices();
        if (_connection.LastError is { } error && error != _lastConnectionError) Message = error;
        _lastConnectionError = _connection.LastError;
        RaiseAll();
    });
    private void UpdateDeviceChoices()
    {
        string? active = Manager.Device is { } device ? DiscoveredDevice.KeyFor(device) : null;
        var discovered = IsDemo && Manager.Device is { } demo ? [new DiscoveredDevice(DiscoveredDevice.KeyFor(demo), [demo])] : _connection.Devices;
        var choices = discovered.Select(device => new DeviceChoice(device, device.Key == active && Manager.Ready,
            device.Key == active && Manager.Phase is ConnectionPhase.Connecting or ConnectionPhase.Handshaking, _artwork.Get(device.Name)?.Case)).ToArray();
        if (!Devices.Select(device => (device.Key, device.Name, device.IsOnline, device.IsActive, device.IsConnecting))
            .SequenceEqual(choices.Select(device => (device.Key, device.Name, device.IsOnline, device.IsActive, device.IsConnecting))))
        { Devices.Clear(); foreach (var choice in choices) Devices.Add(choice); }
    }
    private void OnManagerChanged() => Dispatcher.UIThread.Post(() => UpdateState());
    private int _lastLowBucket = 100;
    private void UpdateState(bool resetSelections = false)
    {
        if (_disposed) return;
        if (Manager.Device is { } device && device != SelectedDevice) SelectedDevice = device;
        UpdateDeviceChoices();
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
        _disposed = true; _reconnect.Stop(); _deviceChange.Stop(); _watcher.Dispose(); _lifetime.Cancel();
        _connection.Changed -= OnConnectionChanged;
        await _connection.DisposeAsync();
        Manager.Changed -= OnManagerChanged;
        await Manager.DisposeAsync();
        _artwork.Dispose();
        _lifetime.Dispose();
    }
}
