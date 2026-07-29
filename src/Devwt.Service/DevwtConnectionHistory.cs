using Devwt.Core;
using System.Threading.Channels;

namespace Devwt.Service;

public sealed record DevwtConnectionHistoryEntry(
    DateTimeOffset Timestamp,
    GatewayRouteProtocol Protocol,
    string ListenIp,
    int Port,
    string TargetIp,
    int TargetPort,
    string ContextId,
    string? ContextName,
    string RouteReason,
    int? ProcessId,
    string? ProcessImagePath,
    string? ApplicationKey,
    string? ClientEndPoint,
    string? SessionId = null);

public interface IDevwtConnectionHistorySink
{
    void Add(DevwtConnectionHistoryEntry entry);
}

public sealed class DevwtConnectionHistory : IDevwtConnectionHistorySink
{
    private readonly object _gate = new();
    private readonly Queue<DevwtConnectionHistoryEntry> _entries = [];
    private readonly int _capacity;

    public DevwtConnectionHistory(int capacity = 200)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), "History capacity must be positive.");
        }

        _capacity = capacity;
    }

    public void Add(DevwtConnectionHistoryEntry entry)
    {
        lock (_gate)
        {
            _entries.Enqueue(entry);
            while (_entries.Count > _capacity)
            {
                _entries.Dequeue();
            }
        }
    }

    public IReadOnlyList<DevwtConnectionHistoryEntry> Snapshot()
    {
        lock (_gate)
        {
            return _entries.Reverse().ToArray();
        }
    }
}

public sealed class DevwtControlConnectionHistorySink : IDevwtConnectionHistorySink, IAsyncDisposable
{
    private readonly IDevwtControlClient _client;
    private readonly Channel<DevwtConnectionHistoryEntry> _entries;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _pump;

    public DevwtControlConnectionHistorySink(IDevwtControlClient client, int capacity = 200)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _client = client;
        _entries = Channel.CreateBounded<DevwtConnectionHistoryEntry>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });
        _pump = Task.Run(PumpAsync);
    }

    public void Add(DevwtConnectionHistoryEntry entry) => _entries.Writer.TryWrite(entry);

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();
        _entries.Writer.TryComplete();
        await _pump;
        _stopping.Dispose();
    }

    private async Task PumpAsync()
    {
        try
        {
            await foreach (var entry in _entries.Reader.ReadAllAsync(_stopping.Token))
            {
                try
                {
                    _client.Send(new DevwtControlRequest(
                        DevwtControlOperation.RecordGatewayConnection,
                        ConnectionHistoryEntry: entry));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    // History is diagnostic and must never interrupt gateway traffic.
                }
            }
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
        }
    }
}
