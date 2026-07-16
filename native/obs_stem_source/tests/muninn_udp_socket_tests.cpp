#include "muninn_udp_socket.h"

#include <cassert>
#include <windows.h>

int main()
{
    WSADATA data{};
    assert(WSAStartup(MAKEWORD(2, 2), &data) == 0);

    const SOCKET owner = muninn_udp_socket::create(true);
    assert(owner != INVALID_SOCKET);
    DWORD flags = HANDLE_FLAG_INHERIT;
    assert(GetHandleInformation(reinterpret_cast<HANDLE>(owner), &flags));
    assert((flags & HANDLE_FLAG_INHERIT) == 0);

    sockaddr_in address{};
    address.sin_family = AF_INET;
    address.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
    address.sin_port = 0;
    assert(bind(owner, reinterpret_cast<sockaddr *>(&address), sizeof(address)) == 0);

    int address_size = sizeof(address);
    assert(getsockname(owner, reinterpret_cast<sockaddr *>(&address), &address_size) == 0);
    const SOCKET contender = muninn_udp_socket::create(true);
    assert(contender != INVALID_SOCKET);
    assert(bind(contender, reinterpret_cast<sockaddr *>(&address), sizeof(address)) != 0);
    assert(WSAGetLastError() == WSAEADDRINUSE || WSAGetLastError() == WSAEACCES);

    closesocket(contender);
    closesocket(owner);
    WSACleanup();
    return 0;
}
