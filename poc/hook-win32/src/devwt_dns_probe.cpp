#include <winsock2.h>
#include <windows.h>

#include <iostream>
#include <string>

static std::wstring ModulePathForAddress(FARPROC address)
{
    if (!address)
    {
        return L"";
    }

    HMODULE module = nullptr;
    if (!GetModuleHandleExW(
            GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
            reinterpret_cast<LPCWSTR>(address),
            &module))
    {
        return L"";
    }

    std::wstring path(32768, L'\0');
    const DWORD length = GetModuleFileNameW(module, path.data(), static_cast<DWORD>(path.size()));
    if (length == 0 || length >= path.size())
    {
        return L"";
    }

    path.resize(length);
    return path;
}

static std::wstring FileNameOf(const std::wstring &path)
{
    const auto slash = path.find_last_of(L"\\/");
    return slash == std::wstring::npos ? path : path.substr(slash + 1);
}

int wmain()
{
    WSADATA winsock{};
    if (WSAStartup(MAKEWORD(2, 2), &winsock) != 0)
    {
        std::cerr << "WSAStartup failed\n";
        return 2;
    }

    const auto ws2 = GetModuleHandleW(L"ws2_32.dll");
    const auto getAddrInfoA = ws2 ? GetProcAddress(ws2, "getaddrinfo") : nullptr;
    const auto getAddrInfoW = ws2 ? GetProcAddress(ws2, "GetAddrInfoW") : nullptr;
    const auto ownerA = FileNameOf(ModulePathForAddress(getAddrInfoA));
    const auto ownerW = FileNameOf(ModulePathForAddress(getAddrInfoW));

    std::wcout << L"RESOLVER getaddrinfo owner=" << ownerA << L"\n";
    std::wcout << L"RESOLVER GetAddrInfoW owner=" << ownerW << L"\n";

    WSACleanup();
    return _wcsicmp(ownerA.c_str(), L"ws2_32.dll") == 0
            && _wcsicmp(ownerW.c_str(), L"ws2_32.dll") == 0
        ? 0
        : 1;
}
