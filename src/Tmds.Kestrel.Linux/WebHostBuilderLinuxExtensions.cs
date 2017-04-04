using System;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Tmds.Kestrel.Linux;

namespace Microsoft.AspNetCore.Hosting
{
    public static class WebHostBuilderLibuvExtensions
    {
        public static IWebHostBuilder UseLinuxTransport(this IWebHostBuilder hostBuilder)
        {
            return hostBuilder.ConfigureServices(services =>
            {
                services.AddSingleton<ITransportFactory, TransportFactory>();
            });
        }

        public static IWebHostBuilder UseLinuxTransport(this IWebHostBuilder hostBuilder, Action<TransportOptions> options)
        {
            return hostBuilder.UseLinuxTransport().ConfigureServices(services =>
            {
                services.Configure(options);
            });
        }
    }
}