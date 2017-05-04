using System;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using RedHatX.AspNetCore.Server.Kestrel.Transport.Linux;

namespace Microsoft.AspNetCore.Hosting
{
    public static class WebHostBuilderLinuxTransportExtensions
    {
        public static IWebHostBuilder UseLinuxTransport(this IWebHostBuilder hostBuilder)
        {
            return hostBuilder.ConfigureServices(services =>
            {
                services.AddSingleton<ITransportFactory, LinuxTransportFactory>();
            });
        }

        public static IWebHostBuilder UseLinuxTransport(this IWebHostBuilder hostBuilder, Action<LinuxTransportOptions> options)
        {
            return hostBuilder.UseLinuxTransport().ConfigureServices(services =>
            {
                services.Configure(options);
            });
        }
    }
}