using VivoPods.Core.Models;
using VivoPods.Core.Protocol;

namespace VivoPods.Core.Transport;

public interface IPodTransport : IAsyncDisposable
{
    event Action<GaiaFrame>? FrameReceived;
    event Action<Exception?>? Disconnected;
    string Label { get; }
    Task ConnectAsync(PodDevice device, CancellationToken ct);
    Task SendAsync(GaiaFrame frame, CancellationToken ct);
}

public interface IDeviceDiscovery
{
    Task<IReadOnlyList<PodDevice>> FindAsync(CancellationToken ct);
}
