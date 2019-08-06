using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace RedHat.AspNetCore.Server.Kestrel.Transport.Linux
{
    public class LinuxTransportFactory : IConnectionListenerFactory
    {
        private LinuxTransportOptions _options;
        private ILoggerFactory _loggerFactory;

        public LinuxTransportFactory(IOptions<LinuxTransportOptions> options, ILoggerFactory loggerFactory)
        {
            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }
            if (loggerFactory == null)
            {
                throw new ArgumentNullException(nameof(loggerFactory));
            }
            _options = options.Value;
            _loggerFactory = loggerFactory;
        }


        public async ValueTask<IConnectionListener> BindAsync(EndPoint endpoint, CancellationToken cancellationToken = default)
        {
            var transport = new Transport(endpoint, _options, _loggerFactory);
            await transport.BindAsync();
            return transport;
        }
    }
}