using System.Diagnostics;
using System.Text.Json;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using VivoPods.Core.Models;

namespace VivoPods.App.Services;

public sealed record DeviceArtworkInfo(string Name, int Model, string Directory);
public sealed record DeviceArtwork(Bitmap Left, Bitmap Right, Bitmap Case) : IDisposable
{
    public void Dispose() { Left.Dispose(); Right.Dispose(); Case.Dispose(); }
}

/// <summary>Model artwork is loaded from embedded resources and retained until the view model closes.</summary>
public sealed class DeviceArtworkProvider : IDisposable
{
    private static readonly Uri ResourceRoot = new($"avares://{typeof(DeviceArtworkProvider).Assembly.GetName().Name}/Assets/Devices/");
    private static readonly Lazy<IReadOnlyDictionary<string, DeviceArtworkInfo>> Catalog = new(LoadCatalog);
    private readonly Dictionary<string, DeviceArtwork?> _images = [];
    private readonly Action<string>? _log;
    private bool _disposed;

    public DeviceArtworkProvider(Action<string>? log = null) => _log = log;

    private static IReadOnlyDictionary<string, DeviceArtworkInfo> LoadCatalog()
    {
        try
        {
            using var stream = AssetLoader.Open(new Uri(ResourceRoot, "catalog.json"));
            var entries = JsonSerializer.Deserialize<DeviceArtworkInfo[]>(stream) ?? [];
            return entries.ToDictionary(entry => DeviceProfile.Normalize(entry.Name));
        }
        catch (Exception ex) when (ex is IOException or JsonException or ArgumentException)
        {
            Trace.WriteLine($"Device artwork catalog: {ex.Message}");
            return new Dictionary<string, DeviceArtworkInfo>();
        }
    }

    public DeviceArtwork? Get(string modelName)
    {
        if (_disposed) return null;
        string name = DeviceProfile.Normalize(DeviceProfile.Resolve(modelName).Name);
        if (!Catalog.Value.TryGetValue(name, out var entry)) return null;
        // Some models intentionally share artwork according to the official file_model mapping.
        if (_images.TryGetValue(entry.Directory, out var cached)) return cached;
        Bitmap? left = null, right = null, box = null;
        try
        {
            left = Load(entry.Directory, "left");
            right = Load(entry.Directory, "right");
            box = Load(entry.Directory, "case");
            return _images[entry.Directory] = new(left, right, box);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException)
        {
            left?.Dispose(); right?.Dispose(); box?.Dispose();
            _log?.Invoke($"无法加载 {entry.Name} 的设备图片：{ex.Message}");
            return _images[entry.Directory] = null;
        }
    }

    private static Bitmap Load(string directory, string part)
    {
        using var stream = AssetLoader.Open(new Uri(ResourceRoot, $"{directory}/{part}.png"));
        return new Bitmap(stream);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var artwork in _images.Values) artwork?.Dispose();
        _images.Clear();
    }
}
