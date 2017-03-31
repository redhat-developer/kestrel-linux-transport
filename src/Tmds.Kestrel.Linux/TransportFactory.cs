using System;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel;
using Microsoft.AspNetCore.Server.Kestrel.Transport;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Tmds.Kestrel.Linux
{
    public class TransportFactory : ITransportFactory
    {
        private TransportOptions _options;
        private ILogger _logger;
        public TransportFactory(IOptions<TransportOptions> options,
            ILoggerFactory loggerFactory)
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
            _logger = loggerFactory.CreateLogger<Transport>();
        }
        public ITransport Create(ListenOptions listenOptions, IConnectionHandler handler)
        {
            return new Transport(listenOptions, handler, _options, _logger);
        }
    }
}