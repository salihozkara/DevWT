#define WIN32_LEAN_AND_MEAN
#include <windows.h>

#include <iostream>
#include <string>
#include <vector>

static std::wstring QuoteArg(const std::wstring &arg)
{
    if (arg.empty())
    {
        return L"\"\"";
    }

    bool needsQuotes = false;
    for (wchar_t ch : arg)
    {
        if (ch == L' ' || ch == L'\t' || ch == L'"')
        {
            needsQuotes = true;
            break;
        }
    }

    if (!needsQuotes)
    {
        return arg;
    }

    std::wstring result = L"\"";
    size_t backslashes = 0;
    for (wchar_t ch : arg)
    {
        if (ch == L'\\')
        {
            backslashes++;
            continue;
        }

        if (ch == L'"')
        {
            result.append(backslashes * 2 + 1, L'\\');
            result.push_back(ch);
            backslashes = 0;
            continue;
        }

        result.append(backslashes, L'\\');
        backslashes = 0;
        result.push_back(ch);
    }

    result.append(backslashes * 2, L'\\');
    result.push_back(L'"');
    return result;
}

static std::wstring BuildCommandLine(const std::vector<std::wstring> &args)
{
    std::wstring commandLine;
    for (const auto &arg : args)
    {
        if (!commandLine.empty())
        {
            commandLine.push_back(L' ');
        }

        commandLine.append(QuoteArg(arg));
    }

    return commandLine;
}

static std::wstring GetDefaultDllPath()
{
    wchar_t modulePath[MAX_PATH]{};
    GetModuleFileNameW(nullptr, modulePath, MAX_PATH);
    std::wstring path(modulePath);
    const auto slash = path.find_last_of(L"\\/");
    if (slash != std::wstring::npos)
    {
        path.resize(slash + 1);
    }
    else
    {
        path.clear();
    }

    path.append(L"devwt-hook.dll");
    return path;
}

static bool InjectDll(HANDLE process, const std::wstring &dllPath)
{
    const size_t byteCount = (dllPath.size() + 1) * sizeof(wchar_t);
    void *remote = VirtualAllocEx(process, nullptr, byteCount, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    if (!remote)
    {
        std::cerr << "VirtualAllocEx failed: " << GetLastError() << "\n";
        return false;
    }

    SIZE_T written = 0;
    if (!WriteProcessMemory(process, remote, dllPath.c_str(), byteCount, &written) || written != byteCount)
    {
        std::cerr << "WriteProcessMemory failed: " << GetLastError() << "\n";
        VirtualFreeEx(process, remote, 0, MEM_RELEASE);
        return false;
    }

    auto loadLibrary = reinterpret_cast<LPTHREAD_START_ROUTINE>(GetProcAddress(GetModuleHandleW(L"kernel32.dll"), "LoadLibraryW"));
    HANDLE thread = CreateRemoteThread(process, nullptr, 0, loadLibrary, remote, 0, nullptr);
    if (!thread)
    {
        std::cerr << "CreateRemoteThread failed: " << GetLastError() << "\n";
        VirtualFreeEx(process, remote, 0, MEM_RELEASE);
        return false;
    }

    WaitForSingleObject(thread, INFINITE);
    DWORD loadResult = 0;
    GetExitCodeThread(thread, &loadResult);
    CloseHandle(thread);
    VirtualFreeEx(process, remote, 0, MEM_RELEASE);

    if (loadResult == 0)
    {
        std::cerr << "Remote LoadLibraryW failed.\n";
        return false;
    }

    return true;
}

static HANDLE CreateKillOnCloseJob()
{
    HANDLE job = CreateJobObjectW(nullptr, nullptr);
    if (!job)
    {
        return nullptr;
    }

    JOBOBJECT_EXTENDED_LIMIT_INFORMATION limits{};
    limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
    if (!SetInformationJobObject(
            job,
            JobObjectExtendedLimitInformation,
            &limits,
            sizeof(limits)))
    {
        CloseHandle(job);
        return nullptr;
    }

    return job;
}

int wmain(int argc, wchar_t **argv)
{
    std::wstring bindIp;
    std::wstring connectIp;
    std::wstring contextId;
    std::wstring portOffset;
    std::wstring portBindingsFile;
    std::wstring dllPath = GetDefaultDllPath();
    std::vector<std::wstring> targetArgs;
    bool childrenOnly = false;

    for (int index = 1; index < argc; index++)
    {
        const std::wstring option = argv[index];
        if (option == L"--children-only")
        {
            childrenOnly = true;
        }
        else if (option == L"--bind-ip" && index + 1 < argc)
        {
            bindIp = argv[++index];
        }
        else if (option == L"--connect-ip" && index + 1 < argc)
        {
            connectIp = argv[++index];
        }
        else if (option == L"--context-id" && index + 1 < argc)
        {
            contextId = argv[++index];
        }
        else if (option == L"--port-offset" && index + 1 < argc)
        {
            portOffset = argv[++index];
        }
        else if (option == L"--port-bindings-file" && index + 1 < argc)
        {
            portBindingsFile = argv[++index];
        }
        else if (option == L"--dll" && index + 1 < argc)
        {
            dllPath = argv[++index];
        }
        else if (option == L"--")
        {
            for (int argIndex = index + 1; argIndex < argc; argIndex++)
            {
                targetArgs.emplace_back(argv[argIndex]);
            }
            break;
        }
        else
        {
            std::wcerr << L"Unknown or incomplete option: " << option << L"\n";
            return 2;
        }
    }

    if ((!childrenOnly && bindIp.empty()) || targetArgs.empty())
    {
        std::cerr << "usage: devwt-hook-launcher [--children-only] --context-id <id> --bind-ip <ip> [--connect-ip <ip>] [--port-offset n] [--port-bindings-file path] [--dll <path>] -- <program> [args...]\n";
        return 2;
    }

    if (GetFileAttributesW(dllPath.c_str()) == INVALID_FILE_ATTRIBUTES)
    {
        std::wcerr << L"Hook DLL not found: " << dllPath << L"\n";
        return 2;
    }

    if (childrenOnly)
    {
        SetEnvironmentVariableW(L"DEVWT_HOOK_CHILDREN_ONLY", L"1");
    }

    if (!bindIp.empty())
    {
        SetEnvironmentVariableW(L"DEVWT_HOOK_BIND_IP", bindIp.c_str());
    }

    if (!connectIp.empty())
    {
        SetEnvironmentVariableW(L"DEVWT_HOOK_CONNECT_IP", connectIp.c_str());
    }

    if (!contextId.empty())
    {
        SetEnvironmentVariableW(L"DEVWT_HOOK_CONTEXT_ID", contextId.c_str());
    }

    if (!portOffset.empty())
    {
        SetEnvironmentVariableW(L"DEVWT_HOOK_PORT_OFFSET", portOffset.c_str());
    }

    if (!portBindingsFile.empty())
    {
        SetEnvironmentVariableW(L"DEVWT_HOOK_BINDINGS_FILE", portBindingsFile.c_str());
    }

    HANDLE job = CreateKillOnCloseJob();
    if (!job)
    {
        std::cerr << "CreateJobObject kill-on-close failed: " << GetLastError() << "\n";
        return 1;
    }

    std::wstring commandLine = BuildCommandLine(targetArgs);
    STARTUPINFOW startup{};
    PROCESS_INFORMATION process{};
    startup.cb = sizeof(startup);

    DWORD creationFlags = CREATE_SUSPENDED | CREATE_BREAKAWAY_FROM_JOB;
    if (!CreateProcessW(
            nullptr,
            commandLine.data(),
            nullptr,
            nullptr,
            TRUE,
            creationFlags,
            nullptr,
            nullptr,
            &startup,
            &process))
    {
        const DWORD breakawayError = GetLastError();
        creationFlags = CREATE_SUSPENDED;
        if (!CreateProcessW(
                nullptr,
                commandLine.data(),
                nullptr,
                nullptr,
                TRUE,
                creationFlags,
                nullptr,
                nullptr,
                &startup,
                &process))
        {
            std::cerr << "CreateProcessW failed: " << GetLastError() << " (breakaway attempt failed: " << breakawayError << ")\n";
            CloseHandle(job);
            return 1;
        }
    }

    if (job && !AssignProcessToJobObject(job, process.hProcess))
    {
        std::cerr << "Could not assign target process to kill-on-close job: " << GetLastError() << "\n";
        TerminateProcess(process.hProcess, 101);
        CloseHandle(process.hThread);
        CloseHandle(process.hProcess);
        CloseHandle(job);
        return 1;
    }

    if (!InjectDll(process.hProcess, dllPath))
    {
        TerminateProcess(process.hProcess, 100);
        CloseHandle(process.hThread);
        CloseHandle(process.hProcess);
        if (job)
        {
            CloseHandle(job);
        }

        return 1;
    }

    ResumeThread(process.hThread);
    WaitForSingleObject(process.hProcess, INFINITE);

    DWORD exitCode = 0;
    GetExitCodeProcess(process.hProcess, &exitCode);
    CloseHandle(process.hThread);
    CloseHandle(process.hProcess);
    if (job)
    {
        CloseHandle(job);
    }

    return static_cast<int>(exitCode);
}
