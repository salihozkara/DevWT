#define WIN32_LEAN_AND_MEAN
#include <windows.h>

#include <algorithm>
#include <iostream>
#include <set>
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
    for (wchar_t ch : arg)
    {
        if (ch == L'"')
        {
            result.append(L"\\\"");
        }
        else
        {
            result.push_back(ch);
        }
    }

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

static std::wstring GetEnvironmentName(const std::wstring &entry)
{
    const auto equals = entry.find(L'=');
    if (equals == std::wstring::npos)
    {
        return entry;
    }

    return entry.substr(0, equals);
}

struct CaseInsensitiveWideLess
{
    bool operator()(const std::wstring &left, const std::wstring &right) const
    {
        return _wcsicmp(left.c_str(), right.c_str()) < 0;
    }
};

static std::vector<wchar_t> BuildEnvironmentBlock(const std::vector<std::wstring> &overrides)
{
    if (overrides.empty())
    {
        return {};
    }

    std::set<std::wstring, CaseInsensitiveWideLess> overrideNames;
    for (const auto &entry : overrides)
    {
        overrideNames.insert(GetEnvironmentName(entry));
    }

    std::vector<std::wstring> entries;
    wchar_t *environment = GetEnvironmentStringsW();
    if (environment)
    {
        for (const wchar_t *cursor = environment; *cursor; cursor += wcslen(cursor) + 1)
        {
            const std::wstring entry = cursor;
            const std::wstring name = GetEnvironmentName(entry);
            if (overrideNames.find(name) == overrideNames.end())
            {
                entries.push_back(entry);
            }
        }

        FreeEnvironmentStringsW(environment);
    }

    for (const auto &entry : overrides)
    {
        entries.push_back(entry);
    }

    std::sort(entries.begin(), entries.end(), [](const std::wstring &left, const std::wstring &right)
    {
        return _wcsicmp(GetEnvironmentName(left).c_str(), GetEnvironmentName(right).c_str()) < 0;
    });

    std::vector<wchar_t> block;
    for (const auto &entry : entries)
    {
        block.insert(block.end(), entry.begin(), entry.end());
        block.push_back(L'\0');
    }

    block.push_back(L'\0');
    return block;
}

int wmain(int argc, wchar_t **argv)
{
    if (argc < 2)
    {
        std::cerr << "usage: devwt-child-spawner [--spawn-delay-ms <ms>] [--env <NAME=VALUE>] [--] <program> [args...]\n";
        return 2;
    }

    DWORD spawnDelayMs = 0;
    std::vector<std::wstring> environmentOverrides;
    std::vector<std::wstring> targetArgs;
    int index = 1;
    for (; index < argc; index++)
    {
        const std::wstring option = argv[index];
        if (option == L"--spawn-delay-ms" && index + 1 < argc)
        {
            spawnDelayMs = static_cast<DWORD>(std::stoul(argv[++index]));
        }
        else if (option == L"--env" && index + 1 < argc)
        {
            const std::wstring entry = argv[++index];
            if (entry.find(L'=') == std::wstring::npos || entry.empty() || entry.front() == L'=')
            {
                std::cerr << "--env requires NAME=VALUE\n";
                return 2;
            }

            environmentOverrides.push_back(entry);
        }
        else if (option == L"--")
        {
            index++;
            break;
        }
        else
        {
            break;
        }
    }

    for (; index < argc; index++)
    {
        targetArgs.emplace_back(argv[index]);
    }

    if (targetArgs.empty())
    {
        std::cerr << "usage: devwt-child-spawner [--spawn-delay-ms <ms>] [--env <NAME=VALUE>] [--] <program> [args...]\n";
        return 2;
    }

    if (spawnDelayMs > 0)
    {
        Sleep(spawnDelayMs);
    }

    std::wstring commandLine = BuildCommandLine(targetArgs);
    std::vector<wchar_t> environmentBlock = BuildEnvironmentBlock(environmentOverrides);
    STARTUPINFOW startup{};
    PROCESS_INFORMATION process{};
    startup.cb = sizeof(startup);

    const DWORD creationFlags = environmentBlock.empty() ? 0 : CREATE_UNICODE_ENVIRONMENT;
    void *environment = environmentBlock.empty() ? nullptr : environmentBlock.data();
    if (!CreateProcessW(nullptr, commandLine.data(), nullptr, nullptr, TRUE, creationFlags, environment, nullptr, &startup, &process))
    {
        std::cerr << "CreateProcessW failed: " << GetLastError() << "\n";
        return 1;
    }

    WaitForSingleObject(process.hProcess, INFINITE);
    DWORD exitCode = 0;
    GetExitCodeProcess(process.hProcess, &exitCode);
    CloseHandle(process.hThread);
    CloseHandle(process.hProcess);
    return static_cast<int>(exitCode);
}
