using Microsoft.AspNetCore.Server.Kestrel;
using Microsoft.AspNetCore.Server.Kestrel.Transport;

namespace Tmds.Kestrel.Linux
{
    public class TransportFactory : ITransportFactory
    {
        public ITransport Create(ListenOptions listenOptions, IConnectionHandler handler)
        {
            return new Transport(listenOptions, handler, new TransportOptions() {});
        }
    }
}