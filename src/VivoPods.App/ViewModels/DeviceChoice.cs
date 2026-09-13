using Avalonia.Media.Imaging;
using VivoPods.Core.Models;

namespace VivoPods.App.ViewModels;

public sealed record DeviceChoice(DiscoveredDevice Device, bool IsActive, bool IsConnecting, Bitmap? Thumbnail)
{
    public string Key => Device.Key;
    public string Name => Device.Name;
    public bool IsOnline => Device.IsConnected;
    public bool IsDemo => Device.Endpoints[0].Transport == TransportKind.Demo;
    public bool ShowThumbnail => Thumbnail != null;
    public bool ShowPlaceholder => !ShowThumbnail;
    public string Status => IsDemo ? "演示设备" : IsActive ? "正在使用" : IsConnecting ? "正在连接" : IsOnline ? "Windows 已连接" : "已配对 · 未连接";
    public string ActionLabel => IsActive ? "当前耳机" : IsOnline ? "使用这副耳机" : "请先连接 Windows";
}
