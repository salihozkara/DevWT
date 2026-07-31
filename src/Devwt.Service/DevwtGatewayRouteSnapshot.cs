using System.Text.Json;
using Devwt.Core;

namespace Devwt.Service;

public interface IDevwtGatewayRouteTableSource
{
    GatewayRouteTable BuildRouteTable();
}

public sealed record DevwtGatewayRouteSnapshot(
    IReadOnlyList<GatewayRoute> Routes,
    DevwtRepositoryState Repositories,
    DevwtContextState Contexts,
    DevwtRoutingState Routing);

public sealed class DevwtGatewayRouteSnapshotStore(string stateRoot) : IDevwtGatewayRouteTableSource
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _path = Path.Combine(stateRoot, "gateway-routes.json");
    private string? _lastJson;
    private DateTime _lastWriteTimeUtc;
    private long _lastLength = -1;
    private GatewayRouteTable _cached = GatewayRouteTable.FromRoutes(
        [],
        DevwtRepositoryState.Empty,
        DevwtContextState.Empty,
        DevwtRoutingState.Empty);

    public void Save(GatewayRouteTable routeTable)
    {
        var json = JsonSerializer.Serialize(routeTable.ToSnapshot(), JsonOptions);
        if (string.Equals(json, _lastJson, StringComparison.Ordinal) && File.Exists(_path))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tempPath = _path + ".tmp";
        File.WriteAllText(tempPath, json);
        if (File.Exists(_path))
        {
            File.Replace(tempPath, _path, destinationBackupFileName: null);
        }
        else
        {
            File.Move(tempPath, _path);
        }

        _lastJson = json;
        var info = new FileInfo(_path);
        _lastWriteTimeUtc = info.LastWriteTimeUtc;
        _lastLength = info.Length;
        _cached = routeTable;
    }

    public GatewayRouteTable BuildRouteTable()
    {
        if (!File.Exists(_path))
        {
            return _cached;
        }

        var info = new FileInfo(_path);
        if (info.LastWriteTimeUtc == _lastWriteTimeUtc && info.Length == _lastLength)
        {
            return _cached;
        }

        var snapshot = JsonSerializer.Deserialize<DevwtGatewayRouteSnapshot>(File.ReadAllText(_path), JsonOptions)
            ?? throw new InvalidDataException("DevWT gateway route snapshot is empty.");
        _lastWriteTimeUtc = info.LastWriteTimeUtc;
        _lastLength = info.Length;
        _cached = GatewayRouteTable.FromRoutes(
            snapshot.Routes,
            snapshot.Repositories,
            snapshot.Contexts,
            snapshot.Routing);
        return _cached;
    }
}
