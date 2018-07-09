using System;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Abstractions.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace RedHat.AspNetCore.Server.Kestrel.Transport.Linux
{
    public class LinuxTransportFactory : ITransportFactory
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

        public ITransport Create(IEndPointInformation IEndPointInformation, IConnectionDispatcher handler)
        {
            return new Transport(IEndPointInformation, handler, _options, _loggerFactory);
        }
    }
}