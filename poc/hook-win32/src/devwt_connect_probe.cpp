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

int main(int argc, char **argv)
{
    const int port = ParseIntArg(argc, argv, "--port", 55293);
    const std::string label = ParseStringArg(argc, argv, "--label", "client");

    WSADATA data{};
    if (WSAStartup(MAKEWORD(2, 2), &data) != 0)
    {
        std::cerr << "WSAStartup failed\n";
        return 10;
    }

    SOCKET socketHandle = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
    if (socketHandle == INVALID_SOCKET)
    {
        std::cerr << "socket failed: " << WSAGetLastError() << "\n";
        WSACleanup();
        return 11;
    }

    sockaddr_in address{};
    address.sin_family = AF_INET;
    address.sin_port = htons(static_cast<u_short>(port));
    InetPtonA(AF_INET, "127.0.0.1", &address.sin_addr);

    if (connect(socketHandle, reinterpret_cast<const sockaddr *>(&address), sizeof(address)) != 0)
    {
        std::cerr << "connect failed: " << WSAGetLastError() << "\n";
        closesocket(socketHandle);
        WSACleanup();
        return 40;
    }

    sockaddr_in peer{};
    int peerLength = sizeof(peer);
    if (getpeername(socketHandle, reinterpret_cast<sockaddr *>(&peer), &peerLength) != 0)
    {
        std::cerr << "getpeername failed: " << WSAGetLastError() << "\n";
        closesocket(socketHandle);
        WSACleanup();
        return 41;
    }

    char peerIp[64]{};
    InetNtopA(AF_INET, &peer.sin_addr, peerIp, static_cast<DWORD>(sizeof(peerIp)));
    std::cout << "CONNECTED " << label << " " << peerIp << ":" << ntohs(peer.sin_port) << " pid=" << GetCurrentProcessId() << std::endl;

    closesocket(socketHandle);
    WSACleanup();
    return 0;
}
