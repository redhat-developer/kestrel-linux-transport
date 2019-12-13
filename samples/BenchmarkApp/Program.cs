using System;
using System.IO;
using System.Net;
using System.Runtime;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Microsoft.AspNetCore.Server.Kestrel.Core.Internal;
using Benchmarks.Middleware;
using RedHat.AspNetCore.Server.Kestrel.Transport.Linux;
using System.Linq;

namespace SampleApp
{
    public class Startup
    {
        public Startup()
        { }

        public void Configure(IApplicationBuilder app, ILoggerFactory loggerFactory)
        {
            app.UsePlainText();
            app.UseJson();
            app.Run(async context =>
            {
                var response = $"hello, world{Environment.NewLine}";
                context.Response.ContentLength = response.Length;
                context.Response.ContentType = "text/plain";
                await context.Response.WriteAsync(response);
            });
        }

        public static void Main(string[] args)
        {
            var hostBuilder = new WebHostBuilder()
                .UseKestrel(options =>
                {
                    options.AllowSynchronousIO = true;
                })
                .UseLinuxTransport()
                .UseStartup<Startup>();

            var host = hostBuilder.Build();
            host.Run();
        }
    }
}
