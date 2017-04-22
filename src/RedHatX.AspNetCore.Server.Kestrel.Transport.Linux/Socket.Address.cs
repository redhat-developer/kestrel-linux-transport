using System;
using System.Net;
using Tmds.Posix;

namespace RedHatX.AspNetCore.Server.Kestrel.Transport.Linux
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

        public unsafe IPSocketAddress(IPEndPoint endPoint)
        {
            if (endPoint == null)
            {
                throw new ArgumentNullException(nameof(endPoint));
            }
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

        public IPEndPoint ToIPEndPoint()
        {
            if (Family == AddressFamily.InterNetwork)
            {
                long value;
                fixed (byte* address = Address)
                {
                    value = ((address[3] << 24 | address[2] << 16 | address[1] << 8 | address[0]) & 0x0FFFFFFFF);
                }
                return new IPEndPoint(new IPAddress(value), Port);
            }
            else if (Family == AddressFamily.InterNetworkV6)
            {
                const int length = 16;
                var bytes = new byte[length];
                fixed (byte* address = Address)
                {
                    for (int i = 0; i < length; i++)
                    {
                        bytes[i] = address[i];
                    }
                }
                return new IPEndPoint(new IPAddress(bytes, ScopeId), Port);
            }
            else
            {
                throw new PosixException(PosixResult.EAFNOSUPPORT);
            }
        }
    }

    unsafe struct UnixSocketAddress
    {
        private short     _family;
        public AddressFamily Family
        {
            get { return (AddressFamily)_family; }
            set { _family = (short)value; }
        }
        private fixed byte Path[108];
        public ushort      Length;
    }
}