#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <winsock2.h>
#include <ws2tcpip.h>
#include <tlhelp32.h>

#include <stdio.h>
#include <stdint.h>
#include <string.h>

#include <algorithm>
#include <fstream>
#include <string>
#include <vector>

using BindFn = int (WSAAPI *)(SOCKET, const sockaddr *, int);
using ConnectFn = int (WSAAPI *)(SOCKET, const sockaddr *, int);
using WsaConnectFn = int (WSAAPI *)(SOCKET, const sockaddr *, int, LPWSABUF, LPWSABUF, LPQOS, LPQOS);
using GetSockNameFn = int (WSAAPI *)(SOCKET, sockaddr *, int *);
using GetAddrInfoAFn = int (WSAAPI *)(PCSTR, PCSTR, const ADDRINFOA *, PADDRINFOA *);
using GetAddrInfoWFn = int (WSAAPI *)(PCWSTR, PCWSTR, const ADDRINFOW *, PADDRINFOW *);
using GetProcAddressFn = FARPROC(WINAPI *)(HMODULE, LPCSTR);
using CreateProcessWFn = BOOL(WINAPI *)(
    LPCWSTR,
    LPWSTR,
    LPSECURITY_ATTRIBUTES,
    LPSECURITY_ATTRIBUTES,
    BOOL,
    DWORD,
    LPVOID,
    LPCWSTR,
    LPSTARTUPINFOW,
    LPPROCESS_INFORMATION);
using CreateProcessAFn = BOOL(WINAPI *)(
    LPCSTR,
    LPSTR,
    LPSECURITY_ATTRIBUTES,
    LPSECURITY_ATTRIBUTES,
    BOOL,
    DWORD,
    LPVOID,
    LPCSTR,
    LPSTARTUPINFOA,
    LPPROCESS_INFORMATION);
struct DevwtAnsiString
{
    USHORT Length;
    USHORT MaximumLength;
    PCHAR Buffer;
};

using LdrGetProcedureAddressFn = LONG(NTAPI *)(HMODULE, DevwtAnsiString *, WORD, PVOID *);

static BindFn g_realBind = nullptr;
static ConnectFn g_realConnect = nullptr;
static WsaConnectFn g_realWsaConnect = nullptr;
static GetSockNameFn g_realGetSockName = nullptr;
static GetAddrInfoAFn g_realGetAddrInfoA = nullptr;
static GetAddrInfoWFn g_realGetAddrInfoW = nullptr;
static GetProcAddressFn g_realGetProcAddress = nullptr;
static CreateProcessWFn g_realCreateProcessW = nullptr;
static CreateProcessAFn g_realCreateProcessA = nullptr;
static LdrGetProcedureAddressFn g_realLdrGetProcedureAddress = nullptr;
static IN_ADDR g_bindAddress{};
static IN_ADDR g_connectAddress{};
static bool g_hasBindAddress = false;
static bool g_hasConnectAddress = false;
static std::string g_contextId;
static int g_portOffset = 0;
static bool g_hasPortOffset = false;
static char g_bindingsFilePath[MAX_PATH]{};
static bool g_childrenOnlyMode = false;
static volatile LONG g_patchRunning = 0;
static SRWLOCK g_inlineLock = SRWLOCK_INIT;
static wchar_t g_hookDllPath[MAX_PATH]{};
static constexpr int DefaultMaxPortShiftBindAttempts = 512;
static constexpr int MaxConfiguredPortShiftBindAttempts = 1024;
static int g_maxPortShiftBindAttempts = DefaultMaxPortShiftBindAttempts;
static bool g_hasExplicitMaxPortShiftBindAttempts = false;

struct InlinePatch
{
    void *Target = nullptr;
    void *Replacement = nullptr;
    BYTE Original[16]{};
    bool Installed = false;
};

static InlinePatch g_bindPatch;
static InlinePatch g_connectPatch;
static InlinePatch g_wsaConnectPatch;
static InlinePatch g_getSockNamePatch;
static InlinePatch g_getAddrInfoAPatch;
static InlinePatch g_getAddrInfoWPatch;
static InlinePatch g_createProcessWPatch;
static InlinePatch g_createProcessAPatch;
static InlinePatch g_createProcessWKernelBasePatch;
static InlinePatch g_createProcessAKernelBasePatch;

struct ContextMapEntry
{
    std::wstring Folder;
    std::string ContextId;
    IN_ADDR BindAddress{};
    IN_ADDR ConnectAddress{};
    int PortOffset = 0;
    bool HasPortOffset = false;
};

struct ChildHookConfig
{
    std::string ContextId;
    IN_ADDR BindAddress{};
    IN_ADDR ConnectAddress{};
    int PortOffset = 0;
    bool HasBindAddress = false;
    bool HasConnectAddress = false;
    bool HasPortOffset = false;
};

struct SocketPortMap
{
    SOCKET Socket{};
    sockaddr_storage OriginalEndpoint{};
    int OriginalEndpointLength = 0;
    u_short TargetPort = 0;
};

static SRWLOCK g_socketPortMapLock = SRWLOCK_INIT;
static std::vector<SocketPortMap> g_socketPortMaps;

static void EnsureRealWinsockFunctions();
static void PatchAllModules();
static void ReloadHookConfiguration();
static bool LoadValueFromPidFile(const char *name, char *valueBuffer, size_t valueBufferLength);
static bool TryResolveHookConfigFromContextMap(LPCWSTR currentDirectory, ChildHookConfig *config, bool *hasMapFile);

extern "C" __declspec(dllexport) void DevwtHookPresent()
{
    ReloadHookConfiguration();
    PatchAllModules();
}

static bool IsLoopbackV4(const sockaddr *name, int length)
{
    if (!name || length < static_cast<int>(sizeof(sockaddr_in)) || name->sa_family != AF_INET)
    {
        return false;
    }

    const auto *v4 = reinterpret_cast<const sockaddr_in *>(name);
    const auto host = ntohl(v4->sin_addr.s_addr);
    return (host & 0xff000000UL) == 0x7f000000UL;
}

static bool IsAssignedBindV4(const sockaddr *name, int length)
{
    if (!g_hasBindAddress || !name || length < static_cast<int>(sizeof(sockaddr_in)) || name->sa_family != AF_INET)
    {
        return false;
    }

    const auto *v4 = reinterpret_cast<const sockaddr_in *>(name);
    return v4->sin_addr.s_addr == g_bindAddress.s_addr;
}

static bool IsSupportedInternetEndpoint(const sockaddr *name, int length)
{
    if (!name || length > static_cast<int>(sizeof(sockaddr_storage)))
    {
        return false;
    }

    if (name->sa_family == AF_INET)
    {
        return length >= static_cast<int>(sizeof(sockaddr_in));
    }

    return name->sa_family == AF_INET6
        && length >= static_cast<int>(sizeof(sockaddr_in6));
}

static bool TryGetEndpointPort(const sockaddr *name, int length, u_short *port)
{
    if (!port || !IsSupportedInternetEndpoint(name, length))
    {
        return false;
    }

    if (name->sa_family == AF_INET)
    {
        *port = ntohs(reinterpret_cast<const sockaddr_in *>(name)->sin_port);
    }
    else
    {
        *port = ntohs(reinterpret_cast<const sockaddr_in6 *>(name)->sin6_port);
    }

    return true;
}

static bool SetEndpointPort(sockaddr *name, int length, u_short port)
{
    if (!IsSupportedInternetEndpoint(name, length))
    {
        return false;
    }

    if (name->sa_family == AF_INET)
    {
        reinterpret_cast<sockaddr_in *>(name)->sin_port = htons(port);
    }
    else
    {
        reinterpret_cast<sockaddr_in6 *>(name)->sin6_port = htons(port);
    }

    return true;
}

static bool IsDevwtManagementPort(const sockaddr *name, int length)
{
    u_short port = 0;
    return TryGetEndpointPort(name, length, &port) && port == 17776;
}

static bool LoadAddressFromEnv(const char *name, IN_ADDR *address)
{
    char buffer[64]{};
    const auto length = GetEnvironmentVariableA(name, buffer, static_cast<DWORD>(sizeof(buffer)));
    if (length == 0 || length >= sizeof(buffer))
    {
        return false;
    }

    return InetPtonA(AF_INET, buffer, address) == 1;
}

static bool LoadStringFromEnv(const char *name, char *buffer, size_t bufferLength)
{
    const auto length = GetEnvironmentVariableA(name, buffer, static_cast<DWORD>(bufferLength));
    return length > 0 && length < bufferLength;
}

static bool LoadStringFromEnvOrPidFile(const char *name, char *buffer, size_t bufferLength)
{
    if (LoadStringFromEnv(name, buffer, bufferLength))
    {
        return true;
    }

    return LoadValueFromPidFile(name, buffer, bufferLength);
}

static bool ParseInt(const char *value, int *result)
{
    if (!value || !*value || !result)
    {
        return false;
    }

    char *end = nullptr;
    const long parsed = strtol(value, &end, 10);
    if (!end || *end != '\0' || parsed <= 0 || parsed > 60000)
    {
        return false;
    }

    *result = static_cast<int>(parsed);
    return true;
}

static bool IsEnvironmentFlagEnabled(const char *name)
{
    char buffer[16]{};
    const auto length = GetEnvironmentVariableA(name, buffer, static_cast<DWORD>(sizeof(buffer)));
    if (length == 0 || length >= sizeof(buffer))
    {
        return false;
    }

    return _stricmp(buffer, "1") == 0 ||
        _stricmp(buffer, "true") == 0 ||
        _stricmp(buffer, "yes") == 0 ||
        _stricmp(buffer, "on") == 0;
}

static bool IsHookDisabled()
{
    return IsEnvironmentFlagEnabled("DEVWT_HOOK_DISABLE");
}

static void DebugLog(const char *message)
{
    char path[MAX_PATH]{};
    const auto length = GetEnvironmentVariableA("DEVWT_HOOK_LOG", path, static_cast<DWORD>(sizeof(path)));
    if (length == 0 || length >= sizeof(path))
    {
        return;
    }

    HANDLE file = CreateFileA(
        path,
        FILE_APPEND_DATA,
        FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
        nullptr,
        OPEN_ALWAYS,
        FILE_ATTRIBUTE_NORMAL,
        nullptr);
    if (file == INVALID_HANDLE_VALUE)
    {
        return;
    }

    char line[1024]{};
    const int written = _snprintf_s(line, sizeof(line), _TRUNCATE, "[%lu] %s\r\n", GetCurrentProcessId(), message);
    if (written > 0)
    {
        DWORD bytesWritten = 0;
        WriteFile(file, line, static_cast<DWORD>(written), &bytesWritten, nullptr);
    }

    CloseHandle(file);
}

static bool BuildSharedPidConfigPath(char *filePath, size_t filePathLength)
{
    char programData[MAX_PATH]{};
    const auto length = GetEnvironmentVariableA("ProgramData", programData, static_cast<DWORD>(sizeof(programData)));
    const char *root = (length > 0 && length < sizeof(programData))
        ? programData
        : "C:\\ProgramData";

    return _snprintf_s(
        filePath,
        filePathLength,
        _TRUNCATE,
        "%s\\DevWT\\hook-pids\\devwt-hook-poc-%lu.env",
        root,
        GetCurrentProcessId()) > 0;
}

static bool BuildTempPidConfigPath(char *filePath, size_t filePathLength)
{
    char tempPath[MAX_PATH]{};
    if (!GetTempPathA(static_cast<DWORD>(sizeof(tempPath)), tempPath))
    {
        return false;
    }

    return _snprintf_s(filePath, filePathLength, _TRUNCATE, "%sdevwt-hook-poc-%lu.env", tempPath, GetCurrentProcessId()) > 0;
}

static bool LoadAddressFromPidFilePath(const char *filePath, const char *name, IN_ADDR *address)
{
    HANDLE file = CreateFileA(filePath, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_DELETE, nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE)
    {
        return false;
    }

    char buffer[512]{};
    DWORD read = 0;
    const BOOL ok = ReadFile(file, buffer, sizeof(buffer) - 1, &read, nullptr);
    CloseHandle(file);
    if (!ok || read == 0)
    {
        return false;
    }

    buffer[read] = '\0';
    const size_t nameLength = strlen(name);
    char *cursor = buffer;
    while (*cursor)
    {
        while (*cursor == '\r' || *cursor == '\n')
        {
            cursor++;
        }

        if (_strnicmp(cursor, name, nameLength) == 0 && cursor[nameLength] == '=')
        {
            char *value = cursor + nameLength + 1;
            char *end = value;
            while (*end && *end != '\r' && *end != '\n')
            {
                end++;
            }

            *end = '\0';
            return InetPtonA(AF_INET, value, address) == 1;
        }

        while (*cursor && *cursor != '\n')
        {
            cursor++;
        }
    }

    return false;
}

static bool LoadAddressFromPidFile(const char *name, IN_ADDR *address)
{
    char filePath[MAX_PATH]{};
    if (BuildSharedPidConfigPath(filePath, sizeof(filePath)) &&
        LoadAddressFromPidFilePath(filePath, name, address))
    {
        return true;
    }

    if (BuildTempPidConfigPath(filePath, sizeof(filePath)) &&
        LoadAddressFromPidFilePath(filePath, name, address))
    {
        return true;
    }

    return false;
}

static bool LoadValueFromPidFilePath(const char *filePath, const char *name, char *valueBuffer, size_t valueBufferLength)
{
    if (!valueBuffer || valueBufferLength == 0)
    {
        return false;
    }

    valueBuffer[0] = '\0';
    HANDLE file = CreateFileA(filePath, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_DELETE, nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE)
    {
        return false;
    }

    char buffer[512]{};
    DWORD read = 0;
    const BOOL ok = ReadFile(file, buffer, sizeof(buffer) - 1, &read, nullptr);
    CloseHandle(file);
    if (!ok || read == 0)
    {
        return false;
    }

    buffer[read] = '\0';
    const size_t nameLength = strlen(name);
    char *cursor = buffer;
    while (*cursor)
    {
        while (*cursor == '\r' || *cursor == '\n')
        {
            cursor++;
        }

        if (_strnicmp(cursor, name, nameLength) == 0 && cursor[nameLength] == '=')
        {
            char *value = cursor + nameLength + 1;
            char *end = value;
            while (*end && *end != '\r' && *end != '\n')
            {
                end++;
            }

            *end = '\0';
            strncpy_s(valueBuffer, valueBufferLength, value, _TRUNCATE);
            return valueBuffer[0] != '\0';
        }

        while (*cursor && *cursor != '\n')
        {
            cursor++;
        }
    }

    return false;
}

static bool LoadValueFromPidFile(const char *name, char *valueBuffer, size_t valueBufferLength)
{
    char filePath[MAX_PATH]{};
    if (BuildSharedPidConfigPath(filePath, sizeof(filePath)) &&
        LoadValueFromPidFilePath(filePath, name, valueBuffer, valueBufferLength))
    {
        return true;
    }

    if (BuildTempPidConfigPath(filePath, sizeof(filePath)) &&
        LoadValueFromPidFilePath(filePath, name, valueBuffer, valueBufferLength))
    {
        return true;
    }

    return false;
}

static void ReloadHookConfiguration()
{
    g_hasBindAddress = false;
    g_hasConnectAddress = false;
    g_contextId.clear();
    g_portOffset = 0;
    g_hasPortOffset = false;
    g_bindingsFilePath[0] = '\0';
    g_childrenOnlyMode = IsEnvironmentFlagEnabled("DEVWT_HOOK_CHILDREN_ONLY");
    g_maxPortShiftBindAttempts = DefaultMaxPortShiftBindAttempts;
    g_hasExplicitMaxPortShiftBindAttempts = false;

    char maxBindAttempts[32]{};
    bool hasExplicitMaxBindAttempts = LoadStringFromEnv(
        "DEVWT_HOOK_BIND_MAX_ATTEMPTS",
        maxBindAttempts,
        sizeof(maxBindAttempts));
    if (!hasExplicitMaxBindAttempts)
    {
        char explicitMarker[16]{};
        const bool hasExplicitMarker = LoadValueFromPidFile(
            "DEVWT_HOOK_BIND_MAX_ATTEMPTS_EXPLICIT",
            explicitMarker,
            sizeof(explicitMarker))
            && (_stricmp(explicitMarker, "1") == 0
                || _stricmp(explicitMarker, "true") == 0
                || _stricmp(explicitMarker, "yes") == 0
                || _stricmp(explicitMarker, "on") == 0);
        hasExplicitMaxBindAttempts = hasExplicitMarker
            && LoadValueFromPidFile(
                "DEVWT_HOOK_BIND_MAX_ATTEMPTS",
                maxBindAttempts,
                sizeof(maxBindAttempts));
    }

    int configuredMaxBindAttempts = 0;
    if (hasExplicitMaxBindAttempts
        && ParseInt(maxBindAttempts, &configuredMaxBindAttempts)
        && configuredMaxBindAttempts >= 1
        && configuredMaxBindAttempts <= MaxConfiguredPortShiftBindAttempts)
    {
        g_maxPortShiftBindAttempts = configuredMaxBindAttempts;
        g_hasExplicitMaxPortShiftBindAttempts = true;
    }

    char maxBindAttemptsMessage[96]{};
    _snprintf_s(
        maxBindAttemptsMessage,
        sizeof(maxBindAttemptsMessage),
        _TRUNCATE,
        "ReloadHookConfiguration maxBindAttempts=%d explicit=%d",
        g_maxPortShiftBindAttempts,
        g_hasExplicitMaxPortShiftBindAttempts ? 1 : 0);
    DebugLog(maxBindAttemptsMessage);

    char hookMode[32]{};
    if (LoadValueFromPidFile("DEVWT_HOOK_MODE", hookMode, sizeof(hookMode)))
    {
        if (_stricmp(hookMode, "full") == 0)
        {
            g_childrenOnlyMode = false;
        }
        else if (_stricmp(hookMode, "children-only") == 0)
        {
            g_childrenOnlyMode = true;
        }
    }

    ChildHookConfig mapConfig{};
    bool hasMapFile = false;
    if (TryResolveHookConfigFromContextMap(nullptr, &mapConfig, &hasMapFile))
    {
        g_bindAddress = mapConfig.BindAddress;
        g_connectAddress = mapConfig.ConnectAddress;
        g_hasBindAddress = mapConfig.HasBindAddress;
        g_hasConnectAddress = mapConfig.HasConnectAddress;
        g_contextId = mapConfig.ContextId;
        g_portOffset = mapConfig.PortOffset;
        g_hasPortOffset = mapConfig.HasPortOffset;
        LoadStringFromEnvOrPidFile("DEVWT_HOOK_BINDINGS_FILE", g_bindingsFilePath, sizeof(g_bindingsFilePath));
        return;
    }

    IN_ADDR bindAddress{};
    IN_ADDR connectAddress{};
    bool hasBindAddress = LoadAddressFromEnv("DEVWT_HOOK_BIND_IP", &bindAddress);
    bool hasConnectAddress = LoadAddressFromEnv("DEVWT_HOOK_CONNECT_IP", &connectAddress);
    char contextId[128]{};
    char portOffset[32]{};
    if (!hasBindAddress)
    {
        hasBindAddress = LoadAddressFromPidFile("DEVWT_HOOK_BIND_IP", &bindAddress);
    }

    if (!hasConnectAddress)
    {
        hasConnectAddress = LoadAddressFromPidFile("DEVWT_HOOK_CONNECT_IP", &connectAddress);
    }

    if (hasBindAddress)
    {
        g_bindAddress = bindAddress;
        g_hasBindAddress = true;
    }

    if (hasConnectAddress)
    {
        g_connectAddress = connectAddress;
        g_hasConnectAddress = true;
    }

    if (LoadStringFromEnvOrPidFile("DEVWT_HOOK_CONTEXT_ID", contextId, sizeof(contextId)))
    {
        g_contextId = contextId;
    }

    if (LoadStringFromEnvOrPidFile("DEVWT_HOOK_PORT_OFFSET", portOffset, sizeof(portOffset))
        && ParseInt(portOffset, &g_portOffset))
    {
        g_hasPortOffset = true;
    }

    LoadStringFromEnvOrPidFile("DEVWT_HOOK_BINDINGS_FILE", g_bindingsFilePath, sizeof(g_bindingsFilePath));
}

static bool AddressToString(const IN_ADDR &address, char *buffer, DWORD bufferLength)
{
    return InetNtopA(AF_INET, const_cast<IN_ADDR *>(&address), buffer, bufferLength) != nullptr;
}

static bool AddressToStringW(const IN_ADDR &address, wchar_t *buffer, DWORD bufferLength)
{
    return InetNtopW(AF_INET, const_cast<IN_ADDR *>(&address), buffer, bufferLength) != nullptr;
}

static bool EndpointAddressToString(const sockaddr *endpoint, int length, char *buffer, DWORD bufferLength)
{
    if (!buffer || !IsSupportedInternetEndpoint(endpoint, length))
    {
        return false;
    }

    if (endpoint->sa_family == AF_INET)
    {
        const auto *v4 = reinterpret_cast<const sockaddr_in *>(endpoint);
        return InetNtopA(
            AF_INET,
            const_cast<IN_ADDR *>(&v4->sin_addr),
            buffer,
            bufferLength) != nullptr;
    }

    const auto *v6 = reinterpret_cast<const sockaddr_in6 *>(endpoint);
    return InetNtopA(
        AF_INET6,
        const_cast<IN6_ADDR *>(&v6->sin6_addr),
        buffer,
        bufferLength) != nullptr;
}

static bool IsLocalhostNameA(PCSTR nodeName)
{
    return nodeName
        && (_stricmp(nodeName, "localhost") == 0
            || strcmp(nodeName, "127.0.0.1") == 0
            || strcmp(nodeName, "::1") == 0);
}

static bool IsLocalhostNameW(PCWSTR nodeName)
{
    return nodeName
        && (_wcsicmp(nodeName, L"localhost") == 0
            || wcscmp(nodeName, L"127.0.0.1") == 0
            || wcscmp(nodeName, L"::1") == 0);
}

static bool IsDevwtManagementServiceA(PCSTR serviceName)
{
    return serviceName && strcmp(serviceName, "17776") == 0;
}

static bool IsDevwtManagementServiceW(PCWSTR serviceName)
{
    return serviceName && wcscmp(serviceName, L"17776") == 0;
}

static bool TryGetResolveAddress(bool passive, IN_ADDR *address)
{
    if (passive || !g_hasConnectAddress)
    {
        if (!g_hasBindAddress)
        {
            return false;
        }

        *address = g_bindAddress;
        return true;
    }

    *address = g_connectAddress;
    return true;
}

static u_short ShiftPort(u_short originalPort)
{
    if (!g_hasPortOffset || originalPort == 0)
    {
        return originalPort;
    }

    auto shifted = 10000 + ((static_cast<int>(originalPort) + g_portOffset) % 50000);
    if (shifted == 17776)
    {
        shifted++;
    }

    return static_cast<u_short>(shifted);
}

static u_short NextShiftedPort(u_short currentPort)
{
    auto next = static_cast<int>(currentPort) + 1;
    if (next > 59999)
    {
        next = 10000;
    }
    if (next == 17776)
    {
        next++;
    }

    return static_cast<u_short>(next);
}

static const char *SocketProtocolName(SOCKET socket)
{
    int type = 0;
    int length = sizeof(type);
    if (getsockopt(socket, SOL_SOCKET, SO_TYPE, reinterpret_cast<char *>(&type), &length) == 0
        && type == SOCK_DGRAM)
    {
        return "udp";
    }

    return "tcp";
}

static void RememberSocketPort(
    SOCKET socket,
    const sockaddr *originalEndpoint,
    int originalEndpointLength,
    u_short targetPort)
{
    u_short originalPort = 0;
    if (!TryGetEndpointPort(originalEndpoint, originalEndpointLength, &originalPort)
        || originalPort == 0
        || originalPort == targetPort
        || originalEndpointLength > static_cast<int>(sizeof(sockaddr_storage)))
    {
        return;
    }

    sockaddr_storage endpointCopy{};
    memcpy(&endpointCopy, originalEndpoint, static_cast<size_t>(originalEndpointLength));

    AcquireSRWLockExclusive(&g_socketPortMapLock);
    for (auto &entry : g_socketPortMaps)
    {
        if (entry.Socket == socket)
        {
            entry.OriginalEndpoint = endpointCopy;
            entry.OriginalEndpointLength = originalEndpointLength;
            entry.TargetPort = targetPort;
            ReleaseSRWLockExclusive(&g_socketPortMapLock);
            return;
        }
    }

    SocketPortMap entry{};
    entry.Socket = socket;
    entry.OriginalEndpoint = endpointCopy;
    entry.OriginalEndpointLength = originalEndpointLength;
    entry.TargetPort = targetPort;
    g_socketPortMaps.push_back(entry);
    ReleaseSRWLockExclusive(&g_socketPortMapLock);
}

static bool TryGetRememberedOriginalEndpoint(
    SOCKET socket,
    u_short targetPort,
    sockaddr_storage *originalEndpoint,
    int *originalEndpointLength)
{
    if (!originalEndpoint || !originalEndpointLength)
    {
        return false;
    }

    AcquireSRWLockShared(&g_socketPortMapLock);
    for (const auto &entry : g_socketPortMaps)
    {
        if (entry.Socket == socket && entry.TargetPort == targetPort)
        {
            *originalEndpoint = entry.OriginalEndpoint;
            *originalEndpointLength = entry.OriginalEndpointLength;
            ReleaseSRWLockShared(&g_socketPortMapLock);
            return true;
        }
    }

    ReleaseSRWLockShared(&g_socketPortMapLock);
    return false;
}

static bool IsProcessStillRunning(DWORD processId)
{
    if (processId == 0)
    {
        return false;
    }

    HANDLE process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, processId);
    if (!process)
    {
        return false;
    }

    DWORD exitCode = 0;
    const bool running = GetExitCodeProcess(process, &exitCode)
        && exitCode == STILL_ACTIVE;
    CloseHandle(process);
    return running;
}

static bool TryParseUnsignedLong(const std::string &value, unsigned long *parsed)
{
    if (!parsed || value.empty())
    {
        return false;
    }

    char *end = nullptr;
    const auto result = strtoul(value.c_str(), &end, 10);
    if (!end || end == value.c_str() || *end != '\0')
    {
        return false;
    }

    *parsed = result;
    return true;
}

static bool BindingAddressesConflict(const std::string &recorded, const char *requested)
{
    return requested
        && (_stricmp(recorded.c_str(), requested) == 0
            || recorded == "0.0.0.0"
            || recorded == "::"
            || strcmp(requested, "0.0.0.0") == 0
            || strcmp(requested, "::") == 0);
}

static bool IsTargetOwnedByDifferentNaturalPort(
    const sockaddr *original,
    int originalLength,
    const sockaddr *target,
    int targetLength,
    const char *protocol)
{
    u_short originalPort = 0;
    u_short targetPort = 0;
    char targetIp[64]{};
    if (g_contextId.empty()
        || !g_bindingsFilePath[0]
        || !TryGetEndpointPort(original, originalLength, &originalPort)
        || !TryGetEndpointPort(target, targetLength, &targetPort)
        || originalPort == 0
        || targetPort == 0
        || !EndpointAddressToString(target, targetLength, targetIp, static_cast<DWORD>(sizeof(targetIp))))
    {
        return false;
    }

    HANDLE file = CreateFileA(
        g_bindingsFilePath,
        GENERIC_READ,
        FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
        nullptr,
        OPEN_EXISTING,
        FILE_ATTRIBUTE_NORMAL,
        nullptr);
    if (file == INVALID_HANDLE_VALUE)
    {
        return false;
    }

    LARGE_INTEGER fileSize{};
    if (!GetFileSizeEx(file, &fileSize) || fileSize.QuadPart <= 0)
    {
        CloseHandle(file);
        return false;
    }

    static constexpr DWORD MaxBindingHistoryBytes = 4 * 1024 * 1024;
    const auto bytesToRead = static_cast<DWORD>(
        std::min<LONGLONG>(fileSize.QuadPart, MaxBindingHistoryBytes));
    const LONGLONG startOffset = fileSize.QuadPart - bytesToRead;
    if (startOffset > 0)
    {
        LARGE_INTEGER offset{};
        offset.QuadPart = startOffset;
        if (!SetFilePointerEx(file, offset, nullptr, FILE_BEGIN))
        {
            CloseHandle(file);
            return false;
        }
    }

    std::vector<char> buffer(static_cast<size_t>(bytesToRead) + 1);
    DWORD bytesRead = 0;
    const bool read = ReadFile(file, buffer.data(), bytesToRead, &bytesRead, nullptr) != FALSE;
    CloseHandle(file);
    if (!read || bytesRead == 0)
    {
        return false;
    }

    buffer[bytesRead] = '\0';
    std::string content(buffer.data(), bytesRead);
    size_t lineStart = 0;
    if (startOffset > 0)
    {
        const auto firstLineEnd = content.find('\n');
        if (firstLineEnd == std::string::npos)
        {
            return false;
        }
        lineStart = firstLineEnd + 1;
    }

    bool sameNaturalPortIsLive = false;
    bool differentNaturalPortIsLive = false;
    while (lineStart < content.size())
    {
        const auto lineEnd = content.find('\n', lineStart);
        auto line = content.substr(
            lineStart,
            lineEnd == std::string::npos ? std::string::npos : lineEnd - lineStart);
        if (!line.empty() && line.back() == '\r')
        {
            line.pop_back();
        }

        std::vector<std::string> fields;
        size_t fieldStart = 0;
        while (fieldStart <= line.size())
        {
            const auto fieldEnd = line.find('\t', fieldStart);
            fields.push_back(line.substr(
                fieldStart,
                fieldEnd == std::string::npos ? std::string::npos : fieldEnd - fieldStart));
            if (fieldEnd == std::string::npos)
            {
                break;
            }
            fieldStart = fieldEnd + 1;
        }

        unsigned long recordedOriginalPort = 0;
        unsigned long recordedTargetPort = 0;
        unsigned long recordedProcessId = 0;
        if (fields.size() == 7
            && _stricmp(fields[0].c_str(), g_contextId.c_str()) == 0
            && TryParseUnsignedLong(fields[2], &recordedOriginalPort)
            && TryParseUnsignedLong(fields[4], &recordedTargetPort)
            && TryParseUnsignedLong(fields[5], &recordedProcessId)
            && recordedTargetPort == targetPort
            && _stricmp(fields[6].c_str(), protocol ? protocol : "tcp") == 0
            && BindingAddressesConflict(fields[3], targetIp)
            && recordedProcessId <= MAXDWORD
            && IsProcessStillRunning(static_cast<DWORD>(recordedProcessId)))
        {
            if (recordedOriginalPort == originalPort)
            {
                sameNaturalPortIsLive = true;
            }
            else
            {
                differentNaturalPortIsLive = true;
            }
        }

        if (sameNaturalPortIsLive)
        {
            return false;
        }
        if (lineEnd == std::string::npos)
        {
            break;
        }
        lineStart = lineEnd + 1;
    }

    return differentNaturalPortIsLive;
}

static void AppendPortBinding(
    const sockaddr *original,
    int originalLength,
    const sockaddr *target,
    int targetLength,
    const char *protocol)
{
    u_short originalPort = 0;
    u_short targetPort = 0;
    if (g_contextId.empty()
        || !g_bindingsFilePath[0]
        || !TryGetEndpointPort(original, originalLength, &originalPort)
        || !TryGetEndpointPort(target, targetLength, &targetPort)
        || originalPort == 0)
    {
        return;
    }

    char originalIp[64]{};
    if (!EndpointAddressToString(original, originalLength, originalIp, static_cast<DWORD>(sizeof(originalIp))))
    {
        return;
    }

    char targetIp[64]{};
    if (!EndpointAddressToString(target, targetLength, targetIp, static_cast<DWORD>(sizeof(targetIp))))
    {
        return;
    }

    HANDLE file = CreateFileA(
        g_bindingsFilePath,
        FILE_APPEND_DATA,
        FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
        nullptr,
        OPEN_ALWAYS,
        FILE_ATTRIBUTE_NORMAL,
        nullptr);
    if (file == INVALID_HANDLE_VALUE)
    {
        return;
    }

    char line[512]{};
    const int length = _snprintf_s(
        line,
        sizeof(line),
        _TRUNCATE,
        "%s\t%s\t%hu\t%s\t%hu\t%lu\t%s\r\n",
        g_contextId.c_str(),
        originalIp,
        originalPort,
        targetIp,
        targetPort,
        GetCurrentProcessId(),
        protocol ? protocol : "tcp");
    if (length > 0)
    {
        DWORD written = 0;
        WriteFile(file, line, static_cast<DWORD>(length), &written, nullptr);
    }

    CloseHandle(file);
}

static std::wstring Utf8ToWide(const std::string &value)
{
    if (value.empty())
    {
        return L"";
    }

    const int length = MultiByteToWideChar(CP_UTF8, 0, value.c_str(), static_cast<int>(value.size()), nullptr, 0);
    if (length <= 0)
    {
        return L"";
    }

    std::wstring result(static_cast<size_t>(length), L'\0');
    MultiByteToWideChar(CP_UTF8, 0, value.c_str(), static_cast<int>(value.size()), result.data(), length);
    return result;
}

static std::wstring AnsiToWide(LPCSTR value)
{
    if (!value || !*value)
    {
        return L"";
    }

    const size_t sourceLength = strlen(value);
    if (sourceLength > static_cast<size_t>(INT_MAX))
    {
        return L"";
    }

    const int sourceLengthInt = static_cast<int>(sourceLength);
    const int length = MultiByteToWideChar(CP_ACP, 0, value, sourceLengthInt, nullptr, 0);
    if (length <= 0)
    {
        return L"";
    }

    std::wstring result(static_cast<size_t>(length), L'\0');
    MultiByteToWideChar(CP_ACP, 0, value, sourceLengthInt, result.data(), length);
    return result;
}

static std::wstring FullPathW(std::wstring value)
{
    if (value.empty())
    {
        return value;
    }

    std::replace(value.begin(), value.end(), L'/', L'\\');
    const DWORD length = GetFullPathNameW(value.c_str(), 0, nullptr, nullptr);
    if (length == 0)
    {
        return value;
    }

    std::wstring result(length, L'\0');
    const DWORD written = GetFullPathNameW(value.c_str(), length, result.data(), nullptr);
    if (written == 0 || written >= length)
    {
        return value;
    }

    result.resize(written);
    std::replace(result.begin(), result.end(), L'/', L'\\');
    return result;
}

static std::wstring NormalizeFolderForMatch(std::wstring value)
{
    value = FullPathW(value);
    while (!value.empty() && (value.back() == L'\\' || value.back() == L'/'))
    {
        value.pop_back();
    }

    if (!value.empty())
    {
        value.push_back(L'\\');
    }

    return value;
}

static bool StartsWithFolderIgnoreCase(const std::wstring &candidate, const std::wstring &folder)
{
    return candidate.size() >= folder.size() &&
        _wcsnicmp(candidate.c_str(), folder.c_str(), folder.size()) == 0;
}

static bool BuildContextMapPath(char *filePath, size_t filePathLength)
{
    const auto explicitLength = GetEnvironmentVariableA("DEVWT_HOOK_MAP_FILE", filePath, static_cast<DWORD>(filePathLength));
    if (explicitLength > 0 && explicitLength < filePathLength)
    {
        return true;
    }

    if (LoadValueFromPidFile("DEVWT_HOOK_MAP_FILE", filePath, filePathLength))
    {
        return true;
    }

    char programData[MAX_PATH]{};
    const auto programDataLength = GetEnvironmentVariableA("ProgramData", programData, static_cast<DWORD>(sizeof(programData)));
    const char *root = (programDataLength > 0 && programDataLength < sizeof(programData))
        ? programData
        : "C:\\ProgramData";

    return _snprintf_s(filePath, filePathLength, _TRUNCATE, "%s\\DevWT\\hook-contexts.tsv", root) > 0;
}

static bool TrimLineEnd(std::string *value)
{
    if (!value)
    {
        return false;
    }

    while (!value->empty() && (value->back() == '\r' || value->back() == '\n'))
    {
        value->pop_back();
    }

    return true;
}

static bool ReadAllTextFile(const char *filePath, std::string *content)
{
    if (!filePath || !content)
    {
        return false;
    }

    HANDLE file = CreateFileA(
        filePath,
        GENERIC_READ,
        FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
        nullptr,
        OPEN_EXISTING,
        FILE_ATTRIBUTE_NORMAL,
        nullptr);
    if (file == INVALID_HANDLE_VALUE)
    {
        return false;
    }

    LARGE_INTEGER size{};
    if (!GetFileSizeEx(file, &size) || size.QuadPart < 0 || size.QuadPart > 1024 * 1024)
    {
        CloseHandle(file);
        return false;
    }

    content->assign(static_cast<size_t>(size.QuadPart), '\0');
    DWORD read = 0;
    const BOOL ok = size.QuadPart == 0 ||
        ReadFile(file, content->data(), static_cast<DWORD>(content->size()), &read, nullptr);
    CloseHandle(file);
    if (!ok)
    {
        content->clear();
        return false;
    }

    content->resize(read);
    return true;
}

static bool ContextMapFileExists(const char *filePath)
{
    if (!filePath || !*filePath)
    {
        return false;
    }

    const DWORD attributes = GetFileAttributesA(filePath);
    return attributes != INVALID_FILE_ATTRIBUTES && (attributes & FILE_ATTRIBUTE_DIRECTORY) == 0;
}

static bool LoadContextMaps(const char *filePath, std::vector<ContextMapEntry> *maps)
{
    if (!maps)
    {
        return false;
    }

    std::string content;
    if (!ReadAllTextFile(filePath, &content))
    {
        return false;
    }

    maps->clear();
    size_t start = 0;
    while (start <= content.size())
    {
        auto end = content.find('\n', start);
        if (end == std::string::npos)
        {
            end = content.size();
        }

        auto line = content.substr(start, end - start);
        start = end + 1;
        TrimLineEnd(&line);
        if (line.empty())
        {
            continue;
        }

        std::vector<std::string> parts;
        size_t fieldStart = 0;
        while (fieldStart <= line.size())
        {
            const auto tab = line.find('\t', fieldStart);
            parts.push_back(line.substr(fieldStart, tab == std::string::npos ? std::string::npos : tab - fieldStart));
            if (tab == std::string::npos)
            {
                break;
            }

            fieldStart = tab + 1;
        }

        if (parts.size() < 3)
        {
            continue;
        }

        const bool hasPortShiftFields = parts.size() >= 5;
        const auto folder = NormalizeFolderForMatch(Utf8ToWide(parts[0]));
        const auto contextId = hasPortShiftFields ? parts[1] : "";
        const auto bindIp = hasPortShiftFields ? parts[2] : parts[1];
        const auto connectIp = hasPortShiftFields ? parts[3] : parts[2];
        const auto portOffset = hasPortShiftFields ? parts[4] : "";
        if (folder.empty())
        {
            continue;
        }

        ContextMapEntry entry{};
        entry.Folder = folder;
        entry.ContextId = contextId;
        if (InetPtonA(AF_INET, bindIp.c_str(), &entry.BindAddress) != 1)
        {
            continue;
        }

        entry.ConnectAddress = entry.BindAddress;
        if (!connectIp.empty())
        {
            InetPtonA(AF_INET, connectIp.c_str(), &entry.ConnectAddress);
        }

        if (!portOffset.empty() && ParseInt(portOffset.c_str(), &entry.PortOffset))
        {
            entry.HasPortOffset = true;
        }

        maps->push_back(entry);
    }

    std::sort(maps->begin(), maps->end(), [](const ContextMapEntry &left, const ContextMapEntry &right)
    {
        if (left.Folder.size() != right.Folder.size())
        {
            return left.Folder.size() > right.Folder.size();
        }

        return _wcsicmp(left.Folder.c_str(), right.Folder.c_str()) < 0;
    });
    return true;
}

static std::wstring ResolveChildDirectory(LPCWSTR currentDirectory)
{
    if (currentDirectory && *currentDirectory)
    {
        return NormalizeFolderForMatch(currentDirectory);
    }

    DWORD length = GetCurrentDirectoryW(0, nullptr);
    if (length == 0)
    {
        return L"";
    }

    std::wstring directory(length, L'\0');
    const DWORD written = GetCurrentDirectoryW(length, directory.data());
    if (written == 0 || written >= length)
    {
        return L"";
    }

    directory.resize(written);
    return NormalizeFolderForMatch(directory);
}

static bool TryResolveHookConfigFromContextMap(LPCWSTR currentDirectory, ChildHookConfig *config, bool *hasMapFile)
{
    if (!config)
    {
        return false;
    }

    if (hasMapFile)
    {
        *hasMapFile = false;
    }

    std::vector<ContextMapEntry> maps;
    char mapPath[MAX_PATH]{};
    const bool hasMapPath = BuildContextMapPath(mapPath, sizeof(mapPath));
    const bool mapExists = hasMapPath && ContextMapFileExists(mapPath);
    if (hasMapFile)
    {
        *hasMapFile = mapExists;
    }

    if (mapExists)
    {
        if (!LoadContextMaps(mapPath, &maps))
        {
            return false;
        }

        const auto childDirectory = ResolveChildDirectory(currentDirectory);
        for (const auto &map : maps)
        {
            if (StartsWithFolderIgnoreCase(childDirectory, map.Folder))
            {
                config->ContextId = map.ContextId;
                config->BindAddress = map.BindAddress;
                config->ConnectAddress = map.ConnectAddress;
                config->PortOffset = map.PortOffset;
                config->HasBindAddress = true;
                config->HasConnectAddress = true;
                config->HasPortOffset = map.HasPortOffset;
                return true;
            }
        }

        return false;
    }

    return false;
}

static bool ResolveChildHookConfig(LPCWSTR currentDirectory, ChildHookConfig *config)
{
    bool hasMapFile = false;
    if (TryResolveHookConfigFromContextMap(currentDirectory, config, &hasMapFile))
    {
        return true;
    }

    if (hasMapFile)
    {
        return false;
    }

    if (!g_hasBindAddress)
    {
        return false;
    }

    config->BindAddress = g_bindAddress;
    config->ConnectAddress = g_hasConnectAddress ? g_connectAddress : g_bindAddress;
    config->ContextId = g_contextId;
    config->PortOffset = g_portOffset;
    config->HasBindAddress = true;
    config->HasConnectAddress = true;
    config->HasPortOffset = g_hasPortOffset;
    return true;
}

static bool WritePidConfigForChild(DWORD pid, const ChildHookConfig &config)
{
    wchar_t tempPath[MAX_PATH]{};
    if (!GetTempPathW(MAX_PATH, tempPath))
    {
        return false;
    }

    wchar_t filePath[MAX_PATH]{};
    _snwprintf_s(filePath, MAX_PATH, _TRUNCATE, L"%sdevwt-hook-poc-%lu.env", tempPath, pid);

    HANDLE file = CreateFileW(filePath, GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_DELETE, nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE)
    {
        return false;
    }

    char content[1024]{};
    char bindIp[64]{};
    char connectIp[64]{};
    int offset = 0;

    if (!config.ContextId.empty())
    {
        offset += _snprintf_s(content + offset, sizeof(content) - offset, _TRUNCATE, "DEVWT_HOOK_CONTEXT_ID=%s\n", config.ContextId.c_str());
    }

    if (config.HasBindAddress && AddressToString(config.BindAddress, bindIp, static_cast<DWORD>(sizeof(bindIp))))
    {
        offset += _snprintf_s(content + offset, sizeof(content) - offset, _TRUNCATE, "DEVWT_HOOK_BIND_IP=%s\n", bindIp);
    }

    if (config.HasConnectAddress && AddressToString(config.ConnectAddress, connectIp, static_cast<DWORD>(sizeof(connectIp))))
    {
        offset += _snprintf_s(content + offset, sizeof(content) - offset, _TRUNCATE, "DEVWT_HOOK_CONNECT_IP=%s\n", connectIp);
    }

    if (config.HasPortOffset)
    {
        offset += _snprintf_s(content + offset, sizeof(content) - offset, _TRUNCATE, "DEVWT_HOOK_PORT_OFFSET=%d\n", config.PortOffset);
    }
    if (g_hasExplicitMaxPortShiftBindAttempts)
    {
        offset += _snprintf_s(
            content + offset,
            sizeof(content) - offset,
            _TRUNCATE,
            "DEVWT_HOOK_BIND_MAX_ATTEMPTS=%d\n"
            "DEVWT_HOOK_BIND_MAX_ATTEMPTS_EXPLICIT=1\n",
            g_maxPortShiftBindAttempts);
    }

    char mapPath[MAX_PATH]{};
    if (BuildContextMapPath(mapPath, sizeof(mapPath)))
    {
        offset += _snprintf_s(content + offset, sizeof(content) - offset, _TRUNCATE, "DEVWT_HOOK_MAP_FILE=%s\n", mapPath);
    }

    if (g_bindingsFilePath[0])
    {
        offset += _snprintf_s(content + offset, sizeof(content) - offset, _TRUNCATE, "DEVWT_HOOK_BINDINGS_FILE=%s\n", g_bindingsFilePath);
    }

    offset += _snprintf_s(content + offset, sizeof(content) - offset, _TRUNCATE, "DEVWT_HOOK_MODE=full\n");

    DWORD written = 0;
    const BOOL ok = offset > 0 && WriteFile(file, content, static_cast<DWORD>(strlen(content)), &written, nullptr);
    CloseHandle(file);
    return ok && written == strlen(content);
}

static bool InjectDllIntoProcess(HANDLE process)
{
    if (!process || !g_hookDllPath[0])
    {
        return false;
    }

    const size_t byteCount = (wcslen(g_hookDllPath) + 1) * sizeof(wchar_t);
    void *remote = VirtualAllocEx(process, nullptr, byteCount, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    if (!remote)
    {
        return false;
    }

    SIZE_T written = 0;
    if (!WriteProcessMemory(process, remote, g_hookDllPath, byteCount, &written) || written != byteCount)
    {
        VirtualFreeEx(process, remote, 0, MEM_RELEASE);
        return false;
    }

    auto loadLibrary = reinterpret_cast<LPTHREAD_START_ROUTINE>(GetProcAddress(GetModuleHandleW(L"kernel32.dll"), "LoadLibraryW"));
    HANDLE thread = CreateRemoteThread(process, nullptr, 0, loadLibrary, remote, 0, nullptr);
    if (!thread)
    {
        VirtualFreeEx(process, remote, 0, MEM_RELEASE);
        return false;
    }

    WaitForSingleObject(thread, INFINITE);
    DWORD exitCode = 0;
    GetExitCodeThread(thread, &exitCode);
    CloseHandle(thread);
    VirtualFreeEx(process, remote, 0, MEM_RELEASE);
    return exitCode != 0;
}

static bool IsWs2Module(HMODULE module)
{
    char path[MAX_PATH]{};
    if (!GetModuleFileNameA(module, path, static_cast<DWORD>(sizeof(path))))
    {
        return false;
    }

    const char *name = strrchr(path, '\\');
    name = name ? name + 1 : path;
    return _stricmp(name, "ws2_32.dll") == 0 || _stricmp(name, "wsock32.dll") == 0;
}

static bool StartsWithIgnoreCase(const char *actual, const char *prefix);

static bool IsKernelLoaderModule(HMODULE module)
{
    char path[MAX_PATH]{};
    if (!GetModuleFileNameA(module, path, static_cast<DWORD>(sizeof(path))))
    {
        return false;
    }

    const char *name = strrchr(path, '\\');
    name = name ? name + 1 : path;
    return _stricmp(name, "kernel32.dll") == 0 || _stricmp(name, "kernelbase.dll") == 0;
}

static FARPROC ResolveProcedureAddress(HMODULE module, const char *name)
{
    const auto getProcAddress = g_realGetProcAddress ? g_realGetProcAddress : &GetProcAddress;
    return module ? getProcAddress(module, name) : nullptr;
}

static CreateProcessWFn ResolveRealCreateProcessW()
{
    if (const auto kernelBase = GetModuleHandleW(L"kernelbase.dll"))
    {
        if (const auto proc = ResolveProcedureAddress(kernelBase, "CreateProcessW"))
        {
            return reinterpret_cast<CreateProcessWFn>(proc);
        }
    }

    if (const auto kernel32 = GetModuleHandleW(L"kernel32.dll"))
    {
        if (const auto proc = ResolveProcedureAddress(kernel32, "CreateProcessW"))
        {
            return reinterpret_cast<CreateProcessWFn>(proc);
        }
    }

    return &CreateProcessW;
}

static CreateProcessAFn ResolveRealCreateProcessA()
{
    if (const auto kernelBase = GetModuleHandleW(L"kernelbase.dll"))
    {
        if (const auto proc = ResolveProcedureAddress(kernelBase, "CreateProcessA"))
        {
            return reinterpret_cast<CreateProcessAFn>(proc);
        }
    }

    if (const auto kernel32 = GetModuleHandleW(L"kernel32.dll"))
    {
        if (const auto proc = ResolveProcedureAddress(kernel32, "CreateProcessA"))
        {
            return reinterpret_cast<CreateProcessAFn>(proc);
        }
    }

    return &CreateProcessA;
}

static bool IsDllName(const char *actual, const char *expected)
{
    return actual && _stricmp(actual, expected) == 0;
}

static std::wstring FileNameOf(const std::wstring &path)
{
    const auto slash = path.find_last_of(L"\\/");
    return slash == std::wstring::npos ? path : path.substr(slash + 1);
}

static bool ShouldInjectImage(const std::wstring &imagePath)
{
    const auto fileName = FileNameOf(imagePath);
    if (fileName.empty())
    {
        return false;
    }

    return _wcsicmp(fileName.c_str(), L"Devwt.Cli.exe") != 0 &&
        _wcsicmp(fileName.c_str(), L"devwt-hook-launcher.exe") != 0 &&
        _wcsicmp(fileName.c_str(), L"devwt-folder-watcher.exe") != 0;
}

static std::wstring QueryProcessImagePath(HANDLE process)
{
    if (!process)
    {
        return L"";
    }

    std::wstring buffer(32768, L'\0');
    DWORD length = static_cast<DWORD>(buffer.size());
    if (!QueryFullProcessImageNameW(process, 0, buffer.data(), &length))
    {
        return L"";
    }

    buffer.resize(length);
    return buffer;
}

static bool StartsWithIgnoreCase(const char *actual, const char *prefix)
{
    if (!actual || !prefix)
    {
        return false;
    }

    return _strnicmp(actual, prefix, strlen(prefix)) == 0;
}

static void PatchSlot(void **slot, void *replacement)
{
    if (!slot || !replacement || *slot == replacement)
    {
        return;
    }

    DWORD oldProtect = 0;
    if (!VirtualProtect(slot, sizeof(void *), PAGE_READWRITE, &oldProtect))
    {
        return;
    }

    *slot = replacement;

    DWORD ignored = 0;
    VirtualProtect(slot, sizeof(void *), oldProtect, &ignored);
    FlushInstructionCache(GetCurrentProcess(), slot, sizeof(void *));
}

static bool WriteFunctionBytes(void *target, const BYTE *bytes, SIZE_T length)
{
    DWORD oldProtect = 0;
    if (!VirtualProtect(target, length, PAGE_EXECUTE_READWRITE, &oldProtect))
    {
        return false;
    }

    memcpy(target, bytes, length);

    DWORD ignored = 0;
    VirtualProtect(target, length, oldProtect, &ignored);
    FlushInstructionCache(GetCurrentProcess(), target, length);
    return true;
}

static bool InstallInlinePatch(InlinePatch *patch, void *target, void *replacement)
{
    if (!patch || !target || !replacement)
    {
        return false;
    }

    AcquireSRWLockExclusive(&g_inlineLock);
    if (patch->Installed && patch->Target == target && patch->Replacement == replacement)
    {
        ReleaseSRWLockExclusive(&g_inlineLock);
        return true;
    }

    patch->Target = target;
    patch->Replacement = replacement;
    memcpy(patch->Original, target, 12);

    BYTE jump[12] = {
        0x48, 0xB8,
        0, 0, 0, 0, 0, 0, 0, 0,
        0xFF, 0xE0
    };
    *reinterpret_cast<void **>(&jump[2]) = replacement;

    patch->Installed = WriteFunctionBytes(target, jump, sizeof(jump));
    ReleaseSRWLockExclusive(&g_inlineLock);
    return patch->Installed;
}

static void RemoveInlinePatch(InlinePatch *patch)
{
    if (!patch || !patch->Installed || !patch->Target)
    {
        return;
    }

    WriteFunctionBytes(patch->Target, patch->Original, 12);
    patch->Installed = false;
}

static void ReinstallInlinePatch(InlinePatch *patch)
{
    if (!patch || !patch->Target || !patch->Replacement)
    {
        return;
    }

    BYTE jump[12] = {
        0x48, 0xB8,
        0, 0, 0, 0, 0, 0, 0, 0,
        0xFF, 0xE0
    };
    *reinterpret_cast<void **>(&jump[2]) = patch->Replacement;
    patch->Installed = WriteFunctionBytes(patch->Target, jump, sizeof(jump));
}

static bool PauseInlinePatch(InlinePatch *patch)
{
    AcquireSRWLockExclusive(&g_inlineLock);
    const bool hadInlinePatch = patch && patch->Installed && patch->Target;
    if (hadInlinePatch)
    {
        RemoveInlinePatch(patch);
    }

    ReleaseSRWLockExclusive(&g_inlineLock);
    return hadInlinePatch;
}

static void ResumeInlinePatch(InlinePatch *patch, bool hadInlinePatch)
{
    if (!hadInlinePatch)
    {
        return;
    }

    AcquireSRWLockExclusive(&g_inlineLock);
    ReinstallInlinePatch(patch);
    ReleaseSRWLockExclusive(&g_inlineLock);
}

static int CallOriginalBind(SOCKET socket, const sockaddr *name, int length)
{
    AcquireSRWLockExclusive(&g_inlineLock);
    const bool hadInlinePatch = g_bindPatch.Installed && g_bindPatch.Target;
    if (hadInlinePatch)
    {
        RemoveInlinePatch(&g_bindPatch);
    }

    const int result = reinterpret_cast<BindFn>(g_realBind)(socket, name, length);

    if (hadInlinePatch)
    {
        ReinstallInlinePatch(&g_bindPatch);
    }

    ReleaseSRWLockExclusive(&g_inlineLock);
    return result;
}

static int CallOriginalConnect(SOCKET socket, const sockaddr *name, int length)
{
    AcquireSRWLockExclusive(&g_inlineLock);
    const bool hadInlinePatch = g_connectPatch.Installed && g_connectPatch.Target;
    if (hadInlinePatch)
    {
        RemoveInlinePatch(&g_connectPatch);
    }

    const int result = reinterpret_cast<ConnectFn>(g_realConnect)(socket, name, length);

    if (hadInlinePatch)
    {
        ReinstallInlinePatch(&g_connectPatch);
    }

    ReleaseSRWLockExclusive(&g_inlineLock);
    return result;
}

static int CallOriginalWsaConnect(
    SOCKET socket,
    const sockaddr *name,
    int length,
    LPWSABUF callerData,
    LPWSABUF calleeData,
    LPQOS sqos,
    LPQOS gqos)
{
    AcquireSRWLockExclusive(&g_inlineLock);
    const bool hadInlinePatch = g_wsaConnectPatch.Installed && g_wsaConnectPatch.Target;
    if (hadInlinePatch)
    {
        RemoveInlinePatch(&g_wsaConnectPatch);
    }

    const int result = reinterpret_cast<WsaConnectFn>(g_realWsaConnect)(socket, name, length, callerData, calleeData, sqos, gqos);

    if (hadInlinePatch)
    {
        ReinstallInlinePatch(&g_wsaConnectPatch);
    }

    ReleaseSRWLockExclusive(&g_inlineLock);
    return result;
}

static int CallOriginalGetSockName(SOCKET socket, sockaddr *name, int *length)
{
    AcquireSRWLockExclusive(&g_inlineLock);
    const bool hadInlinePatch = g_getSockNamePatch.Installed && g_getSockNamePatch.Target;
    if (hadInlinePatch)
    {
        RemoveInlinePatch(&g_getSockNamePatch);
    }

    const int result = reinterpret_cast<GetSockNameFn>(g_realGetSockName)(socket, name, length);

    if (hadInlinePatch)
    {
        ReinstallInlinePatch(&g_getSockNamePatch);
    }

    ReleaseSRWLockExclusive(&g_inlineLock);
    return result;
}

static int CallOriginalGetAddrInfoA(PCSTR nodeName, PCSTR serviceName, const ADDRINFOA *hints, PADDRINFOA *result)
{
    AcquireSRWLockExclusive(&g_inlineLock);
    const bool hadInlinePatch = g_getAddrInfoAPatch.Installed && g_getAddrInfoAPatch.Target;
    if (hadInlinePatch)
    {
        RemoveInlinePatch(&g_getAddrInfoAPatch);
    }

    const int status = reinterpret_cast<GetAddrInfoAFn>(g_realGetAddrInfoA)(nodeName, serviceName, hints, result);

    if (hadInlinePatch)
    {
        ReinstallInlinePatch(&g_getAddrInfoAPatch);
    }

    ReleaseSRWLockExclusive(&g_inlineLock);
    return status;
}

static int CallOriginalGetAddrInfoW(PCWSTR nodeName, PCWSTR serviceName, const ADDRINFOW *hints, PADDRINFOW *result)
{
    AcquireSRWLockExclusive(&g_inlineLock);
    const bool hadInlinePatch = g_getAddrInfoWPatch.Installed && g_getAddrInfoWPatch.Target;
    if (hadInlinePatch)
    {
        RemoveInlinePatch(&g_getAddrInfoWPatch);
    }

    const int status = reinterpret_cast<GetAddrInfoWFn>(g_realGetAddrInfoW)(nodeName, serviceName, hints, result);

    if (hadInlinePatch)
    {
        ReinstallInlinePatch(&g_getAddrInfoWPatch);
    }

    ReleaseSRWLockExclusive(&g_inlineLock);
    return status;
}

static int WSAAPI HookBind(SOCKET socket, const sockaddr *name, int length)
{
    if (!g_realBind)
    {
        EnsureRealWinsockFunctions();
    }

    if (!g_realBind)
    {
        WSASetLastError(WSAEINVAL);
        return SOCKET_ERROR;
    }

    const bool isPortShiftEndpoint = g_hasPortOffset
        && IsSupportedInternetEndpoint(name, length);
    const bool isLegacyAddressEndpoint = !g_hasPortOffset
        && g_hasBindAddress
        && name
        && name->sa_family == AF_INET
        && length >= static_cast<int>(sizeof(sockaddr_in));
    if (!IsHookDisabled()
        && (isPortShiftEndpoint || isLegacyAddressEndpoint)
        && !IsDevwtManagementPort(name, length))
    {
        sockaddr_storage copy{};
        memcpy(&copy, name, static_cast<size_t>(length));
        if (isLegacyAddressEndpoint)
        {
            reinterpret_cast<sockaddr_in *>(&copy)->sin_addr = g_bindAddress;
        }

        u_short originalPort = 0;
        TryGetEndpointPort(name, length, &originalPort);
        auto targetPort = ShiftPort(originalPort);
        int lastBindError = 0;
        for (int attempt = 0; attempt < g_maxPortShiftBindAttempts; attempt++)
        {
            SetEndpointPort(reinterpret_cast<sockaddr *>(&copy), length, targetPort);
            const auto *target = reinterpret_cast<const sockaddr *>(&copy);
            const int result = CallOriginalBind(socket, target, length);
            if (result == 0)
            {
                RememberSocketPort(socket, name, length, targetPort);
                AppendPortBinding(name, length, target, length, SocketProtocolName(socket));
                return result;
            }
            lastBindError = WSAGetLastError();
            const bool retryExcludedPort = lastBindError == WSAEACCES;
            const bool retryDifferentNaturalPortCollision = lastBindError == WSAEADDRINUSE
                && IsTargetOwnedByDifferentNaturalPort(
                    name,
                    length,
                    target,
                    length,
                    SocketProtocolName(socket));
            if (!isPortShiftEndpoint
                || originalPort == 0
                || (!retryExcludedPort && !retryDifferentNaturalPortCollision))
            {
                WSASetLastError(lastBindError);
                return result;
            }

            targetPort = NextShiftedPort(targetPort);
        }

        WSASetLastError(lastBindError);
        return SOCKET_ERROR;
    }

    return CallOriginalBind(socket, name, length);
}

static int WSAAPI HookConnect(SOCKET socket, const sockaddr *name, int length)
{
    if (!g_realConnect)
    {
        EnsureRealWinsockFunctions();
    }

    if (!g_realConnect)
    {
        WSASetLastError(WSAEINVAL);
        return SOCKET_ERROR;
    }

    if (!IsHookDisabled() && !g_hasPortOffset && g_hasConnectAddress && IsLoopbackV4(name, length) && !IsDevwtManagementPort(name, length))
    {
        sockaddr_in copy = *reinterpret_cast<const sockaddr_in *>(name);
        copy.sin_addr = g_connectAddress;
        return CallOriginalConnect(socket, reinterpret_cast<const sockaddr *>(&copy), length);
    }

    return CallOriginalConnect(socket, name, length);
}

static int WSAAPI HookWsaConnect(
    SOCKET socket,
    const sockaddr *name,
    int length,
    LPWSABUF callerData,
    LPWSABUF calleeData,
    LPQOS sqos,
    LPQOS gqos)
{
    if (!g_realWsaConnect)
    {
        EnsureRealWinsockFunctions();
    }

    if (!g_realWsaConnect)
    {
        WSASetLastError(WSAEINVAL);
        return SOCKET_ERROR;
    }

    if (!IsHookDisabled() && !g_hasPortOffset && g_hasConnectAddress && IsLoopbackV4(name, length) && !IsDevwtManagementPort(name, length))
    {
        sockaddr_in copy = *reinterpret_cast<const sockaddr_in *>(name);
        copy.sin_addr = g_connectAddress;
        return CallOriginalWsaConnect(socket, reinterpret_cast<const sockaddr *>(&copy), length, callerData, calleeData, sqos, gqos);
    }

    return CallOriginalWsaConnect(socket, name, length, callerData, calleeData, sqos, gqos);
}

static int WSAAPI HookGetSockName(SOCKET socket, sockaddr *name, int *length)
{
    if (!g_realGetSockName)
    {
        EnsureRealWinsockFunctions();
    }

    if (!g_realGetSockName)
    {
        WSASetLastError(WSAEINVAL);
        return SOCKET_ERROR;
    }

    const int callerCapacity = length ? *length : 0;
    const int result = CallOriginalGetSockName(socket, name, length);
    if (result == 0 &&
        !IsHookDisabled() &&
        length &&
        name &&
        IsSupportedInternetEndpoint(name, *length))
    {
        u_short targetPort = 0;
        TryGetEndpointPort(name, *length, &targetPort);
        sockaddr_storage originalEndpoint{};
        int originalEndpointLength = 0;
        if (TryGetRememberedOriginalEndpoint(
                socket,
                targetPort,
                &originalEndpoint,
                &originalEndpointLength)
            && callerCapacity >= originalEndpointLength)
        {
            memcpy(name, &originalEndpoint, static_cast<size_t>(originalEndpointLength));
            *length = originalEndpointLength;
        }
        else if (IsAssignedBindV4(name, *length))
        {
            auto *v4 = reinterpret_cast<sockaddr_in *>(name);
            v4->sin_addr.s_addr = htonl(INADDR_LOOPBACK);
        }
    }

    return result;
}

static int WSAAPI HookGetAddrInfoA(PCSTR nodeName, PCSTR serviceName, const ADDRINFOA *hints, PADDRINFOA *result)
{
    if (!g_realGetAddrInfoA)
    {
        EnsureRealWinsockFunctions();
    }

    if (!g_realGetAddrInfoA)
    {
        return EAI_FAIL;
    }

    if (!IsHookDisabled()
        && !g_hasPortOffset
        && IsLocalhostNameA(nodeName)
        && !IsDevwtManagementServiceA(serviceName))
    {
        IN_ADDR address{};
        const bool passive = hints && ((hints->ai_flags & AI_PASSIVE) != 0);
        char rewrittenNode[64]{};
        if (TryGetResolveAddress(passive, &address) && AddressToString(address, rewrittenNode, static_cast<DWORD>(sizeof(rewrittenNode))))
        {
            ADDRINFOA rewrittenHints{};
            if (hints)
            {
                rewrittenHints = *hints;
            }

            rewrittenHints.ai_family = AF_INET;
#ifdef AI_V4MAPPED
            rewrittenHints.ai_flags &= ~AI_V4MAPPED;
#endif
#ifdef AI_ALL
            rewrittenHints.ai_flags &= ~AI_ALL;
#endif
            return CallOriginalGetAddrInfoA(rewrittenNode, serviceName, &rewrittenHints, result);
        }
    }

    return CallOriginalGetAddrInfoA(nodeName, serviceName, hints, result);
}

static int WSAAPI HookGetAddrInfoW(PCWSTR nodeName, PCWSTR serviceName, const ADDRINFOW *hints, PADDRINFOW *result)
{
    if (!g_realGetAddrInfoW)
    {
        EnsureRealWinsockFunctions();
    }

    if (!g_realGetAddrInfoW)
    {
        return EAI_FAIL;
    }

    if (!IsHookDisabled()
        && !g_hasPortOffset
        && IsLocalhostNameW(nodeName)
        && !IsDevwtManagementServiceW(serviceName))
    {
        IN_ADDR address{};
        const bool passive = hints && ((hints->ai_flags & AI_PASSIVE) != 0);
        wchar_t rewrittenNode[64]{};
        if (TryGetResolveAddress(passive, &address) && AddressToStringW(address, rewrittenNode, static_cast<DWORD>(sizeof(rewrittenNode) / sizeof(rewrittenNode[0]))))
        {
            ADDRINFOW rewrittenHints{};
            if (hints)
            {
                rewrittenHints = *hints;
            }

            rewrittenHints.ai_family = AF_INET;
#ifdef AI_V4MAPPED
            rewrittenHints.ai_flags &= ~AI_V4MAPPED;
#endif
#ifdef AI_ALL
            rewrittenHints.ai_flags &= ~AI_ALL;
#endif
            return CallOriginalGetAddrInfoW(rewrittenNode, serviceName, &rewrittenHints, result);
        }
    }

    return CallOriginalGetAddrInfoW(nodeName, serviceName, hints, result);
}

static void InjectCreatedChild(
    DWORD originalCreationFlags,
    LPCWSTR currentDirectory,
    LPPROCESS_INFORMATION processInformation)
{
    UNREFERENCED_PARAMETER(originalCreationFlags);
    DebugLog("InjectCreatedChild entered");

    if (IsHookDisabled())
    {
        DebugLog("InjectCreatedChild skipped: hook disabled");
        return;
    }

    if (!processInformation || !processInformation->hProcess || !processInformation->hThread)
    {
        DebugLog("InjectCreatedChild skipped: missing process information");
        return;
    }

    ChildHookConfig config{};
    if (!ResolveChildHookConfig(currentDirectory, &config))
    {
        DebugLog("InjectCreatedChild skipped: no child hook config");
        return;
    }

    if (!ShouldInjectImage(QueryProcessImagePath(processInformation->hProcess)))
    {
        DebugLog("InjectCreatedChild skipped: image denied");
        return;
    }

    WritePidConfigForChild(processInformation->dwProcessId, config);
    InjectDllIntoProcess(processInformation->hProcess);
    DebugLog("InjectCreatedChild injected");
}

static BOOL WINAPI HookCreateProcessW(
    LPCWSTR applicationName,
    LPWSTR commandLine,
    LPSECURITY_ATTRIBUTES processAttributes,
    LPSECURITY_ATTRIBUTES threadAttributes,
    BOOL inheritHandles,
    DWORD creationFlags,
    LPVOID environment,
    LPCWSTR currentDirectory,
    LPSTARTUPINFOW startupInfo,
    LPPROCESS_INFORMATION processInformation)
{
    DebugLog("HookCreateProcessW entered");
    if (!g_realCreateProcessW)
    {
        g_realCreateProcessW = ResolveRealCreateProcessW();
    }

    const DWORD originalCreationFlags = creationFlags;
    const bool hadKernel32InlinePatch = PauseInlinePatch(&g_createProcessWPatch);
    const bool hadKernelBaseInlinePatch = PauseInlinePatch(&g_createProcessWKernelBasePatch);
    const BOOL ok = g_realCreateProcessW(
        applicationName,
        commandLine,
        processAttributes,
        threadAttributes,
        inheritHandles,
        creationFlags | CREATE_SUSPENDED,
        environment,
        currentDirectory,
        startupInfo,
        processInformation);
    ResumeInlinePatch(&g_createProcessWKernelBasePatch, hadKernelBaseInlinePatch);
    ResumeInlinePatch(&g_createProcessWPatch, hadKernel32InlinePatch);

    if (ok)
    {
        InjectCreatedChild(originalCreationFlags, currentDirectory, processInformation);
        if ((originalCreationFlags & CREATE_SUSPENDED) == 0 && processInformation && processInformation->hThread)
        {
            ResumeThread(processInformation->hThread);
        }
    }

    return ok;
}

static BOOL WINAPI HookCreateProcessA(
    LPCSTR applicationName,
    LPSTR commandLine,
    LPSECURITY_ATTRIBUTES processAttributes,
    LPSECURITY_ATTRIBUTES threadAttributes,
    BOOL inheritHandles,
    DWORD creationFlags,
    LPVOID environment,
    LPCSTR currentDirectory,
    LPSTARTUPINFOA startupInfo,
    LPPROCESS_INFORMATION processInformation)
{
    DebugLog("HookCreateProcessA entered");
    if (!g_realCreateProcessA)
    {
        g_realCreateProcessA = ResolveRealCreateProcessA();
    }

    const DWORD originalCreationFlags = creationFlags;
    const auto wideCurrentDirectory = AnsiToWide(currentDirectory);
    const bool hadKernel32InlinePatch = PauseInlinePatch(&g_createProcessAPatch);
    const bool hadKernelBaseInlinePatch = PauseInlinePatch(&g_createProcessAKernelBasePatch);
    const BOOL ok = g_realCreateProcessA(
        applicationName,
        commandLine,
        processAttributes,
        threadAttributes,
        inheritHandles,
        creationFlags | CREATE_SUSPENDED,
        environment,
        currentDirectory,
        startupInfo,
        processInformation);
    ResumeInlinePatch(&g_createProcessAKernelBasePatch, hadKernelBaseInlinePatch);
    ResumeInlinePatch(&g_createProcessAPatch, hadKernel32InlinePatch);

    if (ok)
    {
        InjectCreatedChild(originalCreationFlags, wideCurrentDirectory.c_str(), processInformation);
        if ((originalCreationFlags & CREATE_SUSPENDED) == 0 && processInformation && processInformation->hThread)
        {
            ResumeThread(processInformation->hThread);
        }
    }

    return ok;
}

static FARPROC WINAPI HookGetProcAddress(HMODULE module, LPCSTR name)
{
    FARPROC real = g_realGetProcAddress(module, name);
    if (IsHookDisabled())
    {
        return real;
    }

    if (!name || reinterpret_cast<uintptr_t>(name) <= 0xffff)
    {
        return real;
    }

    if (!g_childrenOnlyMode && IsWs2Module(module))
    {
        if (_stricmp(name, "bind") == 0)
        {
            g_realBind = reinterpret_cast<BindFn>(real);
            return reinterpret_cast<FARPROC>(&HookBind);
        }

        if (_stricmp(name, "connect") == 0)
        {
            g_realConnect = reinterpret_cast<ConnectFn>(real);
            return reinterpret_cast<FARPROC>(&HookConnect);
        }

        if (_stricmp(name, "WSAConnect") == 0)
        {
            g_realWsaConnect = reinterpret_cast<WsaConnectFn>(real);
            return reinterpret_cast<FARPROC>(&HookWsaConnect);
        }

        if (_stricmp(name, "getsockname") == 0)
        {
            g_realGetSockName = reinterpret_cast<GetSockNameFn>(real);
            return reinterpret_cast<FARPROC>(&HookGetSockName);
        }

        if (_stricmp(name, "getaddrinfo") == 0 || _stricmp(name, "GetAddrInfoA") == 0)
        {
            g_realGetAddrInfoA = reinterpret_cast<GetAddrInfoAFn>(real);
            return reinterpret_cast<FARPROC>(&HookGetAddrInfoA);
        }

        if (_stricmp(name, "GetAddrInfoW") == 0)
        {
            g_realGetAddrInfoW = reinterpret_cast<GetAddrInfoWFn>(real);
            return reinterpret_cast<FARPROC>(&HookGetAddrInfoW);
        }
    }

    if (IsKernelLoaderModule(module))
    {
        if (_stricmp(name, "CreateProcessW") == 0)
        {
            g_realCreateProcessW = reinterpret_cast<CreateProcessWFn>(real);
            return reinterpret_cast<FARPROC>(&HookCreateProcessW);
        }

        if (_stricmp(name, "CreateProcessA") == 0)
        {
            g_realCreateProcessA = reinterpret_cast<CreateProcessAFn>(real);
            return reinterpret_cast<FARPROC>(&HookCreateProcessA);
        }

    }

    return real;
}

static void RewriteProcedureLookup(HMODULE module, const char *name, WORD ordinal, void **address)
{
    if (!address || !*address)
    {
        return;
    }

    const bool isWs2 = IsWs2Module(module);
    const bool isKernel = IsKernelLoaderModule(module);
    const bool byName = name && *name;
    if (!g_childrenOnlyMode && isWs2 && ((byName && _stricmp(name, "bind") == 0) || (!byName && ordinal == 2)))
    {
        g_realBind = reinterpret_cast<BindFn>(*address);
        *address = reinterpret_cast<void *>(&HookBind);
    }
    else if (!g_childrenOnlyMode && isWs2 && ((byName && _stricmp(name, "connect") == 0) || (!byName && ordinal == 4)))
    {
        g_realConnect = reinterpret_cast<ConnectFn>(*address);
        *address = reinterpret_cast<void *>(&HookConnect);
    }
    else if (!g_childrenOnlyMode && isWs2 && byName && _stricmp(name, "WSAConnect") == 0)
    {
        g_realWsaConnect = reinterpret_cast<WsaConnectFn>(*address);
        *address = reinterpret_cast<void *>(&HookWsaConnect);
    }
    else if (!g_childrenOnlyMode && isWs2 && ((byName && _stricmp(name, "getsockname") == 0) || (!byName && ordinal == 6)))
    {
        g_realGetSockName = reinterpret_cast<GetSockNameFn>(*address);
        *address = reinterpret_cast<void *>(&HookGetSockName);
    }
    else if (!g_childrenOnlyMode && isWs2 && byName && (_stricmp(name, "getaddrinfo") == 0 || _stricmp(name, "GetAddrInfoA") == 0))
    {
        g_realGetAddrInfoA = reinterpret_cast<GetAddrInfoAFn>(*address);
        *address = reinterpret_cast<void *>(&HookGetAddrInfoA);
    }
    else if (!g_childrenOnlyMode && isWs2 && byName && _stricmp(name, "GetAddrInfoW") == 0)
    {
        g_realGetAddrInfoW = reinterpret_cast<GetAddrInfoWFn>(*address);
        *address = reinterpret_cast<void *>(&HookGetAddrInfoW);
    }
    else if (isKernel && byName && _stricmp(name, "CreateProcessW") == 0)
    {
        g_realCreateProcessW = reinterpret_cast<CreateProcessWFn>(*address);
        *address = reinterpret_cast<void *>(&HookCreateProcessW);
    }
    else if (isKernel && byName && _stricmp(name, "CreateProcessA") == 0)
    {
        g_realCreateProcessA = reinterpret_cast<CreateProcessAFn>(*address);
        *address = reinterpret_cast<void *>(&HookCreateProcessA);
    }
}

static LONG NTAPI HookLdrGetProcedureAddress(HMODULE module, DevwtAnsiString *functionName, WORD ordinal, PVOID *functionAddress)
{
    const LONG status = g_realLdrGetProcedureAddress(module, functionName, ordinal, functionAddress);
    if (status >= 0 && functionAddress)
    {
        char nameBuffer[128]{};
        const char *name = nullptr;
        if (functionName && functionName->Buffer && functionName->Length > 0)
        {
            const auto copyLength = min(static_cast<size_t>(functionName->Length), sizeof(nameBuffer) - 1);
            memcpy(nameBuffer, functionName->Buffer, copyLength);
            nameBuffer[copyLength] = '\0';
            name = nameBuffer;
        }

        RewriteProcedureLookup(module, name, ordinal, reinterpret_cast<void **>(functionAddress));
    }

    return status;
}

static void EnsureRealWinsockFunctions()
{
    if (!g_realGetProcAddress)
    {
        g_realGetProcAddress = &GetProcAddress;
    }

    if (!g_childrenOnlyMode)
    {
        if (const auto ws2 = GetModuleHandleW(L"ws2_32.dll"))
        {
            if (!g_realBind)
            {
                g_realBind = reinterpret_cast<BindFn>(g_realGetProcAddress(ws2, "bind"));
            }

            if (!g_realConnect)
            {
                g_realConnect = reinterpret_cast<ConnectFn>(g_realGetProcAddress(ws2, "connect"));
            }

            if (!g_realWsaConnect)
            {
                g_realWsaConnect = reinterpret_cast<WsaConnectFn>(g_realGetProcAddress(ws2, "WSAConnect"));
            }

            if (!g_realGetSockName)
            {
                g_realGetSockName = reinterpret_cast<GetSockNameFn>(g_realGetProcAddress(ws2, "getsockname"));
            }

            if (!g_realGetAddrInfoA)
            {
                g_realGetAddrInfoA = reinterpret_cast<GetAddrInfoAFn>(g_realGetProcAddress(ws2, "getaddrinfo"));
                if (!g_realGetAddrInfoA)
                {
                    g_realGetAddrInfoA = reinterpret_cast<GetAddrInfoAFn>(g_realGetProcAddress(ws2, "GetAddrInfoA"));
                }
            }

            if (!g_realGetAddrInfoW)
            {
                g_realGetAddrInfoW = reinterpret_cast<GetAddrInfoWFn>(g_realGetProcAddress(ws2, "GetAddrInfoW"));
            }

            if (g_realBind)
            {
                InstallInlinePatch(&g_bindPatch, reinterpret_cast<void *>(g_realBind), reinterpret_cast<void *>(&HookBind));
            }

            if (g_realConnect)
            {
                InstallInlinePatch(&g_connectPatch, reinterpret_cast<void *>(g_realConnect), reinterpret_cast<void *>(&HookConnect));
            }

            if (g_realWsaConnect)
            {
                InstallInlinePatch(&g_wsaConnectPatch, reinterpret_cast<void *>(g_realWsaConnect), reinterpret_cast<void *>(&HookWsaConnect));
            }

            if (g_realGetSockName)
            {
                InstallInlinePatch(&g_getSockNamePatch, reinterpret_cast<void *>(g_realGetSockName), reinterpret_cast<void *>(&HookGetSockName));
            }

            if (g_realGetAddrInfoA)
            {
                InstallInlinePatch(&g_getAddrInfoAPatch, reinterpret_cast<void *>(g_realGetAddrInfoA), reinterpret_cast<void *>(&HookGetAddrInfoA));
            }

            if (g_realGetAddrInfoW)
            {
                InstallInlinePatch(&g_getAddrInfoWPatch, reinterpret_cast<void *>(g_realGetAddrInfoW), reinterpret_cast<void *>(&HookGetAddrInfoW));
            }
        }
    }

    if (!g_realLdrGetProcedureAddress)
    {
        const auto ntdll = GetModuleHandleW(L"ntdll.dll");
        if (ntdll)
        {
            g_realLdrGetProcedureAddress = reinterpret_cast<LdrGetProcedureAddressFn>(g_realGetProcAddress(ntdll, "LdrGetProcedureAddress"));
        }
    }

    if (!g_realCreateProcessW)
    {
        g_realCreateProcessW = ResolveRealCreateProcessW();
    }

    if (!g_realCreateProcessA)
    {
        g_realCreateProcessA = ResolveRealCreateProcessA();
    }

    const auto kernel32 = GetModuleHandleW(L"kernel32.dll");
    const auto kernelBase = GetModuleHandleW(L"kernelbase.dll");
    const auto kernel32CreateProcessW = reinterpret_cast<CreateProcessWFn>(ResolveProcedureAddress(kernel32, "CreateProcessW"));
    const auto kernel32CreateProcessA = reinterpret_cast<CreateProcessAFn>(ResolveProcedureAddress(kernel32, "CreateProcessA"));
    const auto kernelBaseCreateProcessW = reinterpret_cast<CreateProcessWFn>(ResolveProcedureAddress(kernelBase, "CreateProcessW"));
    const auto kernelBaseCreateProcessA = reinterpret_cast<CreateProcessAFn>(ResolveProcedureAddress(kernelBase, "CreateProcessA"));

    if (kernel32CreateProcessW)
    {
        InstallInlinePatch(&g_createProcessWPatch, reinterpret_cast<void *>(kernel32CreateProcessW), reinterpret_cast<void *>(&HookCreateProcessW));
    }

    if (kernel32CreateProcessA)
    {
        InstallInlinePatch(&g_createProcessAPatch, reinterpret_cast<void *>(kernel32CreateProcessA), reinterpret_cast<void *>(&HookCreateProcessA));
    }

    if (kernelBaseCreateProcessW && reinterpret_cast<void *>(kernelBaseCreateProcessW) != reinterpret_cast<void *>(kernel32CreateProcessW))
    {
        InstallInlinePatch(&g_createProcessWKernelBasePatch, reinterpret_cast<void *>(kernelBaseCreateProcessW), reinterpret_cast<void *>(&HookCreateProcessW));
    }

    if (kernelBaseCreateProcessA && reinterpret_cast<void *>(kernelBaseCreateProcessA) != reinterpret_cast<void *>(kernel32CreateProcessA))
    {
        InstallInlinePatch(&g_createProcessAKernelBasePatch, reinterpret_cast<void *>(kernelBaseCreateProcessA), reinterpret_cast<void *>(&HookCreateProcessA));
    }

}

static void PatchModuleImports(HMODULE module)
{
    char modulePath[MAX_PATH]{};
    if (GetModuleFileNameA(module, modulePath, static_cast<DWORD>(sizeof(modulePath))))
    {
        const char *moduleName = strrchr(modulePath, '\\');
        moduleName = moduleName ? moduleName + 1 : modulePath;
        if (IsDllName(moduleName, "KERNEL32.dll") ||
            IsDllName(moduleName, "KERNELBASE.dll") ||
            IsDllName(moduleName, "NTDLL.dll") ||
            IsDllName(moduleName, "SHELL32.dll") ||
            IsDllName(moduleName, "WS2_32.dll") ||
            IsDllName(moduleName, "WSOCK32.dll") ||
            IsDllName(moduleName, "devwt-hook.dll"))
        {
            return;
        }
    }

    const auto base = reinterpret_cast<uint8_t *>(module);
    const auto dos = reinterpret_cast<PIMAGE_DOS_HEADER>(base);
    if (!dos || dos->e_magic != IMAGE_DOS_SIGNATURE)
    {
        return;
    }

    const auto nt = reinterpret_cast<PIMAGE_NT_HEADERS>(base + dos->e_lfanew);
    if (!nt || nt->Signature != IMAGE_NT_SIGNATURE)
    {
        return;
    }

    const auto directory = nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_IMPORT];
    if (directory.VirtualAddress == 0 || directory.Size == 0)
    {
        return;
    }

    auto import = reinterpret_cast<PIMAGE_IMPORT_DESCRIPTOR>(base + directory.VirtualAddress);
    for (; import->Name != 0; import++)
    {
        const auto dllName = reinterpret_cast<const char *>(base + import->Name);
        const bool isWs2 = IsDllName(dllName, "WS2_32.dll") || IsDllName(dllName, "WSOCK32.dll");
        const bool isKernel =
            IsDllName(dllName, "KERNEL32.dll") ||
            IsDllName(dllName, "KERNELBASE.dll") ||
            StartsWithIgnoreCase(dllName, "api-ms-win-core-libraryloader") ||
            StartsWithIgnoreCase(dllName, "ext-ms-win-kernel32-libraryloader") ||
            StartsWithIgnoreCase(dllName, "api-ms-win-core-processthreads") ||
            StartsWithIgnoreCase(dllName, "ext-ms-win-kernel32-processthreads");
        const bool isNtdll = IsDllName(dllName, "NTDLL.dll");
        if (g_childrenOnlyMode && isWs2)
        {
            continue;
        }

        if (!isWs2 && !isKernel && !isNtdll)
        {
            continue;
        }

        auto original = reinterpret_cast<PIMAGE_THUNK_DATA>(base + import->OriginalFirstThunk);
        auto thunk = reinterpret_cast<PIMAGE_THUNK_DATA>(base + import->FirstThunk);
        if (!import->OriginalFirstThunk)
        {
            continue;
        }

        for (; original->u1.AddressOfData != 0 && thunk->u1.Function != 0; original++, thunk++)
        {
            if (IMAGE_SNAP_BY_ORDINAL(original->u1.Ordinal))
            {
                if (isWs2)
                {
                    const auto ordinal = static_cast<WORD>(IMAGE_ORDINAL(original->u1.Ordinal));
                    if (ordinal == 2)
                    {
                        PatchSlot(reinterpret_cast<void **>(&thunk->u1.Function), reinterpret_cast<void *>(&HookBind));
                    }
                    else if (ordinal == 4)
                    {
                        PatchSlot(reinterpret_cast<void **>(&thunk->u1.Function), reinterpret_cast<void *>(&HookConnect));
                    }
                    else if (ordinal == 6)
                    {
                        PatchSlot(reinterpret_cast<void **>(&thunk->u1.Function), reinterpret_cast<void *>(&HookGetSockName));
                    }
                }

                continue;
            }

            const auto importByName = reinterpret_cast<PIMAGE_IMPORT_BY_NAME>(base + original->u1.AddressOfData);
            const auto functionName = reinterpret_cast<const char *>(importByName->Name);
            void *replacement = nullptr;

            if (isWs2 && _stricmp(functionName, "bind") == 0)
            {
                replacement = reinterpret_cast<void *>(&HookBind);
            }
            else if (isWs2 && _stricmp(functionName, "connect") == 0)
            {
                replacement = reinterpret_cast<void *>(&HookConnect);
            }
            else if (isWs2 && _stricmp(functionName, "WSAConnect") == 0)
            {
                replacement = reinterpret_cast<void *>(&HookWsaConnect);
            }
            else if (isWs2 && _stricmp(functionName, "getsockname") == 0)
            {
                replacement = reinterpret_cast<void *>(&HookGetSockName);
            }
            else if (isWs2 && (_stricmp(functionName, "getaddrinfo") == 0 || _stricmp(functionName, "GetAddrInfoA") == 0))
            {
                replacement = reinterpret_cast<void *>(&HookGetAddrInfoA);
            }
            else if (isWs2 && _stricmp(functionName, "GetAddrInfoW") == 0)
            {
                replacement = reinterpret_cast<void *>(&HookGetAddrInfoW);
            }
            else if (isKernel && _stricmp(functionName, "GetProcAddress") == 0 && g_realGetProcAddress)
            {
                replacement = reinterpret_cast<void *>(&HookGetProcAddress);
            }
            else if (isKernel && _stricmp(functionName, "CreateProcessW") == 0)
            {
                replacement = reinterpret_cast<void *>(&HookCreateProcessW);
            }
            else if (isKernel && _stricmp(functionName, "CreateProcessA") == 0)
            {
                replacement = reinterpret_cast<void *>(&HookCreateProcessA);
            }
            else if (isNtdll && _stricmp(functionName, "LdrGetProcedureAddress") == 0 && g_realLdrGetProcedureAddress)
            {
                replacement = reinterpret_cast<void *>(&HookLdrGetProcedureAddress);
            }

            if (replacement)
            {
                PatchSlot(reinterpret_cast<void **>(&thunk->u1.Function), replacement);
            }
        }
    }
}

static void PatchAllModules()
{
    if (IsHookDisabled())
    {
        return;
    }

    if (InterlockedCompareExchange(&g_patchRunning, 1, 0) != 0)
    {
        return;
    }

    EnsureRealWinsockFunctions();
    const HANDLE snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPMODULE | TH32CS_SNAPMODULE32, GetCurrentProcessId());
    if (snapshot != INVALID_HANDLE_VALUE)
    {
        MODULEENTRY32W entry{};
        entry.dwSize = sizeof(entry);
        if (Module32FirstW(snapshot, &entry))
        {
            do
            {
                PatchModuleImports(entry.hModule);
            } while (Module32NextW(snapshot, &entry));
        }

        CloseHandle(snapshot);
    }

    InterlockedExchange(&g_patchRunning, 0);
}

static DWORD WINAPI PatchWorker(void *)
{
    for (int index = 0; index < 100; index++)
    {
        PatchAllModules();
        Sleep(50);
    }

    return 0;
}

BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID)
{
    if (reason != DLL_PROCESS_ATTACH)
    {
        return TRUE;
    }

    DisableThreadLibraryCalls(instance);
    GetModuleFileNameW(instance, g_hookDllPath, MAX_PATH);
    g_realGetProcAddress = &GetProcAddress;
    if (IsHookDisabled())
    {
        return TRUE;
    }

    ReloadHookConfiguration();
    PatchAllModules();

    HANDLE worker = CreateThread(nullptr, 0, &PatchWorker, nullptr, 0, nullptr);
    if (worker)
    {
        CloseHandle(worker);
    }

    return TRUE;
}
