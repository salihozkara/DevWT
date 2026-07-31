#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <winsock2.h>
#include <ws2tcpip.h>

#include <iostream>
#include <string>

static int ParseIntArg(int argc, char **argv, const char *name, int fallback)
{
    for (int index = 1; index + 1 < argc; index++)
    {
        if (std::string(argv[index]) == name)
        {
            return std::stoi(argv[index + 1]);
        }
    }

    return fallback;
}

static std::string ParseStringArg(int argc, char **argv, const char *name, const std::string &fallback)
{
    for (int index = 1; index + 1 < argc; index++)
    {
        if (std::string(argv[index]) == name)
        {
            return argv[index + 1];
        }
    }

    return fallback;
}

static bool HasFlag(int argc, char **argv, const char *name)
{
    for (int index = 1; index < argc; index++)
    {
        if (std::string(argv[index]) == name)
        {
            return true;
        }
    }

    return false;
}

int main(int argc, char **argv)
{
    const int port = ParseIntArg(argc, argv, "--port", 55251);
    const int holdMs = ParseIntArg(argc, argv, "--hold-ms", 3000);
    const int startupDelayMs = ParseIntArg(argc, argv, "--startup-delay-ms", 0);
    const std::string label = ParseStringArg(argc, argv, "--label", "probe");
    const std::string bindIp = ParseStringArg(argc, argv, "--bind-ip", "127.0.0.1");
    const bool udp = HasFlag(argc, argv, "--udp");
    const bool reuseAddress = HasFlag(argc, argv, "--reuse-address");
    const bool exclusiveAddressUse = HasFlag(argc, argv, "--exclusive-address-use");

    if (reuseAddress && exclusiveAddressUse)
    {
        std::cerr << "--reuse-address and --exclusive-address-use cannot be combined\n";
        return 2;
    }

    if (startupDelayMs > 0)
    {
        Sleep(static_cast<DWORD>(startupDelayMs));
    }

    WSADATA data{};
    if (WSAStartup(MAKEWORD(2, 2), &data) != 0)
    {
        std::cerr << "WSAStartup failed\n";
        return 10;
    }

    const int addressFamily = bindIp.find(':') == std::string::npos ? AF_INET : AF_INET6;
    SOCKET socketHandle = socket(addressFamily, udp ? SOCK_DGRAM : SOCK_STREAM, udp ? IPPROTO_UDP : IPPROTO_TCP);
    if (socketHandle == INVALID_SOCKET)
    {
        std::cerr << "socket failed: " << WSAGetLastError() << "\n";
        WSACleanup();
        return 11;
    }

    if (reuseAddress)
    {
        BOOL enabled = TRUE;
        if (setsockopt(
                socketHandle,
                SOL_SOCKET,
                SO_REUSEADDR,
                reinterpret_cast<const char *>(&enabled),
                sizeof(enabled)) != 0)
        {
            std::cerr << "setsockopt SO_REUSEADDR failed: " << WSAGetLastError() << "\n";
            closesocket(socketHandle);
            WSACleanup();
            return 12;
        }
    }

    if (exclusiveAddressUse)
    {
        BOOL enabled = TRUE;
        if (setsockopt(
                socketHandle,
                SOL_SOCKET,
                SO_EXCLUSIVEADDRUSE,
                reinterpret_cast<const char *>(&enabled),
                sizeof(enabled)) != 0)
        {
            std::cerr << "setsockopt SO_EXCLUSIVEADDRUSE failed: " << WSAGetLastError() << "\n";
            closesocket(socketHandle);
            WSACleanup();
            return 13;
        }
    }

    sockaddr_storage address{};
    int addressLength = 0;
    if (addressFamily == AF_INET)
    {
        auto *v4 = reinterpret_cast<sockaddr_in *>(&address);
        v4->sin_family = AF_INET;
        v4->sin_port = htons(static_cast<u_short>(port));
        addressLength = sizeof(*v4);
        if (InetPtonA(AF_INET, bindIp.c_str(), &v4->sin_addr) != 1)
        {
            std::cerr << "invalid IPv4 bind address: " << bindIp << "\n";
            closesocket(socketHandle);
            WSACleanup();
            return 14;
        }
    }
    else
    {
        auto *v6 = reinterpret_cast<sockaddr_in6 *>(&address);
        v6->sin6_family = AF_INET6;
        v6->sin6_port = htons(static_cast<u_short>(port));
        addressLength = sizeof(*v6);
        if (InetPtonA(AF_INET6, bindIp.c_str(), &v6->sin6_addr) != 1)
        {
            std::cerr << "invalid IPv6 bind address: " << bindIp << "\n";
            closesocket(socketHandle);
            WSACleanup();
            return 14;
        }
    }

    if (bind(socketHandle, reinterpret_cast<const sockaddr *>(&address), addressLength) != 0)
    {
        std::cerr << "bind failed: " << WSAGetLastError() << "\n";
        closesocket(socketHandle);
        WSACleanup();
        return 40;
    }

    if (!udp && listen(socketHandle, SOMAXCONN) != 0)
    {
        std::cerr << "listen failed: " << WSAGetLastError() << "\n";
        closesocket(socketHandle);
        WSACleanup();
        return 41;
    }

    sockaddr_storage actual{};
    int actualLength = sizeof(actual);
    getsockname(socketHandle, reinterpret_cast<sockaddr *>(&actual), &actualLength);

    char actualIp[64]{};
    u_short actualPort = 0;
    if (actual.ss_family == AF_INET)
    {
        const auto *v4 = reinterpret_cast<const sockaddr_in *>(&actual);
        InetNtopA(AF_INET, const_cast<IN_ADDR *>(&v4->sin_addr), actualIp, static_cast<DWORD>(sizeof(actualIp)));
        actualPort = ntohs(v4->sin_port);
    }
    else
    {
        const auto *v6 = reinterpret_cast<const sockaddr_in6 *>(&actual);
        InetNtopA(AF_INET6, const_cast<IN6_ADDR *>(&v6->sin6_addr), actualIp, static_cast<DWORD>(sizeof(actualIp)));
        actualPort = ntohs(v6->sin6_port);
    }

    std::cout << "BOUND " << label << " " << actualIp << ":" << actualPort << " pid=" << GetCurrentProcessId() << std::endl;

    Sleep(static_cast<DWORD>(holdMs));
    closesocket(socketHandle);
    WSACleanup();
    return 0;
}
