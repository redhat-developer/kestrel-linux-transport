using System;
using System.Net;
using System.Text;

namespace RedHat.AspNetCore.Server.Kestrel.Transport.Linux
{
    unsafe struct IPSocketAddress
    {
        private short     _family;
        public ushort     Port;
        public fixed byte Address[16];
        public uint       FlowInfo;
        public uint       ScopeId;

        public AddressFamily Family
        {
            get { return (AddressFamily)_family; }
            set { _family = (short)value; }
        }

        public unsafe IPSocketAddress(IPEndPointStruct endPoint)
        {
            bool ipv4 = endPoint.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork;
            FlowInfo = 0;
            _family = (short)(ipv4 ? AddressFamily.InterNetwork : AddressFamily.InterNetworkV6);
            ScopeId = ipv4 ? 0 : (uint)endPoint.Address.ScopeId;
            Port = (ushort)endPoint.Port;
            var bytes = endPoint.Address.GetAddressBytes();
            fixed (byte* address = Address)
            {
                for (int i = 0; i < (ipv4 ? 4 : 16); i++)
                {
                    address[i] = bytes[i];
                }
            }
        }

        public IPEndPointStruct ToIPEndPoint(IPAddress reuseAddress = null)
        {
            if (Family == AddressFamily.InterNetwork)
            {
                long value;
                fixed (byte* address = Address)
                {
                    value = ((address[3] << 24 | address[2] << 16 | address[1] << 8 | address[0]) & 0x0FFFFFFFF);
                }
                bool matchesReuseAddress = reuseAddress != null && reuseAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && reuseAddress.Address == value;
                return new IPEndPointStruct(matchesReuseAddress ? reuseAddress : new IPAddress(value), Port);
            }
            else if (Family == AddressFamily.InterNetworkV6)
            {
                // We can't check if we can use reuseAddress without allocating.
                const int length = 16;
                var bytes = new byte[length];
                fixed (byte* address = Address)
                {
                    for (int i = 0; i < length; i++)
                    {
                        bytes[i] = address[i];
                    }
                }
                return new IPEndPointStruct(new IPAddress(bytes, ScopeId), Port);
            }
            else
            {
                var result = new PosixResult(PosixResult.EAFNOSUPPORT);
                throw result.AsException();
            }
        }
    }

    unsafe struct UnixSocketAddress
    {
        private const int PathLength = 108;
        public unsafe UnixSocketAddress(string path)
        {
            _family = (short)AddressFamily.Unix;
            var bytes = Encoding.UTF8.GetBytes(path);
            int length = Math.Min(bytes.Length, PathLength);
            fixed (byte* pathBytes = Path)
            {
                for (int i = 0; i < length; i++)
                {
                    pathBytes[i] = bytes[i];
                }
            }
            Length = (ushort)length;
        }

        private short     _family;
        public AddressFamily Family
        {
            get { return (AddressFamily)_family; }
            set { _family = (short)value; }
        }
        private fixed byte Path[PathLength];
        public ushort      Length;
    }
}