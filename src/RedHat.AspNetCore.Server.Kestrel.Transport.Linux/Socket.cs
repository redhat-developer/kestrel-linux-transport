using System;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using Tmds.Linux;
using static Tmds.Linux.LibC;

namespace RedHat.AspNetCore.Server.Kestrel.Transport.Linux
{
    struct SocketPair
    {
        public int Socket1;
        public int Socket2;

        public void Dispose()
        {
            if (Socket1 != -1)
            {
                IOInterop.Close(Socket1);
                Socket1 = -1;
            }
            if (Socket2 != -1)
            {
                IOInterop.Close(Socket2);
                Socket2 = -1;
            }
        }
    }

    static class SocketInterop
    {
        public static unsafe PosixResult Socket(int domain, int type, int protocol, bool blocking, out int s)
        {
            type |= SOCK_CLOEXEC;
            if (!blocking)
            {
                type |= SOCK_NONBLOCK;
            }

            s = socket(domain, type, protocol);

            return PosixResult.FromReturnValue(s);
        }

        public static PosixResult Socket(int domain, int type, int protocol, bool blocking, out Socket socket)
        {
            int socketFd;
            PosixResult result = Socket(domain, type, protocol, blocking, out socketFd);
            socket = result.IsSuccess ? new Socket(socketFd) : null;
            return result;
        }

        public static unsafe PosixResult GetAvailableBytes(int socket)
        {
            int availableBytes;
            int rv = ioctl(socket, FIONREAD, &availableBytes);

            return PosixResult.FromReturnValue(rv == -1 ? rv : availableBytes);
        }

        public static PosixResult GetAvailableBytes(Socket socket)
            => GetAvailableBytes(socket.DangerousGetHandle().ToInt32());

        public static unsafe PosixResult Accept(int socket, bool blocking, out int clientSocket)
        {
            int flags = SOCK_CLOEXEC;
            if (!blocking)
            {
                flags |= SOCK_NONBLOCK;
            }
            int rv;
            do
            {
                rv = accept4(socket, null, null, flags);
            } while (rv < 0 && errno == EINTR);

            clientSocket = rv;

            return PosixResult.FromReturnValue(rv);
        }

        public static unsafe PosixResult Accept(Socket socket, bool blocking, out Socket clientSocket)
        {
            int clientSocketFd;
            PosixResult result = Accept(socket.DangerousGetHandle().ToInt32(), blocking, out clientSocketFd);
            clientSocket = result.IsSuccess ? new Socket(clientSocketFd) : null;
            return result;
        }

        public static PosixResult Shutdown(Socket socket, int how)
            => Shutdown(socket.DangerousGetHandle().ToInt32(), how);

        public static PosixResult Shutdown(int socket, int how)
        {
            int rv = shutdown(socket, how);

            return PosixResult.FromReturnValue(rv);
        }

        public static unsafe PosixResult Send(int socket, iovec* ioVectors, int ioVectorLen, int flags = 0)
        {
            msghdr hdr = default(msghdr);
            hdr.msg_iov = ioVectors;
            hdr.msg_iovlen = ioVectorLen;

            flags |= MSG_NOSIGNAL;

            ssize_t rv;
            do
            {
                rv = sendmsg(socket, &hdr, flags);
            } while (rv < 0 && errno == EINTR);

            return PosixResult.FromReturnValue(rv);
        }

        public static unsafe PosixResult Send(SafeHandle socket, iovec* ioVectors, int ioVectorLen, int flags = 0)
        => Send(socket.DangerousGetHandle().ToInt32(), ioVectors, ioVectorLen, flags);

        public static unsafe PosixResult Receive(int socket, iovec* ioVectors, int ioVectorLen)
        {
            msghdr hdr = default(msghdr);
            hdr.msg_iov = ioVectors;
            hdr.msg_iovlen = ioVectorLen;

            int flags = MSG_NOSIGNAL;

            ssize_t rv;
            do
            {
                rv = recvmsg(socket, &hdr, flags);
            } while (rv < 0 && errno == EINTR);

            return PosixResult.FromReturnValue(rv);
        }

        public static unsafe PosixResult Receive(SafeHandle socket, iovec* ioVectors, int ioVectorLen)
        => Receive(socket.DangerousGetHandle().ToInt32(), ioVectors, ioVectorLen);

        public static unsafe PosixResult SetSockOpt(int socket, int level, int optname, void* optval, socklen_t optlen)
        {
            int rv = setsockopt(socket, level, optname, optval, optlen);

            return PosixResult.FromReturnValue(rv);
        }

        public static unsafe PosixResult SetSockOpt(Socket socket, int level, int optname, void* optval, socklen_t optlen)
            => SetSockOpt(socket.DangerousGetHandle().ToInt32(), level, optname, optval, optlen);

        public static unsafe PosixResult GetSockOpt(SafeHandle socket, int level, int optname, void* optval, socklen_t* optlen)
        {
            int rv = getsockopt(socket.DangerousGetHandle().ToInt32(), level, optname, optval, optlen);

            return PosixResult.FromReturnValue(rv);
        }

        public static unsafe PosixResult GetPeerName(int socket, sockaddr_storage* addr)
        {
            socklen_t sockLen = SizeOf.sockaddr_storage;
            int rv = getpeername(socket, (sockaddr*)addr, &sockLen);

            return PosixResult.FromReturnValue(rv);
        }

        public static unsafe PosixResult GetPeerName(Socket socket, sockaddr_storage* addr)
            => GetPeerName(socket.DangerousGetHandle().ToInt32(), addr);

        public static unsafe PosixResult GetSockName(int socket, sockaddr_storage* addr)
        {
            socklen_t sockLen = SizeOf.sockaddr_storage;
            int rv = getsockname(socket, (sockaddr*)addr, &sockLen);

            return PosixResult.FromReturnValue(rv);
        }

        public static unsafe PosixResult GetSockName(Socket socket, sockaddr_storage* addr)
            => GetSockName(socket.DangerousGetHandle().ToInt32(), addr);

        public static unsafe PosixResult SocketPair(int domain, int type, int protocol, bool blocking, out int socket1, out int socket2)
        {
            int* sv = stackalloc int[2];

            type |= SOCK_CLOEXEC;

            if (!blocking)
            {
                type |= SOCK_NONBLOCK;
            }

            int rv = socketpair(domain, type, protocol, sv);

            if (rv == 0)
            {
                socket1 = sv[0];
                socket2 = sv[1];
            }
            else
            {
                socket1 = -1;
                socket2 = -1;
            }

            return PosixResult.FromReturnValue(rv);
        }

        public unsafe static PosixResult ReceiveSocket(int fromSocket, out int socket, bool blocking)
        {
            socket = -1;
            byte dummyBuffer = 0;
            iovec iov = default(iovec);
            iov.iov_base = &dummyBuffer;
            iov.iov_len = 1;

            int controlLength = CMSG_SPACE(sizeof(int));
            byte* control = stackalloc byte[controlLength];

            msghdr header = default(msghdr);
            header.msg_iov = &iov;
            header.msg_iovlen = 1;
            header.msg_control = control;
            header.msg_controllen = controlLength;

            int flags = MSG_NOSIGNAL | MSG_CMSG_CLOEXEC;

            ssize_t rv;
            do
            {
                rv = recvmsg(fromSocket, &header, flags);
            } while (rv < 0 && errno == EINTR);

            if (rv != -1)
            {
                for (cmsghdr* cmsg = CMSG_FIRSTHDR(&header); cmsg != null; cmsg = CMSG_NXTHDR(&header,cmsg))
                {
                    if (cmsg->cmsg_level == SOL_SOCKET && cmsg->cmsg_type == SCM_RIGHTS)
                    {
                        int* fdptr = (int*)CMSG_DATA(cmsg);
                        socket = *fdptr;

                        flags = fcntl(socket, F_GETFL, 0);
                        if (blocking)
                        {
                            flags &= ~O_NONBLOCK;
                        }
                        else
                        {
                            flags |= O_NONBLOCK;
                        }
                        break;
                    }
                }
            }

            return PosixResult.FromReturnValue(rv);
        }

        public static PosixResult ReceiveSocket(Socket fromSocket, out Socket socket, bool blocking)
        {
            int receiveSocketFd;
            PosixResult result = ReceiveSocket(fromSocket.DangerousGetHandle().ToInt32(), out receiveSocketFd, blocking);
            socket = result.IsSuccess ? new Socket(receiveSocketFd) : null;
            return result;
        }

        public unsafe static PosixResult AcceptAndSendHandleTo(Socket fromSocket, int toSocket)
        {
            int acceptFd = fromSocket.DangerousGetHandle().ToInt32();
            ssize_t rv;
            do
            {
                rv = accept4(acceptFd, null, null, SOCK_CLOEXEC);
            } while (rv < 0 && errno == EINTR);

            if (rv != -1)
            {
                int acceptedFd = (int)rv;

                byte dummyBuffer = 0;
                iovec iov = default(iovec);
                iov.iov_base = &dummyBuffer;
                iov.iov_len = 1;

                int controlLength = CMSG_SPACE(sizeof(int));
                byte* control = stackalloc byte[controlLength];

                msghdr header = default(msghdr);
                header.msg_iov = &iov;
                header.msg_iovlen = 1;
                header.msg_control = control;
                header.msg_controllen = controlLength;

                cmsghdr* cmsg = CMSG_FIRSTHDR(&header);
                cmsg->cmsg_level = SOL_SOCKET;
                cmsg->cmsg_type = SCM_RIGHTS;
                cmsg->cmsg_len = CMSG_LEN(sizeof(int));
                int *fdptr = (int*)CMSG_DATA(cmsg);
                *fdptr = acceptedFd;

                do
                {
                    rv = sendmsg(toSocket, &header, MSG_NOSIGNAL);
                } while (rv < 0 && errno == EINTR);

                IOInterop.Close(acceptedFd);
            }

            return PosixResult.FromReturnValue(rv);
        }

        public unsafe static PosixResult CompleteZeroCopy(int socket)
        {
            msghdr msg = default(msghdr);
            int controlLength = 100;
            byte* control = stackalloc byte[controlLength];

            do
            {
                msg.msg_control = control;
                msg.msg_controllen = controlLength;

                ssize_t rv;
                do
                {
                    rv = recvmsg(socket, &msg, MSG_NOSIGNAL| MSG_ERRQUEUE);
                } while (rv < 0 && errno == EINTR);

                if (rv == -1)
                {
                    return PosixResult.FromReturnValue(rv);
                }
                cmsghdr* cm = CMSG_FIRSTHDR(&msg);
                if (cm == null)
                {
                    continue;
                }

                if (!((cm->cmsg_level == SOL_IP && cm->cmsg_type == IP_RECVERR) ||
                    (cm->cmsg_level == SOL_IPV6 && cm->cmsg_type == IPV6_RECVERR)))
                {
                    continue;
                }

                sock_extended_err *serr = (sock_extended_err*)CMSG_DATA(cm);
                if ((serr->ee_origin != SO_EE_ORIGIN_ZEROCOPY) ||
                    (serr->ee_errno != 0))
                {
                    continue;
                }

                return new PosixResult(((serr->ee_code & SO_EE_CODE_ZEROCOPY_COPIED) != 0) ?
                                        ZeroCopyCopied : ZeroCopySuccess);
            } while (true);
        }

        public static unsafe PosixResult Disconnect(int socket)
        {
            sockaddr addr = default(sockaddr);
            addr.sa_family = AF_UNSPEC;

            int rv;
            do
            {
                rv = connect(socket, &addr, SizeOf.sockaddr);
            } while (rv < 0 && errno == EINTR);

            return PosixResult.FromReturnValue(rv);
        }

        public const int ZeroCopyCopied = 0;
        public const int ZeroCopySuccess = 1;

        public static unsafe PosixResult TryGetLocalIPAddress(Socket socket, out IPEndPointStruct ep, IPAddress reuseAddress = null)
            => TryGetLocalIPAddress(socket.DangerousGetHandle().ToInt32(), out ep, reuseAddress);

        public static unsafe PosixResult TryGetLocalIPAddress(int socket, out IPEndPointStruct ep, IPAddress reuseAddress = null)
        {
            sockaddr_storage socketAddress;
            var rv = SocketInterop.GetSockName(socket, &socketAddress);
            if (rv.IsSuccess)
            {
                if (!ToIPEndPointStruct(&socketAddress, out ep, reuseAddress))
                {
                    return new PosixResult(PosixResult.EINVAL);
                }
            }
            else
            {
                ep = default(IPEndPointStruct);
            }
            return rv;
        }

        public static unsafe PosixResult TryGetPeerIPAddress(Socket socket, out IPEndPointStruct ep, IPAddress reuseAddress = null)
            => TryGetPeerIPAddress(socket.DangerousGetHandle().ToInt32(), out ep, reuseAddress);

        public static unsafe PosixResult TryGetPeerIPAddress(int socket, out IPEndPointStruct ep, IPAddress reuseAddress = null)
        {
            sockaddr_storage socketAddress;
            var rv = SocketInterop.GetPeerName(socket, &socketAddress);
            if (rv.IsSuccess)
            {
                if (!ToIPEndPointStruct(&socketAddress, out ep, reuseAddress))
                {
                    return new PosixResult(PosixResult.EINVAL);
                }
            }
            else
            {
                ep = default(IPEndPointStruct);
            }
            return rv;
        }

        private static unsafe bool ToIPEndPointStruct(sockaddr_storage* addr, out IPEndPointStruct ep, IPAddress reuseAddress = null)
        {
            if (addr->ss_family == AF_INET)
            {
                sockaddr_in* addrIn = (sockaddr_in*)addr;
                long value = ((addrIn->sin_addr.s_addr[3] << 24 | addrIn->sin_addr.s_addr[2] << 16 | addrIn->sin_addr.s_addr[1] << 8 | addrIn->sin_addr.s_addr[0]) & 0x0FFFFFFFF);
#pragma warning disable CS0618 // 'IPAddress.Address' is obsolete
                bool matchesReuseAddress = reuseAddress != null && reuseAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && reuseAddress.Address == value;
#pragma warning restore CS0618
                int port = ntohs(addrIn->sin_port);
                ep = new IPEndPointStruct(matchesReuseAddress ? reuseAddress : new IPAddress(value), port);
                return true;
            }
            else if (addr->ss_family == AF_INET6)
            {
                sockaddr_in6* addrIn = (sockaddr_in6*)addr;
                // We can't check if we can use reuseAddress without allocating.
                const int length = 16;
                var bytes = new byte[length];
                for (int i = 0; i < length; i++)
                {
                    bytes[i] = addrIn->sin6_addr.s6_addr[i];
                }
                int port = ntohs(addrIn->sin6_port);
                ep = new IPEndPointStruct(new IPAddress(bytes, addrIn->sin6_scope_id), port);
                return true;
            }
            else
            {
                ep = default(IPEndPointStruct);
                return false;
            }
        }
    }

    // Warning: Some operations use DangerousGetHandle for increased performance
    unsafe class Socket : CloseSafeHandle
    {
        private Socket()
        {}

        public Socket(int handle) :
            base(handle)
        {}

        public static Socket Create(int domain, int type, int protocol, bool blocking)
        {
            Socket socket;
            var result = SocketInterop.Socket(domain, type, protocol, blocking, out socket);
            result.ThrowOnError();
            return socket;
        }

        public int GetAvailableBytes()
        {
            var result = TryGetAvailableBytes();
            result.ThrowOnError();
            return result.IntValue;
        }

        public PosixResult TryGetAvailableBytes()
        {
            return SocketInterop.GetAvailableBytes(this);
        }

        public void Bind(string unixPath)
        {
            TryBind(unixPath)
                .ThrowOnError();
        }

        public unsafe PosixResult TryBind(string unixPath)
        {
            sockaddr_un addr;
            GetSockaddrUn(unixPath, out addr);
            int rv = bind(DangerousGetHandle().ToInt32(), (sockaddr*)&addr, SizeOf.sockaddr_un);

            return PosixResult.FromReturnValue(rv);
        }

        public void Bind(IPEndPointStruct endpoint)
        {
            TryBind(endpoint)
                .ThrowOnError();
        }

        public unsafe PosixResult TryBind(IPEndPointStruct endpoint)
        {
            sockaddr_storage addr;
            GetSockaddrInet(endpoint, &addr, out int length);

            int rv = bind(DangerousGetHandle().ToInt32(), (sockaddr*)&addr, length);

            return PosixResult.FromReturnValue(rv);
        }

        public void Connect(IPEndPointStruct endpoint)
        {
            TryConnect(endpoint)
                .ThrowOnError();
        }

        public unsafe PosixResult TryConnect(IPEndPointStruct endpoint)
        {
            sockaddr_storage addr;
            GetSockaddrInet(endpoint, &addr, out int length);
            int rv;
            do
            {
                rv = connect(DangerousGetHandle().ToInt32(), (sockaddr*)&addr, length);
            } while (rv < 0 && errno == EINTR);

            return PosixResult.FromReturnValue(rv);
        }

        public void Connect(string unixPath)
        {
            TryConnect(unixPath)
                .ThrowOnError();
        }

        public unsafe PosixResult TryConnect(string unixPath)
        {
            sockaddr_un addr;
            GetSockaddrUn(unixPath, out addr);
            int rv;
            do
            {
                rv = connect(DangerousGetHandle().ToInt32(), (sockaddr*)&addr, SizeOf.sockaddr_un);
            } while (rv < 0 && errno == EINTR);

            return PosixResult.FromReturnValue(rv);
        }

        private static void GetSockaddrUn(string unixPath, out sockaddr_un addr)
        {
            addr = default(sockaddr_un);
            addr.sun_family = AF_UNIX;
            var bytes = Encoding.UTF8.GetBytes(unixPath);
            int length = Math.Min(bytes.Length, sockaddr_un.sun_path_length - 1);
            fixed (byte* pathBytes = bytes)
            {
                for (int i = 0; i < length; i++)
                {
                    addr.sun_path[i] = bytes[i];
                }
            }
            addr.sun_path[length] = 0;
        }

        internal static unsafe void GetSockaddrInet(IPEndPointStruct inetAddress, sockaddr_storage* addr, out int length)
        {
            if (inetAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            {
                sockaddr_in* addrIn = (sockaddr_in*)addr;
                addrIn->sin_family = AF_INET;
                addrIn->sin_port = htons((ushort)inetAddress.Port);
                int bytesWritten;
                inetAddress.Address.TryWriteBytes(new Span<byte>(addrIn->sin_addr.s_addr, 4), out bytesWritten);
                length = SizeOf.sockaddr_in;
            }
            else if (inetAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
            {
                sockaddr_in6* addrIn = (sockaddr_in6*)addr;
                addrIn->sin6_family = AF_INET6;
                addrIn->sin6_port = htons((ushort)inetAddress.Port);
                addrIn->sin6_flowinfo = 0;
                addrIn->sin6_scope_id = 0;
                int bytesWritten;
                inetAddress.Address.TryWriteBytes(new Span<byte>(addrIn->sin6_addr.s6_addr, 16), out bytesWritten);
                length = SizeOf.sockaddr_in6;
            }
            else
            {
                length = 0;
            }
        }

        public void Listen(int backlog)
        {
            TryListen(backlog)
                .ThrowOnError();
        }

        public PosixResult TryListen(int backlog)
        {
            int rv = listen(DangerousGetHandle().ToInt32(), backlog);

            return PosixResult.FromReturnValue(rv);
        }

        public unsafe Socket Accept(bool blocking)
        {
            Socket clientSocket;
            var result = TryAccept(out clientSocket, blocking);
            result.ThrowOnError();
            return clientSocket;
        }

        public unsafe PosixResult TryAccept(out Socket clientSocket, bool blocking)
        {
            return SocketInterop.Accept(this, blocking, out clientSocket);
        }

        public int Receive(ArraySegment<byte> buffer)
        {
            var result = TryReceive(buffer);
            result.ThrowOnError();
            return result.IntValue;
        }

        public unsafe PosixResult TryReceive(ArraySegment<byte> buffer)
        {
            ValidateSegment(buffer);
            fixed (byte* buf = buffer.Array)
            {
                iovec ioVector = new iovec() { iov_base = buf + buffer.Offset, iov_len = buffer.Count };
                return SocketInterop.Receive(this, &ioVector, 1);
            }
        }

        public unsafe ssize_t Receive(iovec* ioVectors, int ioVectorLen)
        {
            var result = TryReceive(ioVectors, ioVectorLen);
            result.ThrowOnError();
            return result.Value;
        }

        public unsafe PosixResult TryReceive(iovec* ioVectors, int ioVectorLen)
        {
            return SocketInterop.Receive(this, ioVectors, ioVectorLen);
        }

        public void Shutdown(int shutdown)
        {
            TryShutdown(shutdown)
                .ThrowOnError();
        }

        public PosixResult TryShutdown(int shutdown)
        {
            return SocketInterop.Shutdown(this, shutdown);
        }

        public int Send(ArraySegment<byte> buffer)
        {
            var result = TrySend(buffer);
            result.ThrowOnError();
            return result.IntValue;
        }

        public unsafe PosixResult TrySend(ArraySegment<byte> buffer)
        {
            ValidateSegment(buffer);
            fixed (byte* buf = buffer.Array)
            {
                iovec ioVector = new iovec() { iov_base = buf + buffer.Offset, iov_len = buffer.Count };
                return SocketInterop.Send(this, &ioVector, 1);
            }
        }

        public unsafe ssize_t Send(iovec* ioVectors, int ioVectorLen)
        {
            var result = TrySend(ioVectors, ioVectorLen);
            result.ThrowOnError();
            return result.Value;
        }

        public unsafe PosixResult TrySend(iovec* ioVectors, int ioVectorLen)
        {
            return SocketInterop.Send(this, ioVectors, ioVectorLen);
        }

        public void SetSocketOption(int level, int optname, int value)
        {
            TrySetSocketOption(level, optname, value)
                .ThrowOnError();
        }

        public unsafe PosixResult TrySetSocketOption(int level, int optname, int value)
        {
            return SocketInterop.SetSockOpt(this, level, optname, (byte*)&value, 4);
        }

        // TODO: rename to GetSocketOptionInt
        public int GetSocketOption(int level, int optname)
        {
            int value = 0;
            var result = TryGetSocketOption(level, optname, ref value);
            result.ThrowOnError();
            return value;
        }

        public unsafe PosixResult TryGetSocketOption(int level, int optname, ref int value)
        {
            int v = 0;
            socklen_t length = 4;
            var rv = SocketInterop.GetSockOpt(this, level, optname, (byte*)&v, &length);
            if (rv.IsSuccess)
            {
                value = v;
            }
            return rv;
        }

        public IPEndPointStruct GetLocalIPAddress(IPAddress reuseAddress = null)
        {
            IPEndPointStruct ep;
            TryGetLocalIPAddress(out ep, reuseAddress)
                .ThrowOnError();
            return ep;
        }

        public unsafe PosixResult TryGetLocalIPAddress(out IPEndPointStruct ep, IPAddress reuseAddress = null)
            => SocketInterop.TryGetLocalIPAddress(this, out ep, reuseAddress);

        public IPEndPointStruct GetPeerIPAddress()
        {
            IPEndPointStruct ep;
            TryGetPeerIPAddress(out ep)
                .ThrowOnError();
            return ep;
        }

        public unsafe PosixResult TryGetPeerIPAddress(out IPEndPointStruct ep)
            => SocketInterop.TryGetPeerIPAddress(this, out ep);

        private static void ValidateSegment(ArraySegment<byte> segment)
        {
            // ArraySegment<byte> is not nullable.
            if (segment.Array == null)
            {
                throw new ArgumentNullException(nameof(segment));
            }

            // Length zero is explicitly allowed
            if (segment.Offset < 0 || segment.Count < 0 || segment.Count > (segment.Array.Length - segment.Offset))
            {
                throw new ArgumentOutOfRangeException(nameof(segment));
            }
        }

        public static SocketPair CreatePair(int domain, int type, int protocol, bool blocking)
        {
            int socket1;
            int socket2;
            var result = SocketInterop.SocketPair(domain, type, protocol, blocking, out socket1, out socket2);
            return new SocketPair { Socket1 = socket1, Socket2 = socket2 };
        }

        public unsafe PosixResult TryReceiveSocket(out Socket socket, bool blocking)
        {
            return SocketInterop.ReceiveSocket(this, out socket, blocking);
        }

        public unsafe PosixResult TryAcceptAndSendHandleTo(int toSocket)
        {
            return SocketInterop.AcceptAndSendHandleTo(this, toSocket);
        }
    }
}