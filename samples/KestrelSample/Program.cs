using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Microsoft.AspNetCore.Server.Kestrel.Core.Internal;
using Benchmarks.Middleware;
using Tmds.Kestrel.Linux;
using System.Linq;

namespace SampleApp
{
    public class Startup
    {
        IConfiguration Configuration;
        static bool _log;
        public Startup()
        {
            var configBuilder = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false);
            Configuration = configBuilder.Build();
        }
        public void Configure(IApplicationBuilder app, ILoggerFactory loggerFactory)
        {
            if (_log)
            {
                loggerFactory.AddConsole(Configuration.GetSection("Logging"));
            }
            app.UsePlainText();
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
            TaskScheduler.UnobservedTaskException += (sender, e) =>
            {
                Console.WriteLine("Unobserved exception: {0}", e.Exception);
            };

            bool libuv = args.Contains("libuv");
            bool ta = args.Contains("ta");
            bool ic = args.Contains("ic");
            bool da = args.Contains("da");
            bool notp = args.Contains("notp");
            _log = args.Contains("log");
            int threadCount = 0;
            if (args.Length ==0 || !int.TryParse(args[args.Length -1], out threadCount))
            {
                threadCount = Environment.ProcessorCount << 1;
            }

            var hostBuilder = new WebHostBuilder()
                .UseKestrel()
                .UseStartup<Startup>();

            if (libuv)
            {
                System.Console.WriteLine($"Using Libuv, ThreadCount={threadCount}");
                hostBuilder = hostBuilder.UseLibuv(options => options.ThreadCount = threadCount);
            }
            else
            {
                System.Console.WriteLine($"Using Linux Transport, ThreadCount={threadCount}");
                hostBuilder = hostBuilder.UseLinuxTransport(options =>
                {
                    options.ThreadCount = threadCount;
                    options.SetThreadAffinity = ta;
                    options.ReceiveOnIncomingCpu = ic;
                    options.DeferAccept = da;
                });
            }

            var host = hostBuilder.Build();

            if (notp)
            {
                System.Console.WriteLine("ThreadPoolDispatching disabled");
                host.ServerFeatures.Get<InternalKestrelServerOptions>().ThreadPoolDispatching = false;
            }

            host.Run();
        }
    }
}