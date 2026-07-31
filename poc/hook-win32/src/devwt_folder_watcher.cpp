#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <appmodel.h>
#include <evntrace.h>
#include <evntcons.h>
#include <tdh.h>
#include <tlhelp32.h>
#include <winternl.h>
#include <psapi.h>

#include <algorithm>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <fstream>
#include <iostream>
#include <string>
#include <unordered_map>
#include <unordered_set>
#include <vector>

static const GUID DevwtKernelProcessProviderGuid =
{
    0x22fb2cd6,
    0x0e7b,
    0x422b,
    { 0xa0, 0xc7, 0x2f, 0xad, 0x1f, 0xd0, 0xe7, 0x16 }
};

static constexpr ULONGLONG DevwtKernelProcessKeyword = 0x10;
static constexpr ULONGLONG DevwtKernelAnalyticKeyword = 0x8000000000000000;
static constexpr UCHAR DevwtEventOpcodeStart = 1;
static constexpr USHORT DevwtProcessStartTask = 1;

struct FolderMap
{
    std::wstring Folder;
    std::string ContextId;
    std::string BindIp;
    std::string ConnectIp;
    std::string PortOffset;
};

struct ProcessStrings
{
    std::wstring ImagePath;
    std::wstring CurrentDirectory;
    std::wstring PackageFamilyName;
};

struct ChildrenOnlyImage
{
    std::wstring ImagePath;
};

struct ChildrenOnlyPackageFamily
{
    std::wstring PackageFamilyName;
};

struct RemoteUnicodeString64
{
    USHORT Length;
    USHORT MaximumLength;
    ULONGLONG Buffer;
};

using NtQueryInformationProcessFn = NTSTATUS(NTAPI *)(HANDLE, PROCESSINFOCLASS, PVOID, ULONG, PULONG);

static std::wstring EnsureTrailingSlash(std::wstring value)
{
    if (!value.empty() && value.back() != L'\\' && value.back() != L'/')
    {
        value.push_back(L'\\');
    }

    std::replace(value.begin(), value.end(), L'/', L'\\');
    return value;
}

static std::wstring FullPath(std::wstring value)
{
    DWORD length = GetFullPathNameW(value.c_str(), 0, nullptr, nullptr);
    std::wstring result(length, L'\0');
    GetFullPathNameW(value.c_str(), length, result.data(), nullptr);
    while (!result.empty() && result.back() == L'\0')
    {
        result.pop_back();
    }

    return result;
}

static bool StartsWithIgnoreCase(const std::wstring &value, const std::wstring &prefix)
{
    if (value.size() < prefix.size())
    {
        return false;
    }

    return _wcsnicmp(value.c_str(), prefix.c_str(), prefix.size()) == 0;
}

static size_t FindIgnoreCase(const std::wstring &value, const std::wstring &needle)
{
    if (needle.empty() || value.size() < needle.size())
    {
        return std::wstring::npos;
    }

    for (size_t index = 0; index <= value.size() - needle.size(); index++)
    {
        if (_wcsnicmp(value.c_str() + index, needle.c_str(), needle.size()) == 0)
        {
            return index;
        }
    }

    return std::wstring::npos;
}

static std::wstring ReadRemoteUnicodeString(HANDLE process, const RemoteUnicodeString64 &remote)
{
    if (!remote.Buffer || remote.Length == 0 || remote.Length > 32766)
    {
        return L"";
    }

    std::wstring value(remote.Length / sizeof(wchar_t), L'\0');
    SIZE_T read = 0;
    if (!ReadProcessMemory(process, reinterpret_cast<LPCVOID>(remote.Buffer), value.data(), remote.Length, &read))
    {
        return L"";
    }

    if (read < remote.Length)
    {
        value.resize(read / sizeof(wchar_t));
    }

    return value;
}

static std::wstring QueryCurrentDirectory(HANDLE process)
{
    auto ntdll = GetModuleHandleW(L"ntdll.dll");
    auto query = reinterpret_cast<NtQueryInformationProcessFn>(GetProcAddress(ntdll, "NtQueryInformationProcess"));
    if (!query)
    {
        return L"";
    }

    PROCESS_BASIC_INFORMATION basic{};
    if (query(process, ProcessBasicInformation, &basic, sizeof(basic), nullptr) < 0 || !basic.PebBaseAddress)
    {
        return L"";
    }

    ULONGLONG processParameters = 0;
    SIZE_T read = 0;
    const auto processParametersAddress = reinterpret_cast<const BYTE *>(basic.PebBaseAddress) + 0x20;
    if (!ReadProcessMemory(process, processParametersAddress, &processParameters, sizeof(processParameters), &read) || !processParameters)
    {
        return L"";
    }

    RemoteUnicodeString64 currentDirectory{};
    if (!ReadProcessMemory(process, reinterpret_cast<LPCVOID>(processParameters + 0x38), &currentDirectory, sizeof(currentDirectory), &read))
    {
        return L"";
    }

    return EnsureTrailingSlash(FullPath(ReadRemoteUnicodeString(process, currentDirectory)));
}

static std::wstring QueryImagePath(HANDLE process)
{
    std::wstring buffer(32768, L'\0');
    DWORD length = static_cast<DWORD>(buffer.size());
    if (!QueryFullProcessImageNameW(process, 0, buffer.data(), &length))
    {
        return L"";
    }

    buffer.resize(length);
    return FullPath(buffer);
}

static std::wstring QueryPackageFamilyName(HANDLE process)
{
    UINT32 length = 0;
    LONG result = GetPackageFamilyName(process, &length, nullptr);
    if (result != ERROR_INSUFFICIENT_BUFFER || length == 0)
    {
        return L"";
    }

    std::wstring packageFamilyName(length, L'\0');
    result = GetPackageFamilyName(process, &length, packageFamilyName.data());
    if (result != ERROR_SUCCESS)
    {
        return L"";
    }

    while (!packageFamilyName.empty() && packageFamilyName.back() == L'\0')
    {
        packageFamilyName.pop_back();
    }

    return packageFamilyName;
}

static std::wstring InferPackageFamilyNameFromWindowsAppsPath(std::wstring imagePath)
{
    std::replace(imagePath.begin(), imagePath.end(), L'/', L'\\');
    const std::wstring marker = L"\\WindowsApps\\";
    const size_t markerIndex = FindIgnoreCase(imagePath, marker);
    if (markerIndex == std::wstring::npos)
    {
        return L"";
    }

    const size_t folderStart = markerIndex + marker.size();
    const size_t folderEnd = imagePath.find(L'\\', folderStart);
    if (folderEnd == std::wstring::npos || folderEnd == folderStart)
    {
        return L"";
    }

    const std::wstring folder = imagePath.substr(folderStart, folderEnd - folderStart);
    const size_t publisherSeparator = folder.rfind(L"__");
    if (publisherSeparator == std::wstring::npos || publisherSeparator + 2 >= folder.size())
    {
        return L"";
    }

    std::wstring nameAndVersion = folder.substr(0, publisherSeparator);
    const std::wstring publisherId = folder.substr(publisherSeparator + 2);
    const size_t archSeparator = nameAndVersion.rfind(L'_');
    if (archSeparator == std::wstring::npos)
    {
        return L"";
    }

    nameAndVersion.resize(archSeparator);
    const size_t versionSeparator = nameAndVersion.rfind(L'_');
    if (versionSeparator == std::wstring::npos)
    {
        return L"";
    }

    const std::wstring packageName = nameAndVersion.substr(0, versionSeparator);
    if (packageName.empty() || publisherId.empty())
    {
        return L"";
    }

    return packageName + L"_" + publisherId;
}

static ProcessStrings QueryProcessStrings(DWORD pid)
{
    ProcessStrings result;
    HANDLE process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_VM_READ, FALSE, pid);
    if (!process)
    {
        return result;
    }

    result.ImagePath = QueryImagePath(process);
    result.CurrentDirectory = QueryCurrentDirectory(process);
    result.PackageFamilyName = QueryPackageFamilyName(process);
    if (result.PackageFamilyName.empty())
    {
        result.PackageFamilyName = InferPackageFamilyNameFromWindowsAppsPath(result.ImagePath);
    }
    CloseHandle(process);
    return result;
}

static bool WritePidConfigContent(DWORD pid, const std::string &content)
{
    auto writeFile = [&](const std::wstring &configPath) -> bool
    {
        std::ofstream file(configPath, std::ios::trunc);
        if (!file)
        {
            return false;
        }

        file << content;
        return true;
    };

    bool wrote = false;

    wchar_t programData[MAX_PATH]{};
    DWORD programDataLength = GetEnvironmentVariableW(L"ProgramData", programData, MAX_PATH);
    std::wstring programDataRoot = (programDataLength > 0 && programDataLength < MAX_PATH)
        ? std::wstring(programData)
        : std::wstring(L"C:\\ProgramData");
    std::wstring devwtRoot = programDataRoot + L"\\DevWT";
    std::wstring pidRoot = devwtRoot + L"\\hook-pids";
    CreateDirectoryW(devwtRoot.c_str(), nullptr);
    CreateDirectoryW(pidRoot.c_str(), nullptr);

    wchar_t sharedConfigPath[MAX_PATH]{};
    if (_snwprintf_s(sharedConfigPath, MAX_PATH, _TRUNCATE, L"%s\\devwt-hook-poc-%lu.env", pidRoot.c_str(), pid) > 0)
    {
        wrote = writeFile(sharedConfigPath) || wrote;
    }

    wchar_t tempPath[MAX_PATH]{};
    if (GetTempPathW(MAX_PATH, tempPath))
    {
        wchar_t tempConfigPath[MAX_PATH]{};
        if (_snwprintf_s(tempConfigPath, MAX_PATH, _TRUNCATE, L"%sdevwt-hook-poc-%lu.env", tempPath, pid) > 0)
        {
            wrote = writeFile(tempConfigPath) || wrote;
        }
    }

    return wrote;
}

static std::string ToUtf8(const std::wstring &value);

static bool WritePidConfig(
    DWORD pid,
    const std::string &contextId,
    const std::string &bindIp,
    const std::string &connectIp,
    const std::string &portOffset,
    const std::wstring &mapFilePath,
    const std::wstring &portBindingsFilePath)
{
    std::string content;
    if (!contextId.empty())
    {
        content += "DEVWT_HOOK_CONTEXT_ID=" + contextId + "\n";
    }

    content += "DEVWT_HOOK_BIND_IP=" + bindIp + "\n";
    if (!connectIp.empty())
    {
        content += "DEVWT_HOOK_CONNECT_IP=" + connectIp + "\n";
    }

    if (!portOffset.empty())
    {
        content += "DEVWT_HOOK_PORT_OFFSET=" + portOffset + "\n";
    }

    if (!mapFilePath.empty())
    {
        content += "DEVWT_HOOK_MAP_FILE=" + ToUtf8(mapFilePath) + "\n";
    }

    if (!portBindingsFilePath.empty())
    {
        content += "DEVWT_HOOK_BINDINGS_FILE=" + ToUtf8(portBindingsFilePath) + "\n";
    }

    return WritePidConfigContent(pid, content);
}

static bool WriteChildrenOnlyPidConfig(
    DWORD pid,
    const std::wstring &mapFilePath,
    const std::wstring &portBindingsFilePath)
{
    std::string content = "DEVWT_HOOK_MODE=children-only\n";
    if (!mapFilePath.empty())
    {
        content += "DEVWT_HOOK_MAP_FILE=" + ToUtf8(mapFilePath) + "\n";
    }

    if (!portBindingsFilePath.empty())
    {
        content += "DEVWT_HOOK_BINDINGS_FILE=" + ToUtf8(portBindingsFilePath) + "\n";
    }

    return WritePidConfigContent(pid, content);
}

static uintptr_t FindRemoteModuleBase(DWORD pid, const std::wstring &dllPath)
{
    const std::wstring expected = FullPath(dllPath);
    HANDLE snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPMODULE | TH32CS_SNAPMODULE32, pid);
    if (snapshot == INVALID_HANDLE_VALUE)
    {
        return 0;
    }

    MODULEENTRY32W entry{};
    entry.dwSize = sizeof(entry);
    uintptr_t result = 0;
    if (Module32FirstW(snapshot, &entry))
    {
        do
        {
            if (_wcsicmp(FullPath(entry.szExePath).c_str(), expected.c_str()) == 0)
            {
                result = reinterpret_cast<uintptr_t>(entry.modBaseAddr);
                break;
            }
        } while (Module32NextW(snapshot, &entry));
    }

    CloseHandle(snapshot);
    return result;
}

static std::wstring FindRemoteDevwtHookPath(
    DWORD pid,
    HANDLE process,
    bool *enumerationSucceeded)
{
    if (enumerationSucceeded)
    {
        *enumerationSucceeded = false;
    }

    HANDLE snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPMODULE | TH32CS_SNAPMODULE32, pid);
    if (snapshot != INVALID_HANDLE_VALUE)
    {
        MODULEENTRY32W entry{};
        entry.dwSize = sizeof(entry);
        if (Module32FirstW(snapshot, &entry))
        {
            if (enumerationSucceeded)
            {
                *enumerationSucceeded = true;
            }

            do
            {
                if (_wcsicmp(entry.szModule, L"devwt-hook.dll") == 0)
                {
                    const auto result = FullPath(entry.szExePath);
                    CloseHandle(snapshot);
                    return result;
                }
            } while (Module32NextW(snapshot, &entry));
        }

        CloseHandle(snapshot);
    }

    std::vector<HMODULE> modules(256);
    DWORD bytesNeeded = 0;
    while (EnumProcessModulesEx(
        process,
        modules.data(),
        static_cast<DWORD>(modules.size() * sizeof(HMODULE)),
        &bytesNeeded,
        LIST_MODULES_ALL))
    {
        if (enumerationSucceeded)
        {
            *enumerationSucceeded = true;
        }

        if (bytesNeeded > modules.size() * sizeof(HMODULE))
        {
            modules.resize((bytesNeeded / sizeof(HMODULE)) + 32);
            continue;
        }

        const auto moduleCount = bytesNeeded / sizeof(HMODULE);
        for (size_t index = 0; index < moduleCount; index++)
        {
            wchar_t modulePath[MAX_PATH]{};
            if (!GetModuleFileNameExW(process, modules[index], modulePath, MAX_PATH))
            {
                continue;
            }

            const auto *moduleName = wcsrchr(modulePath, L'\\');
            moduleName = moduleName ? moduleName + 1 : modulePath;
            if (_wcsicmp(moduleName, L"devwt-hook.dll") == 0)
            {
                return FullPath(modulePath);
            }
        }

        break;
    }

    return {};
}

static bool CallRemoteHookReload(DWORD pid, HANDLE process, const std::wstring &dllPath)
{
    HMODULE localModule = LoadLibraryExW(dllPath.c_str(), nullptr, DONT_RESOLVE_DLL_REFERENCES);
    if (!localModule)
    {
        return false;
    }

    auto localProc = reinterpret_cast<uintptr_t>(GetProcAddress(localModule, "DevwtHookPresent"));
    const auto localBase = reinterpret_cast<uintptr_t>(localModule);
    if (!localProc || localProc < localBase)
    {
        FreeLibrary(localModule);
        return false;
    }

    const auto offset = localProc - localBase;
    FreeLibrary(localModule);

    const auto remoteBase = FindRemoteModuleBase(pid, dllPath);
    if (!remoteBase)
    {
        return false;
    }

    auto remoteProc = reinterpret_cast<LPTHREAD_START_ROUTINE>(remoteBase + offset);
    HANDLE thread = CreateRemoteThread(process, nullptr, 0, remoteProc, nullptr, 0, nullptr);
    if (!thread)
    {
        return false;
    }

    WaitForSingleObject(thread, 5000);
    DWORD exitCode = 0;
    const bool ok = GetExitCodeThread(thread, &exitCode) != 0;
    CloseHandle(thread);
    return ok;
}

static void SetInjectError(std::string *error, const std::string &message)
{
    if (error)
    {
        *error = message;
    }
}

static std::string LastErrorText(const char *operation)
{
    return std::string(operation) + " error=" + std::to_string(GetLastError());
}

static bool InjectDll(DWORD pid, const std::wstring &dllPath, std::string *error = nullptr)
{
    HANDLE process = OpenProcess(PROCESS_CREATE_THREAD | PROCESS_QUERY_INFORMATION | PROCESS_VM_OPERATION | PROCESS_VM_WRITE | PROCESS_VM_READ, FALSE, pid);
    if (!process)
    {
        SetInjectError(error, LastErrorText("OpenProcess"));
        return false;
    }

    bool moduleEnumerationSucceeded = false;
    const auto existingHookPath = FindRemoteDevwtHookPath(
        pid,
        process,
        &moduleEnumerationSucceeded);
    if (!moduleEnumerationSucceeded)
    {
        SetInjectError(error, "Remote module enumeration failed");
        CloseHandle(process);
        return false;
    }

    if (!existingHookPath.empty()
        && _wcsicmp(
            FullPath(existingHookPath).c_str(),
            FullPath(dllPath).c_str()) != 0)
    {
        const bool reloaded = CallRemoteHookReload(pid, process, existingHookPath);
        if (!reloaded)
        {
            SetInjectError(error, "Existing DevWT hook reload failed");
        }

        CloseHandle(process);
        return reloaded;
    }

    const size_t byteCount = (dllPath.size() + 1) * sizeof(wchar_t);
    void *remote = VirtualAllocEx(process, nullptr, byteCount, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    if (!remote)
    {
        SetInjectError(error, LastErrorText("VirtualAllocEx"));
        CloseHandle(process);
        return false;
    }

    SIZE_T written = 0;
    const bool wrote = WriteProcessMemory(process, remote, dllPath.c_str(), byteCount, &written) && written == byteCount;
    if (!wrote)
    {
        SetInjectError(error, LastErrorText("WriteProcessMemory"));
        VirtualFreeEx(process, remote, 0, MEM_RELEASE);
        CloseHandle(process);
        return false;
    }

    auto loadLibrary = reinterpret_cast<LPTHREAD_START_ROUTINE>(GetProcAddress(GetModuleHandleW(L"kernel32.dll"), "LoadLibraryW"));
    HANDLE thread = CreateRemoteThread(process, nullptr, 0, loadLibrary, remote, 0, nullptr);
    if (!thread)
    {
        SetInjectError(error, LastErrorText("CreateRemoteThread"));
        VirtualFreeEx(process, remote, 0, MEM_RELEASE);
        CloseHandle(process);
        return false;
    }

    WaitForSingleObject(thread, 5000);
    DWORD exitCode = 0;
    GetExitCodeThread(thread, &exitCode);
    CloseHandle(thread);
    VirtualFreeEx(process, remote, 0, MEM_RELEASE);
    const bool loaded = exitCode != 0;
    if (!loaded)
    {
        SetInjectError(error, "LoadLibraryW returned 0");
        CloseHandle(process);
        return false;
    }

    const bool reloaded = loaded && CallRemoteHookReload(pid, process, dllPath);
    if (!reloaded)
    {
        SetInjectError(error, "DevwtHookPresent reload failed");
    }

    CloseHandle(process);
    return reloaded;
}

static std::wstring ToWide(const std::string &value)
{
    const int length = MultiByteToWideChar(CP_UTF8, 0, value.c_str(), -1, nullptr, 0);
    std::wstring result(static_cast<size_t>(length - 1), L'\0');
    MultiByteToWideChar(CP_UTF8, 0, value.c_str(), -1, result.data(), length);
    return result;
}

static std::string ToUtf8(const std::wstring &value)
{
    if (value.empty())
    {
        return "";
    }

    const int length = WideCharToMultiByte(CP_UTF8, 0, value.c_str(), -1, nullptr, 0, nullptr, nullptr);
    std::string result(static_cast<size_t>(length - 1), '\0');
    WideCharToMultiByte(CP_UTF8, 0, value.c_str(), -1, result.data(), length, nullptr, nullptr);
    return result;
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
        _wcsicmp(fileName.c_str(), L"devwt-folder-watcher.exe") != 0 &&
        fileName.find(L"--DevWT-Proxy--") == std::wstring::npos;
}

struct WatcherState
{
    std::wstring DllPath;
    std::wstring MapFilePath;
    std::wstring PortBindingsFilePath;
    const std::vector<FolderMap> *Maps = nullptr;
    const std::vector<ChildrenOnlyImage> *ChildrenOnlyImages = nullptr;
    const std::vector<ChildrenOnlyPackageFamily> *ChildrenOnlyPackageFamilies = nullptr;
    std::unordered_set<DWORD> Seen;
    std::unordered_map<DWORD, ULONGLONG> RetryAfter;
    std::ofstream *Log = nullptr;
    volatile LONG ProcessStartEventsSeen = 0;
    CRITICAL_SECTION Lock{};
};

static void WriteLog(WatcherState &state, const std::string &line)
{
    std::cout << line << "\n";
    if (state.Log && *state.Log)
    {
        *state.Log << line << "\n";
        state.Log->flush();
    }
}

static bool IsChildrenOnlyImageMatch(const std::vector<ChildrenOnlyImage> &images, const std::wstring &imagePath)
{
    for (const auto &image : images)
    {
        if (_wcsicmp(imagePath.c_str(), image.ImagePath.c_str()) == 0)
        {
            return true;
        }
    }

    return false;
}

static bool IsChildrenOnlyPackageFamilyMatch(const std::vector<ChildrenOnlyPackageFamily> &packageFamilies, const std::wstring &packageFamilyName)
{
    if (packageFamilyName.empty())
    {
        return false;
    }

    for (const auto &packageFamily : packageFamilies)
    {
        if (_wcsicmp(packageFamilyName.c_str(), packageFamily.PackageFamilyName.c_str()) == 0)
        {
            return true;
        }
    }

    return false;
}

static const FolderMap *FindFolderMap(const std::vector<FolderMap> &maps, const ProcessStrings &strings)
{
    for (const auto &map : maps)
    {
        if (StartsWithIgnoreCase(strings.CurrentDirectory, map.Folder) ||
            StartsWithIgnoreCase(strings.ImagePath, map.Folder))
        {
            return &map;
        }
    }

    return nullptr;
}

static void ProcessCandidatePid(WatcherState &state, DWORD pid, ULONGLONG now)
{
    EnterCriticalSection(&state.Lock);
    if (pid == 0 || pid == GetCurrentProcessId() || state.Seen.find(pid) != state.Seen.end())
    {
        LeaveCriticalSection(&state.Lock);
        return;
    }

    const auto retry = state.RetryAfter.find(pid);
    if (retry != state.RetryAfter.end() && now < retry->second)
    {
        LeaveCriticalSection(&state.Lock);
        return;
    }

    const auto strings = QueryProcessStrings(pid);
    const bool childrenOnlyImageMatch =
        state.ChildrenOnlyImages && IsChildrenOnlyImageMatch(*state.ChildrenOnlyImages, strings.ImagePath);
    const bool childrenOnlyPackageMatch =
        state.ChildrenOnlyPackageFamilies && IsChildrenOnlyPackageFamilyMatch(*state.ChildrenOnlyPackageFamilies, strings.PackageFamilyName);
    if (childrenOnlyImageMatch || childrenOnlyPackageMatch)
    {
        const bool wrote = WriteChildrenOnlyPidConfig(pid, state.MapFilePath, state.PortBindingsFilePath);
        std::string injectError = wrote ? "" : "WriteChildrenOnlyPidConfig failed";
        const bool injected = wrote && InjectDll(pid, state.DllPath, &injectError);
        if (injected)
        {
            state.Seen.insert(pid);
            state.RetryAfter.erase(pid);
        }
        else if (injectError == "LoadLibraryW returned 0")
        {
            state.Seen.insert(pid);
            state.RetryAfter.erase(pid);
        }
        else
        {
            state.RetryAfter[pid] = now + 10000;
        }

        std::string line =
            std::string(injected ? "INJECTED_CHILDREN_ONLY " : "FAILED_CHILDREN_ONLY ") +
            std::to_string(pid) +
            " image=" + ToUtf8(strings.ImagePath) +
            " packageFamily=" + ToUtf8(strings.PackageFamilyName);
        if (!injected && !injectError.empty())
        {
            line += " injectError=" + injectError;
        }

        WriteLog(state, line);
        LeaveCriticalSection(&state.Lock);
        return;
    }

    if (!ShouldInjectImage(strings.ImagePath))
    {
        state.Seen.insert(pid);
        state.RetryAfter.erase(pid);
        LeaveCriticalSection(&state.Lock);
        return;
    }

    const FolderMap *match = state.Maps ? FindFolderMap(*state.Maps, strings) : nullptr;
    if (!match)
    {
        LeaveCriticalSection(&state.Lock);
        return;
    }

    const bool wrote = WritePidConfig(
        pid,
        match->ContextId,
        match->BindIp,
        match->ConnectIp,
        match->PortOffset,
        state.MapFilePath,
        state.PortBindingsFilePath);
    std::string injectError = wrote ? "" : "WritePidConfig failed";
    const bool injected = wrote && InjectDll(pid, state.DllPath, &injectError);
    if (injected)
    {
        state.Seen.insert(pid);
        state.RetryAfter.erase(pid);
    }
    else
    {
        state.RetryAfter[pid] = now + 1000;
    }

    std::string line =
        std::string(injected ? "INJECTED " : "FAILED ") +
        std::to_string(pid) +
        " bindIp=" + match->BindIp +
        " connectIp=" + match->ConnectIp +
        " contextId=" + match->ContextId +
        " portOffset=" + match->PortOffset +
        " cwd=" + ToUtf8(strings.CurrentDirectory) +
        " image=" + ToUtf8(strings.ImagePath);
    if (!injected && !injectError.empty())
    {
        line += " injectError=" + injectError;
    }

    WriteLog(state, line);
    LeaveCriticalSection(&state.Lock);
}

static void ScanProcessesOnce(WatcherState &state, bool cleanupMissing)
{
    const ULONGLONG now = GetTickCount64();
    HANDLE snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
    if (snapshot == INVALID_HANDLE_VALUE)
    {
        return;
    }

    PROCESSENTRY32W entry{};
    entry.dwSize = sizeof(entry);
    std::unordered_set<DWORD> present;
    if (Process32FirstW(snapshot, &entry))
    {
        do
        {
            const DWORD pid = entry.th32ProcessID;
            present.insert(pid);
            ProcessCandidatePid(state, pid, now);
        } while (Process32NextW(snapshot, &entry));
    }

    CloseHandle(snapshot);
    if (!cleanupMissing)
    {
        return;
    }

    EnterCriticalSection(&state.Lock);
    for (auto retry = state.RetryAfter.begin(); retry != state.RetryAfter.end();)
    {
        if (present.find(retry->first) == present.end())
        {
            retry = state.RetryAfter.erase(retry);
        }
        else
        {
            ++retry;
        }
    }
    LeaveCriticalSection(&state.Lock);
}

static WatcherState *g_eventState = nullptr;

static bool IsProcessIdPropertyName(const wchar_t *name)
{
    return name &&
        (_wcsicmp(name, L"ProcessID") == 0 ||
         _wcsicmp(name, L"ProcessId") == 0 ||
         _wcsicmp(name, L"PID") == 0);
}

static bool TryReadUInt32Property(PEVENT_RECORD eventRecord, const wchar_t *propertyName, DWORD *value)
{
    if (!eventRecord || !propertyName || !value)
    {
        return false;
    }

    *value = 0;
    PROPERTY_DATA_DESCRIPTOR descriptor{};
    descriptor.PropertyName = reinterpret_cast<ULONGLONG>(propertyName);
    descriptor.ArrayIndex = static_cast<ULONG>(-1);

    ULONG propertySize = 0;
    ULONG status = TdhGetPropertySize(eventRecord, 0, nullptr, 1, &descriptor, &propertySize);
    if (status != ERROR_SUCCESS || propertySize == 0 || propertySize > sizeof(ULONGLONG))
    {
        return false;
    }

    ULONGLONG rawValue = 0;
    status = TdhGetProperty(eventRecord, 0, nullptr, 1, &descriptor, propertySize, reinterpret_cast<PBYTE>(&rawValue));
    if (status != ERROR_SUCCESS || rawValue == 0 || rawValue > MAXDWORD)
    {
        return false;
    }

    *value = static_cast<DWORD>(rawValue);
    return true;
}

static bool TryReadProcessStartPid(PEVENT_RECORD eventRecord, DWORD *pid)
{
    if (!eventRecord || !pid)
    {
        return false;
    }

    *pid = 0;
    const auto descriptor = eventRecord->EventHeader.EventDescriptor;
    if (descriptor.Opcode != DevwtEventOpcodeStart ||
        descriptor.Task != DevwtProcessStartTask)
    {
        return false;
    }

    ULONG eventInfoSize = 0;
    ULONG status = TdhGetEventInformation(eventRecord, 0, nullptr, nullptr, &eventInfoSize);
    if (status != ERROR_INSUFFICIENT_BUFFER || eventInfoSize == 0)
    {
        return false;
    }

    std::vector<BYTE> eventInfoBuffer(eventInfoSize);
    auto *eventInfo = reinterpret_cast<PTRACE_EVENT_INFO>(eventInfoBuffer.data());
    status = TdhGetEventInformation(eventRecord, 0, nullptr, eventInfo, &eventInfoSize);
    if (status != ERROR_SUCCESS)
    {
        return false;
    }

    for (ULONG index = 0; index < eventInfo->TopLevelPropertyCount; index++)
    {
        const auto &property = eventInfo->EventPropertyInfoArray[index];
        if (property.NameOffset == 0 || property.NameOffset >= eventInfoSize)
        {
            continue;
        }

        const auto *name = reinterpret_cast<const wchar_t *>(eventInfoBuffer.data() + property.NameOffset);
        if (IsProcessIdPropertyName(name) && TryReadUInt32Property(eventRecord, name, pid))
        {
            return true;
        }
    }

    return false;
}

static VOID WINAPI ProcessEventRecordCallback(PEVENT_RECORD eventRecord)
{
    if (!eventRecord || !g_eventState)
    {
        return;
    }

    DWORD pid = 0;
    if (TryReadProcessStartPid(eventRecord, &pid))
    {
        InterlockedIncrement(&g_eventState->ProcessStartEventsSeen);
        ProcessCandidatePid(*g_eventState, pid, GetTickCount64());
    }
}

static bool StartProcessEventProbe()
{
    wchar_t systemRoot[MAX_PATH]{};
    DWORD rootLength = GetEnvironmentVariableW(L"SystemRoot", systemRoot, MAX_PATH);
    if (rootLength == 0 || rootLength >= MAX_PATH)
    {
        return false;
    }

    std::wstring commandPath = std::wstring(systemRoot) + L"\\System32\\cmd.exe";
    std::wstring commandLine = L"\"" + commandPath + L"\" /d /c exit /b 0";
    STARTUPINFOW startup{};
    PROCESS_INFORMATION process{};
    startup.cb = sizeof(startup);
    startup.dwFlags = STARTF_USESHOWWINDOW;
    startup.wShowWindow = SW_HIDE;

    const BOOL ok = CreateProcessW(
        commandPath.c_str(),
        commandLine.data(),
        nullptr,
        nullptr,
        FALSE,
        CREATE_NO_WINDOW,
        nullptr,
        nullptr,
        &startup,
        &process);
    if (!ok)
    {
        return false;
    }

    CloseHandle(process.hThread);
    CloseHandle(process.hProcess);
    return true;
}

static bool WaitForProcessStartEventProbe(WatcherState &state, DWORD timeoutMs)
{
    const LONG initialCount = InterlockedCompareExchange(&state.ProcessStartEventsSeen, 0, 0);
    StartProcessEventProbe();

    const ULONGLONG deadline = GetTickCount64() + timeoutMs;
    while (GetTickCount64() < deadline)
    {
        if (InterlockedCompareExchange(&state.ProcessStartEventsSeen, 0, 0) > initialCount)
        {
            return true;
        }

        Sleep(50);
    }

    return false;
}

struct EtwTraceThread
{
    TRACEHANDLE TraceHandle = INVALID_PROCESSTRACE_HANDLE;
};

static DWORD WINAPI ProcessTraceThreadProc(LPVOID parameter)
{
    auto *thread = reinterpret_cast<EtwTraceThread *>(parameter);
    if (!thread || thread->TraceHandle == INVALID_PROCESSTRACE_HANDLE)
    {
        return 1;
    }

    TRACEHANDLE trace = thread->TraceHandle;
    const ULONG status = ProcessTrace(&trace, 1, nullptr, nullptr);
    return status == ERROR_SUCCESS || status == ERROR_CANCELLED ? 0 : status;
}

struct EtwPropertiesBuffer
{
    EVENT_TRACE_PROPERTIES Properties{};
    wchar_t LoggerName[128]{};
};

static bool RunProcessEventWatcher(WatcherState &state, DWORD durationMs, bool runForever)
{
    EtwPropertiesBuffer properties{};
    const DWORD processId = GetCurrentProcessId();
    const ULONGLONG tick = GetTickCount64();
    _snwprintf_s(properties.LoggerName, _TRUNCATE, L"DevWTProcessWatcher-%lu-%llu", processId, tick);
    GUID sessionGuid =
    {
        0x6018650d ^ processId,
        static_cast<USHORT>(0x7047 ^ (tick & 0xffff)),
        static_cast<USHORT>(0x4d1a ^ ((tick >> 16) & 0xffff)),
        { 0xb7, 0x4c, 0x75, 0xd6, 0xe2, 0xb7, 0x73, 0x27 }
    };
    properties.Properties.Wnode.BufferSize = sizeof(properties);
    properties.Properties.Wnode.Flags = WNODE_FLAG_TRACED_GUID;
    properties.Properties.Wnode.Guid = sessionGuid;
    properties.Properties.Wnode.ClientContext = 1;
    properties.Properties.LogFileMode = EVENT_TRACE_REAL_TIME_MODE | EVENT_TRACE_SYSTEM_LOGGER_MODE;
    properties.Properties.EnableFlags = EVENT_TRACE_FLAG_PROCESS;
    properties.Properties.LoggerNameOffset = offsetof(EtwPropertiesBuffer, LoggerName);

    TRACEHANDLE sessionHandle = 0;
    ULONG status = StartTraceW(&sessionHandle, properties.LoggerName, &properties.Properties);
    if (status != ERROR_SUCCESS)
    {
        WriteLog(state, "PROCESS_EVENTS_UNAVAILABLE StartTrace status=" + std::to_string(status));
        return false;
    }

    status = EnableTraceEx2(
        sessionHandle,
        &DevwtKernelProcessProviderGuid,
        EVENT_CONTROL_CODE_ENABLE_PROVIDER,
        TRACE_LEVEL_INFORMATION,
        DevwtKernelAnalyticKeyword | DevwtKernelProcessKeyword,
        0,
        0,
        nullptr);
    if (status != ERROR_SUCCESS)
    {
        WriteLog(state, "PROCESS_EVENTS_UNAVAILABLE EnableTraceEx2 status=" + std::to_string(status));
        ControlTraceW(sessionHandle, properties.LoggerName, &properties.Properties, EVENT_TRACE_CONTROL_STOP);
        return false;
    }

    EVENT_TRACE_LOGFILEW logFile{};
    logFile.LoggerName = properties.LoggerName;
    logFile.ProcessTraceMode = PROCESS_TRACE_MODE_REAL_TIME | PROCESS_TRACE_MODE_EVENT_RECORD;
    logFile.LogFileMode = EVENT_TRACE_REAL_TIME_MODE | EVENT_TRACE_SYSTEM_LOGGER_MODE;
    logFile.EventRecordCallback = ProcessEventRecordCallback;
    TRACEHANDLE traceHandle = OpenTraceW(&logFile);
    if (traceHandle == INVALID_PROCESSTRACE_HANDLE)
    {
        const DWORD error = GetLastError();
        WriteLog(state, "PROCESS_EVENTS_UNAVAILABLE OpenTrace status=" + std::to_string(error));
        ControlTraceW(sessionHandle, properties.LoggerName, &properties.Properties, EVENT_TRACE_CONTROL_STOP);
        return false;
    }

    EtwTraceThread threadState{ traceHandle };
    g_eventState = &state;
    HANDLE thread = CreateThread(nullptr, 0, ProcessTraceThreadProc, &threadState, 0, nullptr);
    if (!thread)
    {
        const DWORD error = GetLastError();
        g_eventState = nullptr;
        CloseTrace(traceHandle);
        ControlTraceW(sessionHandle, properties.LoggerName, &properties.Properties, EVENT_TRACE_CONTROL_STOP);
        WriteLog(state, "PROCESS_EVENTS_UNAVAILABLE CreateThread status=" + std::to_string(error));
        return false;
    }

    if (!WaitForProcessStartEventProbe(state, 2000))
    {
        g_eventState = nullptr;
        CloseTrace(traceHandle);
        ControlTraceW(sessionHandle, properties.LoggerName, &properties.Properties, EVENT_TRACE_CONTROL_STOP);
        WaitForSingleObject(thread, 5000);
        CloseHandle(thread);
        WriteLog(state, "PROCESS_EVENTS_UNAVAILABLE NoProcessStartEvents");
        return false;
    }

    WriteLog(state, "PROCESS_EVENTS_STARTED");
    if (runForever)
    {
        WaitForSingleObject(thread, INFINITE);
    }
    else
    {
        WaitForSingleObject(thread, durationMs);
    }

    g_eventState = nullptr;
    CloseTrace(traceHandle);
    ControlTraceW(sessionHandle, properties.LoggerName, &properties.Properties, EVENT_TRACE_CONTROL_STOP);
    WaitForSingleObject(thread, 5000);
    CloseHandle(thread);
    return true;
}

int main(int argc, char **argv)
{
    std::wstring dllPath;
    std::wstring logPath;
    std::wstring mapFilePath;
    std::wstring portBindingsFilePath;
    DWORD targetPid = 0;
    DWORD childrenOnlyTargetPid = 0;
    std::string directContextId;
    std::string directBindIp;
    std::string directConnectIp;
    std::string directPortOffset;
    DWORD pollMs = 250;
    DWORD durationMs = 30000;
    bool processEvents = false;
    std::vector<FolderMap> maps;
    std::vector<ChildrenOnlyImage> childrenOnlyImages;
    std::vector<ChildrenOnlyPackageFamily> childrenOnlyPackageFamilies;

    for (int index = 1; index < argc; index++)
    {
        const std::string option = argv[index];
        if (option == "--dll" && index + 1 < argc)
        {
            dllPath = FullPath(ToWide(argv[++index]));
        }
        else if (option == "--map" && index + 1 < argc)
        {
            const std::string value = argv[++index];
            const auto separator = value.find('=');
            if (separator == std::string::npos)
            {
                std::cerr << "--map requires folder=ip\n";
                return 2;
            }

            const auto mapValue = value.substr(separator + 1);
            std::vector<std::string> parts;
            size_t start = 0;
            while (start <= mapValue.size())
            {
                const auto comma = mapValue.find(',', start);
                parts.push_back(mapValue.substr(start, comma == std::string::npos ? std::string::npos : comma - start));
                if (comma == std::string::npos)
                {
                    break;
                }

                start = comma + 1;
            }

            const bool hasPortShiftFields = parts.size() >= 4;
            const auto contextId = hasPortShiftFields ? parts[0] : "";
            const auto bindIp = hasPortShiftFields ? parts[1] : parts[0];
            const auto connectIp = hasPortShiftFields
                ? parts[2]
                : (parts.size() >= 2 ? parts[1] : bindIp);
            const auto portOffset = hasPortShiftFields ? parts[3] : "";
            maps.push_back({
                EnsureTrailingSlash(FullPath(ToWide(value.substr(0, separator)))),
                contextId,
                bindIp,
                connectIp,
                portOffset
            });
        }
        else if (option == "--children-only-image" && index + 1 < argc)
        {
            childrenOnlyImages.push_back({ FullPath(ToWide(argv[++index])) });
        }
        else if (option == "--children-only-package-family" && index + 1 < argc)
        {
            childrenOnlyPackageFamilies.push_back({ ToWide(argv[++index]) });
        }
        else if (option == "--pid" && index + 1 < argc)
        {
            targetPid = static_cast<DWORD>(std::stoul(argv[++index]));
        }
        else if (option == "--children-only-pid" && index + 1 < argc)
        {
            childrenOnlyTargetPid = static_cast<DWORD>(std::stoul(argv[++index]));
        }
        else if (option == "--bind-ip" && index + 1 < argc)
        {
            directBindIp = argv[++index];
        }
        else if (option == "--connect-ip" && index + 1 < argc)
        {
            directConnectIp = argv[++index];
        }
        else if (option == "--context-id" && index + 1 < argc)
        {
            directContextId = argv[++index];
        }
        else if (option == "--port-offset" && index + 1 < argc)
        {
            directPortOffset = argv[++index];
        }
        else if (option == "--poll-ms" && index + 1 < argc)
        {
            pollMs = static_cast<DWORD>(std::stoul(argv[++index]));
        }
        else if (option == "--process-events")
        {
            processEvents = true;
        }
        else if (option == "--duration-ms" && index + 1 < argc)
        {
            durationMs = static_cast<DWORD>(std::stoul(argv[++index]));
        }
        else if (option == "--log" && index + 1 < argc)
        {
            logPath = FullPath(ToWide(argv[++index]));
        }
        else if (option == "--map-file" && index + 1 < argc)
        {
            mapFilePath = FullPath(ToWide(argv[++index]));
        }
        else if (option == "--port-bindings-file" && index + 1 < argc)
        {
            portBindingsFilePath = FullPath(ToWide(argv[++index]));
        }
        else
        {
            std::cerr << "usage: devwt-folder-watcher --dll <path> (--map <folder=bindIp[,connectIp]>... | --children-only-image <ide.exe>... | --children-only-package-family <pfn>... | --pid <pid> --bind-ip <ip> [--connect-ip <ip>] | --children-only-pid <pid>) [--map-file path] [--process-events] [--poll-ms n] [--duration-ms n] [--log path]\n";
            return 2;
        }
    }

    if (dllPath.empty()
        || (maps.empty()
            && childrenOnlyImages.empty()
            && childrenOnlyPackageFamilies.empty()
            && targetPid == 0
            && childrenOnlyTargetPid == 0)
        || (targetPid != 0 && childrenOnlyTargetPid != 0))
    {
        std::cerr << "usage: devwt-folder-watcher --dll <path> (--map <folder=bindIp[,connectIp]>... | --children-only-image <ide.exe>... | --children-only-package-family <pfn>... | --pid <pid> --bind-ip <ip> [--connect-ip <ip>] | --children-only-pid <pid>) [--map-file path] [--process-events] [--poll-ms n] [--duration-ms n] [--log path]\n";
        return 2;
    }

    if (childrenOnlyTargetPid != 0)
    {
        const bool wrote = WriteChildrenOnlyPidConfig(
            childrenOnlyTargetPid,
            mapFilePath,
            portBindingsFilePath);
        std::string injectError = wrote ? "" : "WriteChildrenOnlyPidConfig failed";
        const bool injected = wrote && InjectDll(
            childrenOnlyTargetPid,
            dllPath,
            &injectError);
        std::cout << (injected ? "INJECTED_CHILDREN_ONLY " : "FAILED_CHILDREN_ONLY ")
                  << childrenOnlyTargetPid;
        if (!injectError.empty())
        {
            std::cout << " injectError=" << injectError;
        }

        std::cout << "\n";
        return injected ? 0 : 1;
    }

    if (targetPid != 0)
    {
        if (directBindIp.empty())
        {
            std::cerr << "--pid mode requires --bind-ip <ip>\n";
            return 2;
        }

        if (directConnectIp.empty())
        {
            directConnectIp = directBindIp;
        }

        const bool wrote = WritePidConfig(
            targetPid,
            directContextId,
            directBindIp,
            directConnectIp,
            directPortOffset,
            mapFilePath,
            portBindingsFilePath);
        const bool injected = wrote && InjectDll(targetPid, dllPath);
        std::cout << (injected ? "INJECTED " : "FAILED ")
                  << targetPid
                  << " bindIp=" << directBindIp
                  << " connectIp=" << directConnectIp
                  << " contextId=" << directContextId
                  << " portOffset=" << directPortOffset
                  << "\n";
        return injected ? 0 : 1;
    }

    std::ofstream log;
    if (!logPath.empty())
    {
        log.open(logPath, std::ios::trunc);
    }

    WatcherState state;
    state.DllPath = dllPath;
    state.MapFilePath = mapFilePath;
    state.PortBindingsFilePath = portBindingsFilePath;
    state.Maps = &maps;
    state.ChildrenOnlyImages = &childrenOnlyImages;
    state.ChildrenOnlyPackageFamilies = &childrenOnlyPackageFamilies;
    state.Log = &log;
    InitializeCriticalSection(&state.Lock);

    const bool runForever = durationMs == 0;
    const ULONGLONG deadline = GetTickCount64() + durationMs;
    ScanProcessesOnce(state, false);

    if (processEvents && RunProcessEventWatcher(state, durationMs, runForever))
    {
        DeleteCriticalSection(&state.Lock);
        return 0;
    }

    if (processEvents && pollMs < 1000)
    {
        pollMs = 1000;
    }
    if (processEvents)
    {
        WriteLog(state, "PROCESS_EVENTS_FALLBACK_POLL_MS " + std::to_string(pollMs));
    }

    while (runForever || GetTickCount64() < deadline)
    {
        ScanProcessesOnce(state, true);
        Sleep(pollMs);
    }

    DeleteCriticalSection(&state.Lock);
    return 0;
}
