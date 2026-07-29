using System.ComponentModel;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Devwt.Core;

namespace Devwt.Service;

public interface IProcessObservationSource
{
    IReadOnlyList<ProcessObservation> Read();
}

public interface IListenerObservationSource
{
    IReadOnlyList<ListenerObservation> Read();
}

public sealed record ListenerObservation(
    int ProcessId,
    string LocalAddress,
    int Port,
    GatewayRouteProtocol Protocol = GatewayRouteProtocol.Tcp);

public sealed class WindowsProcessObservationSource(
    ICommandRunner commandRunner,
    IProcessCurrentDirectoryReader? currentDirectoryReader = null,
    Func<DevwtRuntimeSettings>? runtimeSettingsProvider = null) : IProcessObservationSource
{
    public IReadOnlyList<ProcessObservation> Read()
    {
        var result = commandRunner.Run(
            [
                "powershell.exe",
                "-NoProfile",
                "-ExecutionPolicy",
                "Bypass",
                "-Command",
                """
                Get-CimInstance Win32_Process | Select-Object ProcessId,ParentProcessId,ExecutablePath,CommandLine,CreationDate | ConvertTo-Json -Compress
                """
            ]);
        if (result.ExitCode != 0)
        {
            throw new IOException(result.Error);
        }

        var reader = currentDirectoryReader ?? new WindowsProcessCurrentDirectoryReader();
        var environmentNames = ResolveSessionEnvironmentNames(runtimeSettingsProvider?.Invoke());
        return ProcessObservationJson.Parse(result.Output)
            .Select(observation => observation with
            {
                WorkingDirectory = observation.WorkingDirectory ?? reader.TryRead(observation.ProcessId),
                EnvironmentVariables = environmentNames.Count == 0
                    ? observation.EnvironmentVariables
                    : WindowsProcessEnvironmentReader.TryRead(observation.ProcessId, environmentNames)
            })
            .ToArray();
    }

    private static IReadOnlyList<string> ResolveSessionEnvironmentNames(DevwtRuntimeSettings? settings)
    {
        if (settings is null || settings.SessionRules.Count == 0)
        {
            return [];
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in settings.SessionRules)
        {
            if (!string.IsNullOrWhiteSpace(rule.Match.EnvironmentVariable))
            {
                names.Add(rule.Match.EnvironmentVariable);
            }

            if (rule.Identity.Kind == DevwtSessionIdentityKind.EnvironmentVariable
                && !string.IsNullOrWhiteSpace(rule.Identity.Value))
            {
                names.Add(rule.Identity.Value);
            }
        }

        if (names.Count > 0)
        {
            names.Add("DEVWT_SESSION_ID");
        }

        return names.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }
}

internal static class WindowsProcessEnvironmentReader
{
    private const int ProcessBasicInformation = 0;
    private const int ProcessQueryLimitedInformation = 0x1000;
    private const int ProcessVmRead = 0x0010;
    private const int MaxEnvironmentBytes = 256 * 1024;
    private const int ChunkBytes = 4096;
    private const int PebProcessParametersOffset64 = 0x20;
    private const int RtlUserProcessParametersEnvironmentOffset64 = 0x80;

    public static IReadOnlyDictionary<string, string>? TryRead(int processId, IReadOnlyList<string> names)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            || !Environment.Is64BitProcess
            || names.Count == 0)
        {
            return null;
        }

        var process = OpenProcess(ProcessQueryLimitedInformation | ProcessVmRead, false, processId);
        if (process == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            if (IsWow64(process))
            {
                return null;
            }

            var pbi = new ProcessBasicInformationData();
            var status = NtQueryInformationProcess(
                process,
                ProcessBasicInformation,
                ref pbi,
                Marshal.SizeOf<ProcessBasicInformationData>(),
                out _);
            if (status != 0 || pbi.PebBaseAddress == IntPtr.Zero)
            {
                return null;
            }

            var processParameters = ReadPointer(process, IntPtr.Add(pbi.PebBaseAddress, PebProcessParametersOffset64));
            if (processParameters == IntPtr.Zero)
            {
                return null;
            }

            var environment = ReadPointer(process, IntPtr.Add(processParameters, RtlUserProcessParametersEnvironmentOffset64));
            if (environment == IntPtr.Zero)
            {
                return null;
            }

            var block = ReadUnicodeEnvironmentBlock(process, environment);
            if (string.IsNullOrEmpty(block))
            {
                return null;
            }

            return PickEnvironmentValues(block, names);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or AccessViolationException or ArgumentException)
        {
            return null;
        }
        finally
        {
            CloseHandle(process);
        }
    }

    private static bool IsWow64(IntPtr process)
    {
        if (!Environment.Is64BitOperatingSystem)
        {
            return false;
        }

        return IsWow64Process(process, out var wow64) && wow64;
    }

    private static IntPtr ReadPointer(IntPtr process, IntPtr address)
    {
        var buffer = new byte[IntPtr.Size];
        if (!ReadProcessMemory(process, address, buffer, buffer.Length, out var bytesRead) || bytesRead.ToInt32() != buffer.Length)
        {
            return IntPtr.Zero;
        }

        return new IntPtr(BitConverter.ToInt64(buffer, 0));
    }

    private static string? ReadUnicodeEnvironmentBlock(IntPtr process, IntPtr address)
    {
        using var memory = new MemoryStream();
        var buffer = new byte[ChunkBytes];
        for (var offset = 0; offset < MaxEnvironmentBytes; offset += ChunkBytes)
        {
            if (!ReadProcessMemory(process, IntPtr.Add(address, offset), buffer, buffer.Length, out var bytesRead)
                || bytesRead == IntPtr.Zero)
            {
                break;
            }

            var count = bytesRead.ToInt32();
            memory.Write(buffer, 0, count);
            if (EndsEnvironmentBlock(memory.GetBuffer().AsSpan(0, (int)memory.Length)))
            {
                break;
            }
        }

        var bytes = memory.ToArray();
        var length = FindEnvironmentBlockLength(bytes);
        return length <= 0 ? null : Encoding.Unicode.GetString(bytes, 0, length);
    }

    private static bool EndsEnvironmentBlock(ReadOnlySpan<byte> bytes) =>
        FindEnvironmentBlockLength(bytes) >= 0;

    private static int FindEnvironmentBlockLength(ReadOnlySpan<byte> bytes)
    {
        for (var index = 0; index + 3 < bytes.Length; index += 2)
        {
            if (bytes[index] == 0
                && bytes[index + 1] == 0
                && bytes[index + 2] == 0
                && bytes[index + 3] == 0)
            {
                return index;
            }
        }

        return -1;
    }

    private static IReadOnlyDictionary<string, string> PickEnvironmentValues(string block, IReadOnlyList<string> names)
    {
        var wanted = names.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in block.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = entry.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0)
            {
                continue;
            }

            var name = entry[..separator];
            if (wanted.Contains(name))
            {
                result[name] = entry[(separator + 1)..];
            }
        }

        return result;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(int desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(
        IntPtr process,
        IntPtr baseAddress,
        [Out] byte[] buffer,
        int size,
        out IntPtr bytesRead);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool IsWow64Process(IntPtr process, out bool wow64Process);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        IntPtr processHandle,
        int processInformationClass,
        ref ProcessBasicInformationData processInformation,
        int processInformationLength,
        out int returnLength);

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformationData
    {
        public IntPtr Reserved1;
        public IntPtr PebBaseAddress;
        public IntPtr Reserved2A;
        public IntPtr Reserved2B;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }
}

public sealed class WindowsTcpListenerObservationSource(ICommandRunner commandRunner) : IListenerObservationSource
{
    public IReadOnlyList<ListenerObservation> Read()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try
            {
                return ReadWindowsListeners();
            }
            catch (Exception ex) when (ex is Win32Exception or OutOfMemoryException or InvalidOperationException)
            {
            }
        }

        return ReadPowerShellListeners();
    }

    private IReadOnlyList<ListenerObservation> ReadPowerShellListeners()
    {
        var result = commandRunner.Run(
            [
                "powershell.exe",
                "-NoProfile",
                "-ExecutionPolicy",
                "Bypass",
                "-Command",
                """
                $tcp = Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue |
                  Select-Object LocalAddress,LocalPort,OwningProcess,@{Name='Protocol';Expression={'Tcp'}}
                $udp = Get-NetUDPEndpoint -ErrorAction SilentlyContinue |
                  Select-Object LocalAddress,LocalPort,OwningProcess,@{Name='Protocol';Expression={'Udp'}}
                @($tcp) + @($udp) | ConvertTo-Json -Compress
                """
            ]);
        if (result.ExitCode != 0)
        {
            throw new IOException(result.Error);
        }

        return ListenerObservationJson.Parse(result.Output);
    }

    private static IReadOnlyList<ListenerObservation> ReadWindowsListeners()
    {
        var observations = new List<ListenerObservation>();
        observations.AddRange(ReadWindowsTcp4Listeners());
        observations.AddRange(ReadWindowsTcp6Listeners());
        observations.AddRange(ReadWindowsUdp4Listeners());
        observations.AddRange(ReadWindowsUdp6Listeners());
        return observations;
    }

    private static IReadOnlyList<ListenerObservation> ReadWindowsTcp4Listeners()
    {
        var bufferLength = 0;
        var result = GetExtendedTcpTable(
            IntPtr.Zero,
            ref bufferLength,
            sort: true,
            ipVersion: AfInet,
            tableClass: TcpTableClassOwnerPidListener,
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
                tableClass: TcpTableClassOwnerPidListener,
                reserved: 0);
            if (result != 0)
            {
                throw new Win32Exception((int)result);
            }

            var rowCount = Marshal.ReadInt32(buffer);
            var rowPointer = IntPtr.Add(buffer, sizeof(int));
            var rowSize = Marshal.SizeOf<MibTcpRowOwnerPid>();
            var observations = new List<ListenerObservation>(rowCount);
            for (var index = 0; index < rowCount; index++)
            {
                var row = Marshal.PtrToStructure<MibTcpRowOwnerPid>(rowPointer);
                if (row.State == MibTcpStateListen)
                {
                    observations.Add(new ListenerObservation(
                        ProcessId: unchecked((int)row.OwningPid),
                        LocalAddress: new IPAddress(row.LocalAddr).ToString(),
                        Port: NetworkPortToHost(row.LocalPort),
                        Protocol: GatewayRouteProtocol.Tcp));
                }

                rowPointer = IntPtr.Add(rowPointer, rowSize);
            }

            return observations;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static IReadOnlyList<ListenerObservation> ReadWindowsTcp6Listeners()
    {
        var bufferLength = 0;
        var result = GetExtendedTcpTable(
            IntPtr.Zero,
            ref bufferLength,
            sort: true,
            ipVersion: AfInet6,
            tableClass: TcpTableClassOwnerPidListener,
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
                ipVersion: AfInet6,
                tableClass: TcpTableClassOwnerPidListener,
                reserved: 0);
            if (result != 0)
            {
                throw new Win32Exception((int)result);
            }

            var rowCount = Marshal.ReadInt32(buffer);
            var rowPointer = IntPtr.Add(buffer, sizeof(int));
            var rowSize = Marshal.SizeOf<MibTcp6RowOwnerPid>();
            var observations = new List<ListenerObservation>(rowCount);
            for (var index = 0; index < rowCount; index++)
            {
                var row = Marshal.PtrToStructure<MibTcp6RowOwnerPid>(rowPointer);
                if (row.State == MibTcpStateListen)
                {
                    observations.Add(new ListenerObservation(
                        ProcessId: unchecked((int)row.OwningPid),
                        LocalAddress: Ipv6AddressToString(row.LocalAddr, row.LocalScopeId),
                        Port: NetworkPortToHost(row.LocalPort),
                        Protocol: GatewayRouteProtocol.Tcp));
                }

                rowPointer = IntPtr.Add(rowPointer, rowSize);
            }

            return observations;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static IReadOnlyList<ListenerObservation> ReadWindowsUdp4Listeners()
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
            var observations = new List<ListenerObservation>(rowCount);
            for (var index = 0; index < rowCount; index++)
            {
                var row = Marshal.PtrToStructure<MibUdpRowOwnerPid>(rowPointer);
                observations.Add(new ListenerObservation(
                    ProcessId: unchecked((int)row.OwningPid),
                    LocalAddress: new IPAddress(row.LocalAddr).ToString(),
                    Port: NetworkPortToHost(row.LocalPort),
                    Protocol: GatewayRouteProtocol.Udp));
                rowPointer = IntPtr.Add(rowPointer, rowSize);
            }

            return observations;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static IReadOnlyList<ListenerObservation> ReadWindowsUdp6Listeners()
    {
        var bufferLength = 0;
        var result = GetExtendedUdpTable(
            IntPtr.Zero,
            ref bufferLength,
            sort: true,
            ipVersion: AfInet6,
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
                ipVersion: AfInet6,
                tableClass: UdpTableClassOwnerPid,
                reserved: 0);
            if (result != 0)
            {
                throw new Win32Exception((int)result);
            }

            var rowCount = Marshal.ReadInt32(buffer);
            var rowPointer = IntPtr.Add(buffer, sizeof(int));
            var rowSize = Marshal.SizeOf<MibUdp6RowOwnerPid>();
            var observations = new List<ListenerObservation>(rowCount);
            for (var index = 0; index < rowCount; index++)
            {
                var row = Marshal.PtrToStructure<MibUdp6RowOwnerPid>(rowPointer);
                observations.Add(new ListenerObservation(
                    ProcessId: unchecked((int)row.OwningPid),
                    LocalAddress: Ipv6AddressToString(row.LocalAddr, row.LocalScopeId),
                    Port: NetworkPortToHost(row.LocalPort),
                    Protocol: GatewayRouteProtocol.Udp));
                rowPointer = IntPtr.Add(rowPointer, rowSize);
            }

            return observations;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static int NetworkPortToHost(uint port) =>
        (ushort)IPAddress.NetworkToHostOrder(unchecked((short)port));

    private static string Ipv6AddressToString(byte[] address, uint scopeId)
    {
        var hostScopeId = unchecked((uint)IPAddress.NetworkToHostOrder(unchecked((int)scopeId)));
        return new IPAddress(address, hostScopeId).ToString();
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(
        IntPtr pTcpTable,
        ref int dwOutBufLen,
        bool sort,
        int ipVersion,
        int tableClass,
        uint reserved);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedUdpTable(
        IntPtr pUdpTable,
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

    [StructLayout(LayoutKind.Sequential)]
    private struct MibUdpRowOwnerPid
    {
        public uint LocalAddr;
        public uint LocalPort;
        public uint OwningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcp6RowOwnerPid
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] LocalAddr;

        public uint LocalScopeId;
        public uint LocalPort;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] RemoteAddr;

        public uint RemoteScopeId;
        public uint RemotePort;
        public uint State;
        public uint OwningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MibUdp6RowOwnerPid
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] LocalAddr;

        public uint LocalScopeId;
        public uint LocalPort;
        public uint OwningPid;
    }

    private const int AfInet = 2;
    private const int AfInet6 = 23;
    private const int TcpTableClassOwnerPidListener = 3;
    private const int UdpTableClassOwnerPid = 1;
    private const uint ErrorInsufficientBuffer = 122;
    private const uint MibTcpStateListen = 2;
}

public static class ProcessObservationJson
{
    public static IReadOnlyList<ProcessObservation> Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        using var document = JsonDocument.Parse(json);
        var observations = new List<ProcessObservation>();
        foreach (var element in EnumerateElements(document.RootElement))
        {
            observations.Add(new ProcessObservation(
                ProcessId: RequiredInt(element, "ProcessId"),
                ParentProcessId: OptionalInt(element, "ParentProcessId"),
                ImagePath: OptionalString(element, "ExecutablePath"),
                CommandLine: OptionalString(element, "CommandLine"),
                WorkingDirectory: null,
                StartTime: OptionalString(element, "CreationDate")));
        }

        return observations;
    }

    private static IEnumerable<JsonElement> EnumerateElements(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in root.EnumerateArray())
            {
                yield return element;
            }
        }
        else
        {
            yield return root;
        }
    }

    private static int RequiredInt(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.Number
            ? property.GetInt32()
            : throw new InvalidDataException($"Missing process property: {propertyName}");

    private static int? OptionalInt(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.Number
            ? property.GetInt32()
            : null;

    private static string? OptionalString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
}

public static class ListenerObservationJson
{
    public static IReadOnlyList<ListenerObservation> Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        using var document = JsonDocument.Parse(json);
        var observations = new List<ListenerObservation>();
        foreach (var element in EnumerateElements(document.RootElement))
        {
            observations.Add(new ListenerObservation(
                ProcessId: RequiredInt(element, "OwningProcess"),
                LocalAddress: OptionalString(element, "LocalAddress") ?? "",
                Port: RequiredInt(element, "LocalPort"),
                Protocol: ParseProtocol(OptionalString(element, "Protocol"))));
        }

        return observations;
    }

    private static IEnumerable<JsonElement> EnumerateElements(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in root.EnumerateArray())
            {
                yield return element;
            }
        }
        else
        {
            yield return root;
        }
    }

    private static int RequiredInt(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.Number
            ? property.GetInt32()
            : throw new InvalidDataException($"Missing listener property: {propertyName}");

    private static string? OptionalString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static GatewayRouteProtocol ParseProtocol(string? value) =>
        value?.Equals("Udp", StringComparison.OrdinalIgnoreCase) == true
            ? GatewayRouteProtocol.Udp
            : GatewayRouteProtocol.Tcp;
}
