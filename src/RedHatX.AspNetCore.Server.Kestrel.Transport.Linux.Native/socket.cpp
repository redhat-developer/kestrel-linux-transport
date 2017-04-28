#include "utilities.h"

#include <sys/types.h>
#include <sys/socket.h>
#include <sys/un.h>
#include <netinet/ip.h>
#include <netinet/tcp.h>
#include <stddef.h>
#include <sys/ioctl.h>
#include <unistd.h>
#include <string.h>
#include <fcntl.h>

#ifndef SO_INCOMING_CPU
#define SO_INCOMING_CPU 49
#endif

struct PalSocketAddress
{
    int16_t Family;
};

// NOTE: the layout of this type is intended to exactly  match the layout of a `struct iovec`. There are
//       assertions in pal_networking.cpp that validate this.
struct IOVector
{
    uint8_t* Base;
    uintptr_t Count;
};

// NOTE: clang has trouble with offsetof nested inside of static_assert. Instead, store
//       the necessary field offsets in constants.
const int OffsetOfIOVectorBase = offsetof(IOVector, Base);
const int OffsetOfIOVectorCount = offsetof(IOVector, Count);
const int OffsetOfIovecBase = offsetof(iovec, iov_base);
const int OffsetOfIovecLen = offsetof(iovec, iov_len);

// We require that IOVector have the same layout as iovec.
static_assert(sizeof(IOVector) == sizeof(iovec), "");
static_assert(sizeof(decltype(IOVector::Base)) == sizeof(decltype(iovec::iov_base)), "");
static_assert(OffsetOfIOVectorBase == OffsetOfIovecBase, "");
static_assert(sizeof(decltype(IOVector::Count)) == sizeof(decltype(iovec::iov_len)), "");
static_assert(OffsetOfIOVectorCount == OffsetOfIovecLen, "");

extern "C"
{
    PosixResult RHXKL_Socket(int32_t addressFamily, int32_t socketType, int32_t protocolType, int32_t blocking, intptr_t* createdSocket);
    PosixResult RHXKL_Connect(intptr_t socket, PalSocketAddress* palSocketAddress, int32_t palEndPointLen);
    PosixResult RHXKL_Bind(intptr_t socket, PalSocketAddress* palSocketAddress, int32_t palEndPointLen);
    PosixResult RHXKL_GetAvailableBytes(intptr_t socket);
    PosixResult RHXKL_Listen(intptr_t socket, int backlog);
    PosixResult RHXKL_Accept(intptr_t socket, PalSocketAddress* palSocketAddress, int32_t palEndPointLen, int32_t blocking, intptr_t* acceptedSocket);
    PosixResult RHXKL_Shutdown(intptr_t socket, int32_t socketShutdown);
    PosixResult RHXKL_Send(int socket, IOVector* ioVectors, int ioVectorLen);
    PosixResult RHXKL_Receive(int socket, IOVector* ioVectors, int ioVectorLen);
    PosixResult RHXKL_SetSockOpt(intptr_t socket, int32_t socketOptionLevel, int32_t socketOptionName, uint8_t* optionValue, int32_t optionLen);
    PosixResult RHXKL_GetSockOpt(intptr_t socket, int32_t socketOptionLevel, int32_t socketOptionName, uint8_t* optionValue, int32_t* optionLen);
    PosixResult RHXKL_GetPeerName(intptr_t socket, PalSocketAddress* palSocketAddress, int32_t palEndPointLen);
    PosixResult RHXKL_GetSockName(intptr_t socket, PalSocketAddress* palSocketAddress, int32_t palEndPointLen);
    PosixResult RHXKL_Duplicate(intptr_t socket, intptr_t* dup);
}

struct IPSocketAddress
{
    int16_t  Family;
    uint16_t Port;
    unsigned char Address[16];
    uint32_t FlowInfo;
    uint32_t ScopeId;
};

struct UnixSocketAddress
{
    int16_t  Family;
    unsigned char Path[108];
    uint16_t Length;
};

/*
 * Socket shutdown modes.
 *
 * NOTE: these values are taken from System.Net.SocketShutdown.
 */
enum SocketShutdown : int32_t
{
    PAL_SHUT_READ = 0,  // SHUT_RD
    PAL_SHUT_WRITE = 1, // SHUT_WR
    PAL_SHUT_BOTH = 2,  // SHUT_RDWR
};

/**
 * Address families recognized by {Get,Set}AddressFamily.
 *
 * NOTE: these values are taken from System.Net.AddressFamily. If you add
 *       new entries, be sure that the values are chosen accordingly.
 */
enum AddressFamily : int32_t
{
    PAL_AF_UNSPEC = 0, // System.Net.AddressFamily.Unspecified
    PAL_AF_UNIX = 1,   // System.Net.AddressFamily.Unix
    PAL_AF_INET = 2,   // System.Net.AddressFamily.InterNetwork
    PAL_AF_INET6 = 23, // System.Net.AddressFamily.InterNetworkV6
};

/*
 * Socket types.
 *
 * NOTE: these values are taken from System.Net.SocketType.
 */
enum SocketType : int32_t
{
    PAL_SOCK_STREAM = 1,    // System.Net.SocketType.Stream
    PAL_SOCK_DGRAM = 2,     // System.Net.SocketType.Dgram
    PAL_SOCK_RAW = 3,       // System.Net.SocketType.Raw
    PAL_SOCK_RDM = 4,       // System.Net.SocketType.Rdm
    PAL_SOCK_SEQPACKET = 5, // System.Net.SocketType.SeqPacket
};

/*
 * Protocol types.
 *
 * NOTE: these values are taken from System.Net.ProtocolType.
 */
enum ProtocolType : int32_t
{
    PAL_PT_UNSPECIFIED = 0, // System.Net.ProtocolType.Unspecified
    PAL_PT_ICMP = 1,        // System.Net.ProtocolType.Icmp
    PAL_PT_TCP = 6,         // System.Net.ProtocolType.Tcp
    PAL_PT_UDP = 17,        // System.Net.ProtocolType.Udp
    PAL_PT_ICMPV6 = 58,     // System.Net.ProtocolType.IcmpV6
};

/*
 * Socket option levels.
 *
 * NOTE: these values are taken from System.Net.SocketOptionLevel.
 */
enum SocketOptionLevel : int32_t
{
    PAL_SOL_SOCKET = 0xffff,
    PAL_SOL_IP = 0,
    PAL_SOL_IPV6 = 41,
    PAL_SOL_TCP = 6,
    PAL_SOL_UDP = 17,
};

/*
 * Socket option names.
 *
 * NOTE: these values are taken from System.Net.SocketOptionName. Only values that are known to be usable on all target
 *       platforms are represented here. Unsupported values are present as commented-out entries.
 */
enum SocketOptionName : int32_t
{
    // Names for level PAL_SOL_SOCKET
    PAL_SO_DEBUG = 0x0001,
    PAL_SO_ACCEPTCONN = 0x0002,
    PAL_SO_REUSEADDR = 0x0004,
    PAL_SO_KEEPALIVE = 0x0008,
    PAL_SO_DONTROUTE = 0x0010,
    PAL_SO_BROADCAST = 0x0020,
    // PAL_SO_USELOOPBACK = 0x0040,
    PAL_SO_LINGER = 0x0080,
    PAL_SO_OOBINLINE = 0x0100,
    // PAL_SO_DONTLINGER = ~PAL_SO_LINGER,
    PAL_SO_EXCLUSIVEADDRUSE = ~PAL_SO_REUSEADDR,
    PAL_SO_SNDBUF = 0x1001,
    PAL_SO_RCVBUF = 0x1002,
    PAL_SO_SNDLOWAT = 0x1003,
    PAL_SO_RCVLOWAT = 0x1004,
    PAL_SO_SNDTIMEO = 0x1005,
    PAL_SO_RCVTIMEO = 0x1006,
    PAL_SO_ERROR = 0x1007,
    PAL_SO_TYPE = 0x1008,

    // corefx controls this together with PAL_SO_REUSEADDR
    PAL_SO_REUSEPORT = 0x2001,
    PAL_SO_INCOMING_CPU = 0x2002,
    
    // PAL_SO_MAXCONN = 0x7fffffff,

    // Names for level PAL_SOL_IP
    PAL_SO_IP_OPTIONS = 1,
    PAL_SO_IP_HDRINCL = 2,
    PAL_SO_IP_TOS = 3,
    PAL_SO_IP_TTL = 4,
    PAL_SO_IP_MULTICAST_IF = 9,
    PAL_SO_IP_MULTICAST_TTL = 10,
    PAL_SO_IP_MULTICAST_LOOP = 11,
    PAL_SO_IP_ADD_MEMBERSHIP = 12,
    PAL_SO_IP_DROP_MEMBERSHIP = 13,
    // PAL_SO_IP_DONTFRAGMENT = 14,
    PAL_SO_IP_ADD_SOURCE_MEMBERSHIP = 15,
    PAL_SO_IP_DROP_SOURCE_MEMBERSHIP = 16,
    PAL_SO_IP_BLOCK_SOURCE = 17,
    PAL_SO_IP_UNBLOCK_SOURCE = 18,
    PAL_SO_IP_PKTINFO = 19,

    // Names for PAL_SOL_IPV6
    PAL_SO_IPV6_HOPLIMIT = 21,
    // PAL_SO_IPV6_PROTECTION_LEVEL = 23,
    PAL_SO_IPV6_V6ONLY = 27,

    // Names for PAL_SOL_TCP
    PAL_SO_TCP_NODELAY = 1,
    // PAL_SO_TCP_BSDURGENT = 2,
    PAL_SO_TCP_DEFER_ACCEPT = 3,

    // Names for PAL_SOL_UDP
    // PAL_SO_UDP_NOCHECKSUM = 1,
    // PAL_SO_UDP_CHECKSUM_COVERAGE = 20,
    // PAL_SO_UDP_UPDATEACCEPTCONTEXT = 0x700b,
    // PAL_SO_UDP_UPDATECONNECTCONTEXT = 0x7010,
};

static PosixResult SetPalSocketAddress(PalSocketAddress* palSocketAddress, int32_t palLen, sockaddr* addr, socklen_t addrLen)
{
    addrLen = 0;
    palSocketAddress->Family = PAL_AF_UNSPEC;
    switch (addr->sa_family)
    {
        case AF_INET:
            if (palLen < static_cast<int>(sizeof(IPSocketAddress)))
            {
                return PosixResultForErrno(EINVAL);
            }
            {
                auto addrin = reinterpret_cast<sockaddr_in*>(addr);
                auto ipSockAddr = reinterpret_cast<IPSocketAddress*>(palSocketAddress);
                ipSockAddr->Family = PAL_AF_INET;
                ipSockAddr->Port = ntohs(addrin->sin_port);
                ipSockAddr->FlowInfo = 0;
                ipSockAddr->ScopeId = 0;
                uint32_t addr4 = addrin->sin_addr.s_addr;
                memcpy(ipSockAddr->Address, &addr4, 4);
                return PosixResultSUCCESS;
            }
        case AF_INET6:
            if (palLen < static_cast<int>(sizeof(IPSocketAddress)))
            {
                return PosixResultForErrno(EINVAL);
            }
            {
                auto addrin6 = reinterpret_cast<sockaddr_in6*>(addr);
                auto ipSockAddr = reinterpret_cast<IPSocketAddress*>(palSocketAddress);
                ipSockAddr->Family = PAL_AF_INET6;
                ipSockAddr->Port = ntohs(addrin6->sin6_port);
                ipSockAddr->FlowInfo = addrin6->sin6_flowinfo;
                ipSockAddr->ScopeId = addrin6->sin6_scope_id;
                memcpy(ipSockAddr->Address, addrin6->sin6_addr.s6_addr, 16);
                return PosixResultSUCCESS;
            }
        case AF_UNIX:
            if (palLen < static_cast<int>(sizeof(UnixSocketAddress)))
            {
                return PosixResultForErrno(EINVAL);
            }
            {
                auto addrun = reinterpret_cast<sockaddr_un*>(addr);
                auto palUnix = reinterpret_cast<UnixSocketAddress*>(palSocketAddress);
                palUnix->Family = PAL_AF_UNIX;
                // unnamed
                if (addrLen == sizeof(sa_family_t))
                {
                    palUnix->Length = 0;
                }
                // abstract
                else if (addrun->sun_path[0] == 0)
                {
                    palUnix->Length = static_cast<uint16_t>(addrLen - sizeof(sa_family_t));
                }
                // pathname
                else
                {
                    palUnix->Length = static_cast<uint16_t>(strlen(addrun->sun_path));
                }
                memcpy(palUnix->Path, addrun->sun_path, palUnix->Length);
                return PosixResultSUCCESS;
            }
        default:
            return PosixResultForErrno(EAFNOSUPPORT);
    }
}

static bool TryGetPlatformSocketAddress(PalSocketAddress* palSocketAddress, int32_t palEndPointLength, sockaddr_storage* storage, socklen_t* sockLen)
{
    if (palEndPointLength < 2)
    {
        return false;
    }
    int32_t palAddressFamily = palSocketAddress->Family;
    switch (palAddressFamily)
    {
        case PAL_AF_UNIX:
            if (palEndPointLength != sizeof(UnixSocketAddress))
            {
                return false;
            }
            {
                auto addrun = reinterpret_cast<sockaddr_un*>(storage);
                auto palUnix = reinterpret_cast<UnixSocketAddress*>(palSocketAddress);
                if (palUnix->Length > sizeof(addrun->sun_path))
                {
                    return false;
                }
                addrun->sun_family = AF_UNIX;
                memcpy(addrun->sun_path, palUnix->Path, palUnix->Length);
                if (palUnix->Length != sizeof(addrun->sun_path))
                {
                    addrun->sun_path[palUnix->Length] = 0;
                }
                return true;
            }
        case PAL_AF_INET:
            if (palEndPointLength != sizeof(IPSocketAddress))
            {
                return false;
            }
            {
                auto addrin = reinterpret_cast<sockaddr_in*>(storage);
                auto ipSockAddr = reinterpret_cast<IPSocketAddress*>(palSocketAddress);
                addrin->sin_family = AF_INET;
                addrin->sin_port = htons(ipSockAddr->Port);
                uint32_t addr4;
                memcpy(&addr4, ipSockAddr->Address, 4);
                addrin->sin_addr.s_addr = addr4;
                *sockLen = sizeof(sockaddr_in);
                return true;
            }
        case PAL_AF_INET6:
            if (palEndPointLength != sizeof(IPSocketAddress))
            {
                return false;
            }
            {
                auto addrin6 = reinterpret_cast<sockaddr_in6*>(storage);
                auto ipSockAddr = reinterpret_cast<IPSocketAddress*>(palSocketAddress);
                addrin6->sin6_family = AF_INET6;
                addrin6->sin6_port = htons(ipSockAddr->Port);
                addrin6->sin6_flowinfo = ipSockAddr->FlowInfo;
                addrin6->sin6_scope_id = ipSockAddr->ScopeId;
                memcpy(addrin6->sin6_addr.s6_addr, ipSockAddr->Address, 16);
                *sockLen = sizeof(sockaddr_in6);
                return true;
            }
        default:
            return false;
    }
}

static bool TryGetPlatformSocketOption(int32_t socketOptionName, int32_t socketOptionLevel, int& optLevel, int& optName)
{
    switch (socketOptionName)
    {
        case PAL_SOL_SOCKET:
            optLevel = SOL_SOCKET;

            switch (socketOptionLevel)
            {
                case PAL_SO_DEBUG:
                    optName = SO_DEBUG;
                    return true;

                case PAL_SO_ACCEPTCONN:
                    optName = SO_ACCEPTCONN;
                    return true;

                case PAL_SO_REUSEADDR:
                    optName = SO_REUSEADDR;
                    return true;

                case PAL_SO_KEEPALIVE:
                    optName = SO_KEEPALIVE;
                    return true;

                case PAL_SO_DONTROUTE:
                    optName = SO_DONTROUTE;
                    return true;

                case PAL_SO_BROADCAST:
                    optName = SO_BROADCAST;
                    return true;

                // case PAL_SO_USELOOPBACK:

                case PAL_SO_LINGER:
                    optName = SO_LINGER;
                    return true;

                case PAL_SO_OOBINLINE:
                    optName = SO_OOBINLINE;
                    return true;

                // case PAL_SO_DONTLINGER:

                // case PAL_SO_EXCLUSIVEADDRUSE:

                case PAL_SO_SNDBUF:
                    optName = SO_SNDBUF;
                    return true;

                case PAL_SO_RCVBUF:
                    optName = SO_RCVBUF;
                    return true;

                case PAL_SO_SNDLOWAT:
                    optName = SO_SNDLOWAT;
                    return true;

                case PAL_SO_RCVLOWAT:
                    optName = SO_RCVLOWAT;
                    return true;

                case PAL_SO_SNDTIMEO:
                    optName = SO_SNDTIMEO;
                    return true;

                case PAL_SO_RCVTIMEO:
                    optName = SO_RCVTIMEO;
                    return true;

                case PAL_SO_ERROR:
                    optName = SO_ERROR;
                    return true;

                case PAL_SO_TYPE:
                    optName = SO_TYPE;
                    return true;

                // case PAL_SO_MAXCONN:
                case PAL_SO_REUSEPORT:
                    optName = SO_REUSEPORT;
                    return true;

                case PAL_SO_INCOMING_CPU:
                    optName = SO_INCOMING_CPU;
                    return true;

                default:
                    return false;
            }

        case PAL_SOL_IP:
            optLevel = IPPROTO_IP;

            switch (socketOptionLevel)
            {
                case PAL_SO_IP_OPTIONS:
                    optName = IP_OPTIONS;
                    return true;

                case PAL_SO_IP_HDRINCL:
                    optName = IP_HDRINCL;
                    return true;

                case PAL_SO_IP_TOS:
                    optName = IP_TOS;
                    return true;

                case PAL_SO_IP_TTL:
                    optName = IP_TTL;
                    return true;

                case PAL_SO_IP_MULTICAST_IF:
                    optName = IP_MULTICAST_IF;
                    return true;

                case PAL_SO_IP_MULTICAST_TTL:
                    optName = IP_MULTICAST_TTL;
                    return true;

                case PAL_SO_IP_MULTICAST_LOOP:
                    optName = IP_MULTICAST_LOOP;
                    return true;

                case PAL_SO_IP_ADD_MEMBERSHIP:
                    optName = IP_ADD_MEMBERSHIP;
                    return true;

                case PAL_SO_IP_DROP_MEMBERSHIP:
                    optName = IP_DROP_MEMBERSHIP;
                    return true;

                // case PAL_SO_IP_DONTFRAGMENT:

#ifdef IP_ADD_SOURCE_MEMBERSHIP
                case PAL_SO_IP_ADD_SOURCE_MEMBERSHIP:
                    optName = IP_ADD_SOURCE_MEMBERSHIP;
                    return true;
#endif

#ifdef IP_DROP_SOURCE_MEMBERSHIP
                case PAL_SO_IP_DROP_SOURCE_MEMBERSHIP:
                    optName = IP_DROP_SOURCE_MEMBERSHIP;
                    return true;
#endif

#ifdef IP_BLOCK_SOURCE
                case PAL_SO_IP_BLOCK_SOURCE:
                    optName = IP_BLOCK_SOURCE;
                    return true;
#endif

#ifdef IP_UNBLOCK_SOURCE
                case PAL_SO_IP_UNBLOCK_SOURCE:
                    optName = IP_UNBLOCK_SOURCE;
                    return true;
#endif

                case PAL_SO_IP_PKTINFO:
                    optName = IP_PKTINFO;
                    return true;

                default:
                    return false;
            }

        case PAL_SOL_IPV6:
            optLevel = IPPROTO_IPV6;

            switch (socketOptionLevel)
            {
                case PAL_SO_IPV6_HOPLIMIT:
                    optName = IPV6_HOPLIMIT;
                    return true;

                // case PAL_SO_IPV6_PROTECTION_LEVEL:

                case PAL_SO_IPV6_V6ONLY:
                    optName = IPV6_V6ONLY;
                    return true;

                case PAL_SO_IP_PKTINFO:
                    optName = IPV6_RECVPKTINFO;
                    return true;

                default:
                    return false;
            }

        case PAL_SOL_TCP:
            optLevel = IPPROTO_TCP;

            switch (socketOptionLevel)
            {
                case PAL_SO_TCP_NODELAY:
                    optName = TCP_NODELAY;
                    return true;

                // case PAL_SO_TCP_BSDURGENT:

                case PAL_SO_TCP_DEFER_ACCEPT:
                    optName = TCP_DEFER_ACCEPT;
                    return true;

                default:
                    return false;
            }

        case PAL_SOL_UDP:
            optLevel = IPPROTO_UDP;

            switch (socketOptionLevel)
            {
                // case PAL_SO_UDP_NOCHECKSUM:

                // case PAL_SO_UDP_CHECKSUM_COVERAGE:

                // case PAL_SO_UDP_UPDATEACCEPTCONTEXT:

                // case PAL_SO_UDP_UPDATECONNECTCONTEXT:

                default:
                    return false;
            }

        default:
            return false;
    }
}

static bool TryConvertAddressFamilyPalToPlatform(int32_t palAddressFamily, sa_family_t* platformAddressFamily)
{
    assert(platformAddressFamily != nullptr);

    switch (palAddressFamily)
    {
        case PAL_AF_UNSPEC:
            *platformAddressFamily = AF_UNSPEC;
            return true;

        case PAL_AF_UNIX:
            *platformAddressFamily = AF_UNIX;
            return true;

        case PAL_AF_INET:
            *platformAddressFamily = AF_INET;
            return true;

        case PAL_AF_INET6:
            *platformAddressFamily = AF_INET6;
            return true;

        default:
            *platformAddressFamily = static_cast<sa_family_t>(palAddressFamily);
            return false;
    }
}

static bool TryConvertSocketTypePalToPlatform(int32_t palSocketType, int* platformSocketType)
{
    assert(platformSocketType != nullptr);

    switch (palSocketType)
    {
        case PAL_SOCK_STREAM:
            *platformSocketType = SOCK_STREAM;
            return true;

        case PAL_SOCK_DGRAM:
            *platformSocketType = SOCK_DGRAM;
            return true;

        case PAL_SOCK_RAW:
            *platformSocketType = SOCK_RAW;
            return true;

        case PAL_SOCK_RDM:
            *platformSocketType = SOCK_RDM;
            return true;

        case PAL_SOCK_SEQPACKET:
            *platformSocketType = SOCK_SEQPACKET;
            return true;

        default:
            *platformSocketType = static_cast<int>(palSocketType);
            return false;
    }
}

static bool TryConvertProtocolTypePalToPlatform(int32_t palProtocolType, int* platformProtocolType)
{
    assert(platformProtocolType != nullptr);

    switch (palProtocolType)
    {
        case PAL_PT_UNSPECIFIED:
            *platformProtocolType = 0;
            return true;

        case PAL_PT_ICMP:
            *platformProtocolType = IPPROTO_ICMP;
            return true;

        case PAL_PT_TCP:
            *platformProtocolType = IPPROTO_TCP;
            return true;

        case PAL_PT_UDP:
            *platformProtocolType = IPPROTO_UDP;
            return true;

        case PAL_PT_ICMPV6:
            *platformProtocolType = IPPROTO_ICMPV6;
            return true;

        default:
            *platformProtocolType = static_cast<int>(palProtocolType);
            return false;
    }
}

PosixResult RHXKL_Socket(int32_t addressFamily, int32_t socketType, int32_t protocolType, int32_t blocking, intptr_t* createdSocket)
{
    if (createdSocket == nullptr)
    {
        return PosixResultEFAULT;
    }

    sa_family_t platformAddressFamily;
    int platformSocketType, platformProtocolType;

    if (!TryConvertAddressFamilyPalToPlatform(addressFamily, &platformAddressFamily))
    {
        *createdSocket = -1;
        return PosixResultForErrno(EAFNOSUPPORT);
    }

    if (!TryConvertSocketTypePalToPlatform(socketType, &platformSocketType))
    {
        *createdSocket = -1;
        return PosixResultForErrno(EPROTOTYPE);
    }

    if (!TryConvertProtocolTypePalToPlatform(protocolType, &platformProtocolType))
    {
        *createdSocket = -1;
        return PosixResultForErrno(EPROTONOSUPPORT);
    }

    platformSocketType |= SOCK_CLOEXEC;
    if (blocking == 0)
    {
        platformSocketType |= SOCK_NONBLOCK;
    }

    int rv = socket(platformAddressFamily, platformSocketType, platformProtocolType);

    *createdSocket = rv;
    return ToPosixResult(rv);
}

PosixResult RHXKL_GetAvailableBytes(intptr_t socket)
{
    int fd = ToFileDescriptor(socket);

    int rv;
    int err = ioctl(fd, FIONREAD, &rv);
    if (err == -1)
    {
        rv = -1;
    }

    return ToPosixResult(rv);
}

PosixResult RHXKL_Connect(intptr_t socket, PalSocketAddress* palSocketAddress, int32_t palEndPointLen)
{
    int fd = ToFileDescriptor(socket);
    sockaddr_storage storage;
    socklen_t sockLen;
    if (!TryGetPlatformSocketAddress(palSocketAddress, palEndPointLen, &storage, &sockLen))
    {
        return PosixResultEFAULT;
    }

    int rv;
    while (CheckInterrupted(rv = connect(fd, reinterpret_cast<sockaddr*>(&storage), sockLen)));
    return ToPosixResult(rv);
}

PosixResult RHXKL_Bind(intptr_t socket, PalSocketAddress* palSocketAddress, int32_t palEndPointLen)
{
    int fd = ToFileDescriptor(socket);
    sockaddr_storage storage;
    socklen_t sockLen;
    if (!TryGetPlatformSocketAddress(palSocketAddress, palEndPointLen, &storage, &sockLen))
    {
        return PosixResultEFAULT;
    }

    int rv = bind(fd, reinterpret_cast<sockaddr*>(&storage), sockLen);
    return ToPosixResult(rv);
}

PosixResult RHXKL_Listen(intptr_t socket, int32_t backlog)
{
    int fd = ToFileDescriptor(socket);
    int rv = listen(fd, backlog);
    return ToPosixResult(rv);
}

PosixResult RHXKL_Accept(intptr_t socket, PalSocketAddress* palSocketAddress, int32_t palEndPointLen, int blocking, intptr_t* acceptedSocket)
{
    if (acceptedSocket == nullptr || (palSocketAddress != nullptr && palEndPointLen < 2))
    {
        return PosixResultEFAULT;
    }

    int fd = ToFileDescriptor(socket);
    int flags = SOCK_CLOEXEC;
    if (blocking == 0)
    {
        flags |= SOCK_NONBLOCK;
    }

    sockaddr_storage storage;
    socklen_t sockLen = sizeof(sockaddr_storage);
    sockaddr* sockAddr = palSocketAddress != nullptr ? reinterpret_cast<sockaddr*>(&storage) : nullptr;
    int rv;
    while (CheckInterrupted(rv = accept4(fd, sockAddr, &sockLen, flags)));
    *acceptedSocket = rv;
    if (rv != -1 && palSocketAddress != nullptr)
    {
        SetPalSocketAddress(palSocketAddress, palEndPointLen, reinterpret_cast<sockaddr*>(&storage), sockLen);
    }

    return ToPosixResult(rv);
}

PosixResult RHXKL_Shutdown(intptr_t socket, int32_t socketShutdown)
{
    int fd = ToFileDescriptor(socket);

    int how;
    switch (socketShutdown)
    {
        case PAL_SHUT_READ:
            how = SHUT_RD;
            break;

        case PAL_SHUT_WRITE:
            how = SHUT_WR;
            break;

        case PAL_SHUT_BOTH:
            how = SHUT_RDWR;
            break;

        default:
            return PosixResultForErrno(EINVAL);
    }

    int rv = shutdown(fd, how);
    return ToPosixResult(rv);
}

PosixResult RHXKL_Send(int fd, IOVector* ioVectors, int ioVectorLen)
{
    if (ioVectors == nullptr || ioVectorLen <= 0)
    {
        return PosixResultEFAULT;
    }

    // TODO: check we don't' send more than INT_MAX byte because we are returning an int

    msghdr header =
    {
        .msg_name = nullptr,
        .msg_namelen = 0,
        .msg_iov = reinterpret_cast<iovec*>(ioVectors),
        .msg_iovlen = static_cast<size_t>(ioVectorLen),
        .msg_control = nullptr,
        .msg_controllen = 0,
        .msg_flags = 0
    };
    int flags = MSG_NOSIGNAL;

    int rv;
    while (CheckInterrupted(rv = static_cast<int>(sendmsg(fd, &header, flags))));
    return ToPosixResult(rv);
}

PosixResult RHXKL_Receive(int fd, IOVector* ioVectors, int ioVectorLen)
{
    if (ioVectors == nullptr || ioVectorLen <= 0)
    {
        return PosixResultEFAULT;
    }

    // TODO: check we don't' send more than INT_MAX byte because we are returning an int

    msghdr header =
    {
        .msg_name = nullptr,
        .msg_namelen = 0,
        .msg_iov = reinterpret_cast<iovec*>(ioVectors),
        .msg_iovlen = static_cast<size_t>(ioVectorLen),
        .msg_control = nullptr,
        .msg_controllen = 0,
        .msg_flags = 0
    };
    int flags = MSG_NOSIGNAL;

    int rv;
    while (CheckInterrupted(rv = static_cast<int>(recvmsg(fd, &header, flags))));
    return ToPosixResult(rv);
}

PosixResult RHXKL_SetSockOpt(intptr_t socket, int32_t socketOptionLevel, int32_t socketOptionName, uint8_t* optionValue, int32_t optionLen)
{
    if (optionLen < 0)
    {
        return PosixResultEFAULT;
    }

    int fd = ToFileDescriptor(socket);

    int optLevel, optName;
    if (!TryGetPlatformSocketOption(socketOptionLevel, socketOptionName, optLevel, optName))
    {
        return PosixResultForErrno(ENOTSUP);
    }

    int rv = setsockopt(fd, optLevel, optName, optionValue, static_cast<socklen_t>(optionLen));
    return ToPosixResult(rv);
}

PosixResult RHXKL_GetSockOpt(intptr_t socket, int32_t socketOptionLevel, int32_t socketOptionName, uint8_t* optionValue, int32_t* optionLen)
{
    if (optionLen == nullptr || *optionLen < 0)
    {
        return PosixResultEFAULT;
    }

    int fd = ToFileDescriptor(socket);

    int optLevel, optName;
    if (!TryGetPlatformSocketOption(socketOptionLevel, socketOptionName, optLevel, optName))
    {
        return PosixResultForErrno(ENOTSUP);
    }

    auto optLen = static_cast<socklen_t>(*optionLen);
    int rv = getsockopt(fd, optLevel, optName, optionValue, &optLen);

    *optionLen = static_cast<int32_t>(optLen);
    return ToPosixResult(rv);
}

PosixResult RHXKL_GetPeerName(intptr_t socket, PalSocketAddress* palSocketAddress, int32_t palEndPointLen)
{
    if (palSocketAddress == nullptr || palEndPointLen < 2)
    {
        return PosixResultEFAULT;
    }

    int fd = ToFileDescriptor(socket);
    sockaddr_storage storage;
    socklen_t sockLen = sizeof(sockaddr_storage);
    int rv = getpeername(fd, reinterpret_cast<sockaddr*>(&storage), &sockLen);
    if (rv != -1)
    {
        return SetPalSocketAddress(palSocketAddress, palEndPointLen, reinterpret_cast<sockaddr*>(&storage), sockLen);
    }

    return ToPosixResult(rv);
}

PosixResult RHXKL_GetSockName(intptr_t socket, PalSocketAddress* palSocketAddress, int32_t palEndPointLen)
{
    if (palSocketAddress == nullptr || palEndPointLen < 2)
    {
        return PosixResultEFAULT;
    }

    int fd = ToFileDescriptor(socket);
    sockaddr_storage storage;
    socklen_t sockLen = sizeof(sockaddr_storage);
    int rv = getsockname(fd, reinterpret_cast<sockaddr*>(&storage), &sockLen);
    if (rv != -1)
    {
        return SetPalSocketAddress(palSocketAddress, palEndPointLen, reinterpret_cast<sockaddr*>(&storage), sockLen);
    }

    return ToPosixResult(rv);
}

PosixResult RHXKL_Duplicate(intptr_t socket, intptr_t* dup)
{
    if (dup == nullptr)
    {
        return PosixResultEFAULT;
    }
    int fd = ToFileDescriptor(socket);
    int rv = fcntl(fd, F_DUPFD_CLOEXEC, 0);
    *dup = rv;

    return ToPosixResult(rv);
}
