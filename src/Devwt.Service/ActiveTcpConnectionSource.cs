using System.ComponentModel;
using System.Net;
using System.Runtime.InteropServices;
using Devwt.Core;

namespace Devwt.Service;

public interface IActiveTcpConnectionSource
{
    int? TryFindOwningProcess(IPEndPoint clientEndPoint, IPEndPoint gatewayEndPoint);
}

public interface IActiveUdpEndpointSource
{
    int? TryFindOwningProcess(IPEndPoint localEndPoint);
}

public static class DevwtBrowserKeyResolver
{
    public static string? ResolveBrowserKey(
        IActiveTcpConnectionSource connectionSource,
        IProcessObservationSource processSource,
        IPEndPoint clientEndPoint,
        IPEndPoint serverEndPoint)
    {
        var processId = connectionSource.TryFindOwningProcess(clientEndPoint, serverEndPoint);
        if (processId is null)
        {
            return null;
        }

        var process = processSource.Read().FirstOrDefault(item => item.ProcessId == processId.Value);
        return string.IsNullOrWhiteSpace(process?.ImagePath)
            ? null
            : DevwtBrowserKey.Normalize(process.ImagePath);
    }
}

public sealed class WindowsActiveUdpEndpointSource(ICommandRunner commandRunner) : IActiveUdpEndpointSource
{
    public int? TryFindOwningProcess(IPEndPoint localEndPoint)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            && localEndPoint.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            try
            {
                return TryFindOwningProcessNative(localEndPoint);
            }
            catch (Exception ex) when (ex is Win32Exception or OutOfMemoryException or InvalidOperationException)
            {
            }
        }

        return TryFindOwningProcessPowerShell(localEndPoint);
    }

    private static int? TryFindOwningProcessNative(IPEndPoint localEndPoint)
    {
        var bufferLength = 0;
        var result = GetExtendedUdpTable(
            IntPtr.Zero,
            ref bufferLength,
            sort: true,
            ipVersion: AfInet,
            tableClass: UdpTableClassOwnerPid,
            reserved: 0);
        if (result != ErrorInsufficientBuffer && result != 0)
        {
            throw new Win32Exception((int)result);
        }

        var buffer = Marshal.AllocHGlobal(bufferLength);
        try
        {
            result = GetExtendedUdpTable(
                buffer,
                ref bufferLength,
                sort: true,
                ipVersion: AfInet,
                tableClass: UdpTableClassOwnerPid,
                reserved: 0);
            if (result != 0)
            {
                throw new Win32Exception((int)result);
            }

            var rowCount = Marshal.ReadInt32(buffer);
            var rowPointer = IntPtr.Add(buffer, sizeof(int));
            var rowSize = Marshal.SizeOf<MibUdpRowOwnerPid>();
            for (var index = 0; index < rowCount; index++)
            {
                var row = Marshal.PtrToStructure<MibUdpRowOwnerPid>(rowPointer);
                var address = new IPAddress(row.LocalAddr);
                if ((address.Equals(localEndPoint.Address) || address.Equals(IPAddress.Any))
                    && NetworkPortToHost(row.LocalPort) == localEndPoint.Port)
                {
                    return unchecked((int)row.OwningPid);
                }

                rowPointer = IntPtr.Add(rowPointer, rowSize);
            }

            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private int? TryFindOwningProcessPowerShell(IPEndPoint localEndPoint)
    {
        var command = $$"""
            $endpoint = Get-NetUDPEndpoint -ErrorAction SilentlyContinue |
              Where-Object {
                ($_.LocalAddress -eq '{{localEndPoint.Address}}' -or $_.LocalAddress -eq '0.0.0.0') -and
                $_.LocalPort -eq {{localEndPoint.Port}}
              } |
              Select-Object -First 1 -ExpandProperty OwningProcess
            if ($null -ne $endpoint) { $endpoint }
            """;
        var result = commandRunner.Run(
            [
                "powershell.exe",
                "-NoProfile",
                "-ExecutionPolicy",
                "Bypass",
                "-Command",
                command
            ]);
        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.Output))
        {
            return null;
        }

        return int.TryParse(result.Output.Trim(), out var processId) ? processId : null;
    }

    private static int NetworkPortToHost(uint port) =>
        (ushort)IPAddress.NetworkToHostOrder(unchecked((short)port));

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedUdpTable(
        IntPtr pUdpTable,
        ref int dwOutBufLen,
        bool sort,
        int ipVersion,
        int tableClass,
        uint reserved);

    [StructLayout(LayoutKind.Sequential)]
    private struct MibUdpRowOwnerPid
    {
        public uint LocalAddr;
        public uint LocalPort;
        public uint OwningPid;
    }

    private const int AfInet = 2;
    private const int UdpTableClassOwnerPid = 1;
    private const uint ErrorInsufficientBuffer = 122;
}

public sealed class WindowsActiveTcpConnectionSource(ICommandRunner commandRunner) : IActiveTcpConnectionSource
{
    public int? TryFindOwningProcess(IPEndPoint clientEndPoint, IPEndPoint gatewayEndPoint)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            && clientEndPoint.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
            && gatewayEndPoint.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            try
            {
                return TryFindOwningProcessNative(clientEndPoint, gatewayEndPoint);
            }
            catch (Exception ex) when (ex is Win32Exception or OutOfMemoryException or InvalidOperationException)
            {
            }
        }

        return TryFindOwningProcessPowerShell(clientEndPoint, gatewayEndPoint);
    }

    private static int? TryFindOwningProcessNative(IPEndPoint clientEndPoint, IPEndPoint gatewayEndPoint)
    {
        var bufferLength = 0;
        var result = GetExtendedTcpTable(
            IntPtr.Zero,
            ref bufferLength,
            sort: true,
            ipVersion: AfInet,
            tableClass: TcpTableClassOwnerPidAll,
            reserved: 0);
        if (result != ErrorInsufficientBuffer && result != 0)
        {
            throw new Win32Exception((int)result);
        }

        var buffer = Marshal.AllocHGlobal(bufferLength);
        try
        {
            result = GetExtendedTcpTable(
                buffer,
                ref bufferLength,
                sort: true,
                ipVersion: AfInet,
                tableClass: TcpTableClassOwnerPidAll,
                reserved: 0);
            if (result != 0)
            {
                throw new Win32Exception((int)result);
            }

            var rowCount = Marshal.ReadInt32(buffer);
            var rowPointer = IntPtr.Add(buffer, sizeof(int));
            var rowSize = Marshal.SizeOf<MibTcpRowOwnerPid>();
            for (var index = 0; index < rowCount; index++)
            {
                var row = Marshal.PtrToStructure<MibTcpRowOwnerPid>(rowPointer);
                if (row.State == MibTcpStateEstablished
                    && new IPAddress(row.LocalAddr).Equals(clientEndPoint.Address)
                    && NetworkPortToHost(row.LocalPort) == clientEndPoint.Port
                    && new IPAddress(row.RemoteAddr).Equals(gatewayEndPoint.Address)
                    && NetworkPortToHost(row.RemotePort) == gatewayEndPoint.Port)
                {
                    return unchecked((int)row.OwningPid);
                }

                rowPointer = IntPtr.Add(rowPointer, rowSize);
            }

            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private int? TryFindOwningProcessPowerShell(IPEndPoint clientEndPoint, IPEndPoint gatewayEndPoint)
    {
        var command = $$"""
            $connection = Get-NetTCPConnection -State Established -ErrorAction SilentlyContinue |
              Where-Object {
                $_.LocalAddress -eq '{{clientEndPoint.Address}}' -and
                $_.LocalPort -eq {{clientEndPoint.Port}} -and
                $_.RemoteAddress -eq '{{gatewayEndPoint.Address}}' -and
                $_.RemotePort -eq {{gatewayEndPoint.Port}}
              } |
              Select-Object -First 1 -ExpandProperty OwningProcess
            if ($null -ne $connection) { $connection }
            """;
        var result = commandRunner.Run(
            [
                "powershell.exe",
                "-NoProfile",
                "-ExecutionPolicy",
                "Bypass",
                "-Command",
                command
            ]);
        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.Output))
        {
            return null;
        }

        return int.TryParse(result.Output.Trim(), out var processId) ? processId : null;
    }

    private static int NetworkPortToHost(uint port) =>
        (ushort)IPAddress.NetworkToHostOrder(unchecked((short)port));

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(
        IntPtr pTcpTable,
        ref int dwOutBufLen,
        bool sort,
        int ipVersion,
        int tableClass,
        uint reserved);

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcpRowOwnerPid
    {
        public uint State;
        public uint LocalAddr;
        public uint LocalPort;
        public uint RemoteAddr;
        public uint RemotePort;
        public uint OwningPid;
    }

    private const int AfInet = 2;
    private const int TcpTableClassOwnerPidAll = 5;
    private const uint ErrorInsufficientBuffer = 122;
    private const uint MibTcpStateEstablished = 5;
}
