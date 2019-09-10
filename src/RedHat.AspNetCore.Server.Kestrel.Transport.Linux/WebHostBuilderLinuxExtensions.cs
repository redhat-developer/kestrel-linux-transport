using System;
using System.Runtime.InteropServices;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.DependencyInjection;
using RedHat.AspNetCore.Server.Kestrel.Transport.Linux;

namespace Microsoft.AspNetCore.Hosting
{
    public static class WebHostBuilderLinuxTransportExtensions
    {
        public static IWebHostBuilder UseLinuxTransport(this IWebHostBuilder hostBuilder)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                return hostBuilder;
            }
            return hostBuilder.ConfigureServices(services =>
            {
                services.AddSingleton<IConnectionListenerFactory, LinuxTransportFactory>();
            });
        }

        public static IWebHostBuilder UseLinuxTransport(this IWebHostBuilder hostBuilder, Action<LinuxTransportOptions> options)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                return hostBuilder;
            }
            return hostBuilder.UseLinuxTransport().ConfigureServices(services =>
            {
                services.Configure(options);
            });
        }
    }
}