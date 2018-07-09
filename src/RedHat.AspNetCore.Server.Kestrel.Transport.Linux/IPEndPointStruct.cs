using System.Net;

namespace RedHat.AspNetCore.Server.Kestrel.Transport.Linux
{
    internal struct IPEndPointStruct
    {
        public IPEndPointStruct(IPAddress address, int port)
        {
            Address = address;
            Port = port;
        }

        public IPAddress Address { get; set; }

        public int Port { get; set; }

        public System.Net.Sockets.AddressFamily AddressFamily => Address.AddressFamily;

        public static implicit operator IPEndPointStruct(IPEndPoint endPoint)
            => new IPEndPointStruct(endPoint.Address, endPoint.Port);
    }
}