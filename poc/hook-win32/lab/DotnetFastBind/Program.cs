using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

var port = args.Length > 0 ? int.Parse(args[0]) : 55273;
var label = args.Length > 1 ? args[1] : "dotnet-fast";
var holdMs = args.Length > 2 ? int.Parse(args[2]) : 10000;

var listener = new TcpListener(IPAddress.Parse("127.0.0.1"), port);
listener.Start();

var endpoint = (IPEndPoint)listener.LocalEndpoint;
Console.WriteLine($"DOTNET_BOUND {label} {endpoint.Address}:{endpoint.Port} pid={Environment.ProcessId}");

await Task.Delay(holdMs);
listener.Stop();
GC.KeepAlive(Process.GetCurrentProcess());
