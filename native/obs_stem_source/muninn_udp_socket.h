#pragma once

#ifdef _WIN32
#include <winsock2.h>

namespace muninn_udp_socket {

inline SOCKET create(bool exclusive_listener = false)
{
    const SOCKET socket = WSASocketW(
        AF_INET,
        SOCK_DGRAM,
        IPPROTO_UDP,
        nullptr,
        0,
        WSA_FLAG_NO_HANDLE_INHERIT);
    if (socket == INVALID_SOCKET) {
        return INVALID_SOCKET;
    }
    if (exclusive_listener) {
        const BOOL exclusive = TRUE;
        if (setsockopt(socket, SOL_SOCKET, SO_EXCLUSIVEADDRUSE,
                       reinterpret_cast<const char *>(&exclusive), sizeof(exclusive)) != 0) {
            closesocket(socket);
            return INVALID_SOCKET;
        }
    }
    return socket;
}

} // namespace muninn_udp_socket
#endif
