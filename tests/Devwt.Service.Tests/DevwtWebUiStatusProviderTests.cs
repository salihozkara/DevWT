using Devwt.Core;

namespace Devwt.Service.Tests;

public sealed class DevwtWebUiStatusProviderTests
{
    [Fact]
    public void Status_routes_include_the_listener_process_name()
    {
        var stateRoot = Path.Combine(Path.GetTempPath(), $"devwt-status-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stateRoot);
        try
        {
            var route = new GatewayRoute(
                "context-a",
                "repository-a",
                stateRoot,
                5173,
                "127.0.0.1",
                55173,
                4242);
            var provider = new DevwtWebUiStatusProvider(
                new DevwtStateStore(stateRoot),
                new FixedRouteTableSource(route),
                processNameResolver: processId => processId == 4242 ? "node" : null);

            var statusRoute = Assert.Single(provider.Build().Routes);

            Assert.Equal("node", statusRoute.ProcessName);
            Assert.Equal(route.ContextId, statusRoute.ContextId);
            Assert.Equal(route.Port, statusRoute.Port);
            Assert.Equal(route.TargetPort, statusRoute.TargetPort);
            Assert.Equal(route.ListenerProcessId, statusRoute.ListenerProcessId);
        }
        finally
        {
            Directory.Delete(stateRoot, recursive: true);
        }
    }

    private sealed class FixedRouteTableSource(GatewayRoute route) : IDevwtGatewayRouteTableSource
    {
        public GatewayRouteTable BuildRouteTable() =>
            GatewayRouteTable.FromRoutes(
                [route],
                DevwtRepositoryState.Empty,
                DevwtContextState.Empty,
                DevwtRoutingState.Empty);
    }
}
