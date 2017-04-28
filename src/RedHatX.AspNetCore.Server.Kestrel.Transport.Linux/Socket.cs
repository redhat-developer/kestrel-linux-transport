using System;
using System.Net;
using System.Runtime.InteropServices;
using Tmds.Posix;

namespace RedHatX.AspNetCore.Server.Kestrel.Transport.Linux
{
    unsafe struct IOVector
    {
        public void* Base;
        public void* Count;
    }

    static class SocketInterop
    {
        [DllImportAttribute(Interop.Library, EntryPoint = "RHXKL_Socket")]
        public static extern unsafe PosixResult Socket(AddressFamily addressFamily, SocketType socketType, ProtocolType protocolType, bool blocking, out Socket socket);

        [DllImportAttribute(Interop.Library, EntryPoint = "RHXKL_GetAvailableBytes")]
        public static extern PosixResult GetAvailableBytes(Socket socket);

        [DllImportAttribute(Interop.Library, EntryPoint = "RHXKL_Bind")]
        public static extern unsafe PosixResult Bind(Socket socket, byte* addr, int addrlen);

        [DllImportAttribute(Interop.Library, EntryPoint = "RHXKL_Connect")]
        public static extern unsafe PosixResult Connect(Socket socket, byte* addr, int addrlen);

        [DllImportAttribute(Interop.Library, EntryPoint = "RHXKL_Listen")]
        public static extern PosixResult Listen(Socket socket, int backlog);

        [DllImportAttribute(Interop.Library, EntryPoint = "RHXKL_Accept")]
        public static unsafe extern PosixResult Accept(Socket socket, byte* socketAddress, int socketAddressLen, bool blocking, out Socket clientSocket);

        [DllImportAttribute(Interop.Library, EntryPoint = "RHXKL_Shutdown")]
        public static extern PosixResult Shutdown(Socket socket, SocketShutdown shutdown);

        [DllImportAttribute(Interop.Library, EntryPoint = "RHXKL_Send")]
        public static extern unsafe PosixResult Send(int socket, IOVector* ioVectors, int ioVectorLen);
        public static unsafe PosixResult Send(SafeHandle socket, IOVector* ioVectors, int ioVectorLen)
        => Send(socket.DangerousGetHandle().ToInt32(), ioVectors, ioVectorLen);

        [DllImportAttribute(Interop.Library, EntryPoint = "RHXKL_Receive")]
        public static unsafe extern PosixResult Receive(int socket, IOVector* ioVectors, int ioVectorLen);
        public static unsafe PosixResult Receive(SafeHandle socket, IOVector* ioVectors, int ioVectorLen)
        => Receive(socket.DangerousGetHandle().ToInt32(), ioVectors, ioVectorLen);

        [DllImportAttribute(Interop.Library, EntryPoint = "RHXKL_SetSockOpt")]
        public static extern unsafe PosixResult SetSockOpt(SafeHandle socket, SocketOptionLevel optionLevel, SocketOptionName optionName, byte* optionValue, int optionLen);

        [DllImportAttribute(Interop.Library, EntryPoint = "RHXKL_GetSockOpt")]
        public static extern unsafe PosixResult GetSockOpt(SafeHandle socket, SocketOptionLevel optionLevel, SocketOptionName optionName, byte* optionValue, int* optionLen);

        [DllImportAttribute(Interop.Library, EntryPoint = "RHXKL_GetPeerName")]
        public static extern unsafe PosixResult GetPeerName(Socket socket, byte* addr, int addrlen);

        [DllImportAttribute(Interop.Library, EntryPoint = "RHXKL_GetSockName")]
        public static extern unsafe PosixResult GetSockName(Socket socket, byte* addr, int addrlen);

        [DllImportAttribute(Interop.Library, EntryPoint = "RHXKL_Duplicate")]
        public static extern PosixResult Duplicate(Socket socket, out Socket dup);
    }

    // Warning: Some operations use DangerousGetHandle for increased performance
    class Socket : CloseSafeHandle
    {
        private Socket()
        {}

        public static Socket Create(AddressFamily addressFamily, SocketType socketType, ProtocolType protocolType, bool blocking)
        {
            Socket socket;
            var result = SocketInterop.Socket(addressFamily, socketType, protocolType, blocking, out socket);
            result.ThrowOnError();
            return socket;
        }

        public int GetAvailableBytes()
        {
            var result = TryGetAvailableBytes();
            result.ThrowOnError();
            return result.Value;
        }

        public PosixResult TryGetAvailableBytes()
        {
            return SocketInterop.GetAvailableBytes(this);
        }

        public void Bind(IPEndPoint endpoint)
        {
            TryBind(endpoint)
                .ThrowOnError();
        }

        public unsafe PosixResult TryBind(IPEndPoint endpoint)
        {
            IPSocketAddress socketAddress = new IPSocketAddress(endpoint);
            return SocketInterop.Bind(this, (byte*)&socketAddress, sizeof(IPSocketAddress));
        }

        public void Connect(IPEndPoint endpoint)
        {
            TryConnect(endpoint)
                .ThrowOnError();
        }

        public unsafe PosixResult TryConnect(IPEndPoint endpoint)
        {
            IPSocketAddress socketAddress = new IPSocketAddress(endpoint);
            return SocketInterop.Connect(this, (byte*)&socketAddress, sizeof(IPSocketAddress));
        }

        public void Listen(int backlog)
        {
            TryListen(backlog)
                .ThrowOnError();
        }

        public PosixResult TryListen(int backlog)
        {
            return SocketInterop.Listen(this, backlog);
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
            return SocketInterop.Accept(this, null, 0, blocking, out clientSocket);
        }

        public int Receive(ArraySegment<byte> buffer)
        {
            var result = TryReceive(buffer);
            result.ThrowOnError();
            return result.Value;
        }

        public unsafe PosixResult TryReceive(ArraySegment<byte> buffer)
        {
            ValidateSegment(buffer);
            fixed (byte* buf = buffer.Array)
            {
                IOVector ioVector = new IOVector() { Base = buf + buffer.Offset, Count = (void*)buffer.Count };
                return SocketInterop.Receive(this, &ioVector, 1);
            }
        }

        public unsafe int Receive(IOVector* ioVectors, int ioVectorLen)
        {
            var result = TryReceive(ioVectors, ioVectorLen);
            result.ThrowOnError();
            return result.Value;
        }

        public unsafe PosixResult TryReceive(IOVector* ioVectors, int ioVectorLen)
        {
            return SocketInterop.Receive(this, ioVectors, ioVectorLen);
        }

        public void Shutdown(SocketShutdown shutdown)
        {
            TryShutdown(shutdown)
                .ThrowOnError();
        }

        public PosixResult TryShutdown(SocketShutdown shutdown)
        {
            return SocketInterop.Shutdown(this, shutdown);
        }

        public int Send(ArraySegment<byte> buffer)
        {
            var result = TrySend(buffer);
            result.ThrowOnError();
            return result.Value;
        }

        public unsafe PosixResult TrySend(ArraySegment<byte> buffer)
        {
            ValidateSegment(buffer);
            fixed (byte* buf = buffer.Array)
            {
                IOVector ioVector = new IOVector() { Base = buf + buffer.Offset, Count = (void*)buffer.Count };
                return SocketInterop.Send(this, &ioVector, 1);
            }
        }

        public unsafe int Send(IOVector* ioVectors, int ioVectorLen)
        {
            var result = TrySend(ioVectors, ioVectorLen);
            result.ThrowOnError();
            return result.Value;
        }

        public unsafe PosixResult TrySend(IOVector* ioVectors, int ioVectorLen)
        {
            return SocketInterop.Send(this, ioVectors, ioVectorLen);
        }

        public void SetSocketOption(SocketOptionLevel optionLevel, SocketOptionName optionName, int value)
        {
            TrySetSocketOption(optionLevel, optionName, value)
                .ThrowOnError();
        }

        public unsafe PosixResult TrySetSocketOption(SocketOptionLevel optionLevel, SocketOptionName optionName, int value)
        {
            return SocketInterop.SetSockOpt(this, optionLevel, optionName, (byte*)&value, 4);
        }

        // TODO: rename to GetSocketOptionInt
        public int GetSocketOption(SocketOptionLevel optionLevel, SocketOptionName optionName)
        {
            int value = 0;
            var result = TryGetSocketOption(optionLevel, optionName, ref value);
            result.ThrowOnError();
            return value;
        }

        public unsafe PosixResult TryGetSocketOption(SocketOptionLevel optionLevel, SocketOptionName optionName, ref int value)
        {
            int v = 0;
            int length = 4;
            var rv = SocketInterop.GetSockOpt(this, optionLevel, optionName, (byte*)&v, &length);
            if (rv.IsSuccess)
            {
                value = v;
            }
            return rv;
        }

        public IPEndPoint GetLocalIPAddress()
        {
            IPEndPoint ep;
            TryGetLocalIPAddress(out ep)
                .ThrowOnError();
            return ep;
        }

        public unsafe PosixResult TryGetLocalIPAddress(out IPEndPoint ep)
        {
            IPSocketAddress socketAddress;
            var rv = SocketInterop.GetSockName(this, (byte*)&socketAddress, sizeof(IPSocketAddress));
            if (rv.IsSuccess)
            {
                ep = socketAddress.ToIPEndPoint();
            }
            else
            {
                ep = null;
            }
            return rv;
        }

        public IPEndPoint GetPeerIPAddress()
        {
            IPEndPoint ep;
            TryGetPeerIPAddress(out ep)
                .ThrowOnError();
            return ep;
        }

        public unsafe PosixResult TryGetPeerIPAddress(out IPEndPoint ep)
        {
            IPSocketAddress socketAddress;
            var rv = SocketInterop.GetPeerName(this, (byte*)&socketAddress, sizeof(IPSocketAddress));
            if (rv.IsSuccess)
            {
                ep = socketAddress.ToIPEndPoint();
            }
            else
            {
                ep = null;
            }
            return rv;
        }

        public Socket Duplicate()
        {
            Socket dup;
            var rv = TryDuplicate(out dup);
            rv.ThrowOnError();
            return dup;
        }

        public PosixResult TryDuplicate(out Socket dup)
        {
            return SocketInterop.Duplicate(this, out dup);
        }

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
    }
}