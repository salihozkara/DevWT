#define WIN32_LEAN_AND_MEAN
#include <windows.h>

#include <iostream>
#include <string>
#include <vector>

int wmain(int argc, wchar_t **argv)
{
    const wchar_t *name = argc > 1 ? argv[1] : L"DEVWT_ENV_PROBE";

    DWORD length = GetEnvironmentVariableW(name, nullptr, 0);
    if (length == 0)
    {
        std::wcout << L"ENV " << name << L"=<missing>\n";
        return 3;
    }

    std::vector<wchar_t> value(length);
    const DWORD written = GetEnvironmentVariableW(name, value.data(), length);
    if (written == 0 || written >= length)
    {
        std::wcerr << L"GetEnvironmentVariableW failed: " << GetLastError() << L"\n";
        return 1;
    }

    std::wstring text(value.data(), written);
    std::wcout << L"ENV " << name << L"=" << text << L"\n";
    return 0;
}
