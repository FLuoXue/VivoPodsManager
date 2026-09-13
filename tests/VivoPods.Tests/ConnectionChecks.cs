using VivoPods.Core.Models;
using VivoPods.Core.Protocol;
using VivoPods.Core.Services;
using VivoPods.Core.Transport;

internal static class ConnectionChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var a = new PodDevice("classic-a", "vivo TWS 1", 1, false);
        var b = new PodDevice("classic-b", "vivo TWS 2", 2, true);
        var bleB = b with { Id = "ble-b", Transport = TransportKind.Gatt };
        var groups = DiscoveredDevice.Group([a, b, bleB, b with { Id = "other", Address = 3 }]);
        check(groups.Count == 3 && groups.Single(device => device.Key == DiscoveredDevice.KeyFor(b)).Endpoints.Count == 2,
            "device list merges transports by address, never by name");

        var discovery = new FakeDiscovery { Devices = [a, b, bleB] };
        var attempts = new List<string>();
        await using (var manager = new PodManager(device => { attempts.Add(device.Id); return new DemoTransport(); }))
        await using (var connection = new DeviceConnectionCoordinator(discovery, manager))
        {
            connection.PreferredKey = DiscoveredDevice.KeyFor(a);
            await connection.RefreshAsync();
            check(manager.Ready && manager.Device?.Id == b.Id && attempts.Count == 1,
                "startup selects an online headset instead of an offline saved device");
            check(manager.Device?.Transport == TransportKind.Rfcomm, "automatic connection prefers RFCOMM over duplicate BLE");

            discovery.Devices = [a with { IsConnected = true }, b, bleB];
            await connection.RefreshAsync();
            check(manager.Device?.Id == b.Id && attempts.Count == 1, "device refresh preserves a healthy active headset");

            await connection.SelectAsync(DiscoveredDevice.KeyFor(a));
            check(manager.Ready && manager.Device?.Id == a.Id && connection.PreferredKey == DiscoveredDevice.KeyFor(a),
                "sidebar selection switches and remembers the physical headset");
            await connection.DisconnectAsync();
            int beforePausedScan = attempts.Count;
            await connection.RefreshAsync();
            check(connection.Paused && !manager.Ready && attempts.Count == beforePausedScan,
                "intentional disconnect survives background device updates");
            await connection.RetryAsync();
            check(manager.Ready && manager.Device?.Id == a.Id && !connection.Paused, "explicit resume reconnects the remembered online headset");

            discovery.Error = new IOException("simulated discovery failure");
            await connection.RefreshAsync();
            check(manager.Ready && manager.Device?.Id == a.Id && connection.LastError != null,
                "discovery failure preserves the active session and reports an error");
            discovery.Error = null; discovery.Devices = [a, b];
            await connection.RefreshAsync();
            check(manager.Ready && manager.Device?.Id == b.Id, "Windows disconnection moves management to the remaining online headset");

            connection.Automatic = false;
            await manager.DisconnectAsync();
            int beforeDisabledScan = attempts.Count;
            await connection.RefreshAsync();
            check(!manager.Ready && attempts.Count == beforeDisabledScan, "disabled automatic connection does not reconnect");
            await connection.RetryAsync();
            check(manager.Ready && !connection.Automatic, "manual retry works without changing the automatic connection preference");

            discovery.Devices = [a, b with { IsConnected = false }];
            await connection.RefreshAsync();
            int beforeOfflineScan = attempts.Count;
            connection.Automatic = true;
            await connection.RefreshAsync();
            check(!manager.Ready && attempts.Count == beforeOfflineScan && manager.State.Left.Stale,
                "offline paired records are not dialed and old readings become stale");
            await connection.DemoAsync();
            await connection.RetryAsync();
            check(!manager.Ready && manager.Device == null && manager.State.Left.Level == null,
                "leaving demo without a real headset clears simulated readings and device identity");
            discovery.Devices = [b];
            await connection.RefreshAsync();
            check(manager.Ready && manager.Device?.Id == b.Id, "a headset arriving after demo exit connects automatically");
            await connection.DisconnectAsync();
            connection.Resume();
            await connection.RefreshAsync();
            check(manager.Ready && !connection.Paused, "reenabling automatic connection resumes a paused session");
        }

        discovery = new FakeDiscovery { Devices = [b, bleB] };
        attempts.Clear();
        DemoTransport? activeBle = null;
        await using (var manager = new PodManager(device =>
        {
            attempts.Add(device.Id);
            return device.Transport == TransportKind.Rfcomm ? new RejectingTransport() : activeBle = new DemoTransport();
        }))
        await using (var connection = new DeviceConnectionCoordinator(discovery, manager))
        {
            await connection.RefreshAsync();
            check(manager.Ready && manager.Device?.Id == bleB.Id && attempts.SequenceEqual(new[] { b.Id, bleB.Id }),
                "failed RFCOMM handshake falls back to an online BLE endpoint");
            activeBle!.Drop();
            await connection.RefreshAsync();
            check(manager.Ready && attempts.Count(id => id == b.Id) == 1, "failed endpoint cooldown prevents rapid repeated handshakes");
            await connection.DisconnectAsync();
            await connection.RetryAsync();
            check(manager.Ready && attempts.Count(id => id == b.Id) == 2, "explicit retry bypasses the endpoint cooldown");
        }

        discovery = new FakeDiscovery { Devices = [b] };
        await using (var manager = new PodManager(_ => new RejectingTransport(silent: true)))
        await using (var connection = new DeviceConnectionCoordinator(discovery, manager))
        {
            Task refreshing = connection.RefreshAsync();
            await Task.Delay(100);
            await connection.DisconnectAsync().WaitAsync(TimeSpan.FromSeconds(2));
            await refreshing.WaitAsync(TimeSpan.FromSeconds(2));
            check(!manager.Ready && connection.Paused && !connection.IsConnecting, "pause cancels an in-flight automatic handshake");
        }
    }

    private sealed class FakeDiscovery : IDeviceDiscovery
    {
        public IReadOnlyList<PodDevice> Devices { get; set; } = [];
        public Exception? Error { get; set; }
        public Task<IReadOnlyList<PodDevice>> FindAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Error == null ? Task.FromResult(Devices) : Task.FromException<IReadOnlyList<PodDevice>>(Error);
        }
    }

    private sealed class RejectingTransport(bool silent = false) : IPodTransport
    {
        public event Action<GaiaFrame>? FrameReceived;
        public event Action<Exception?>? Disconnected { add { } remove { } }
        public string Label => "test";
        public Task ConnectAsync(PodDevice device, CancellationToken ct) => Task.CompletedTask;
        public Task SendAsync(GaiaFrame frame, CancellationToken ct)
        {
            if (!silent) FrameReceived?.Invoke(new(4, 10, 0x8300, [1]));
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
