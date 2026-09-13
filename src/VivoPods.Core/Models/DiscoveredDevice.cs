namespace VivoPods.Core.Models;

/// <summary>One physical headset, potentially exposed through both RFCOMM and BLE.</summary>
public sealed record DiscoveredDevice(string Key, IReadOnlyList<PodDevice> Endpoints)
{
    public string Name => Endpoints[0].Name;
    public bool IsConnected => Endpoints.Any(device => device.IsConnected);
    public static string KeyFor(PodDevice device) => device.Address != 0 ? $"address:{device.Address:X12}" : device.Id;

    public static IReadOnlyList<DiscoveredDevice> Group(IEnumerable<PodDevice> devices) => devices
        .Where(device => device.Transport != TransportKind.Demo && DeviceProfile.IsFamily(device.Name))
        .GroupBy(KeyFor)
        .Select(group => new DiscoveredDevice(group.Key, group.OrderBy(device => device.Transport)
            .ThenBy(device => device.Id, StringComparer.Ordinal).ToArray()))
        .OrderByDescending(device => device.IsConnected)
        .ThenBy(device => device.Name, StringComparer.OrdinalIgnoreCase).ThenBy(device => device.Key, StringComparer.Ordinal)
        .ToArray();
}
