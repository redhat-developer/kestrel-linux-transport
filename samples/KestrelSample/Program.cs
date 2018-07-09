using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Abstractions.Internal;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Microsoft.AspNetCore.Server.Kestrel.Core.Internal;
using Benchmarks.Middleware;
using RedHat.AspNetCore.Server.Kestrel.Transport.Linux;

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
                .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false);
            Configuration = configBuilder.Build();
        }

        public void ConfigureServices(IServiceCollection services)
        {
            if (_log)
            {
                services.AddLogging(builder =>
                {
                    builder.AddConfiguration(Configuration.GetSection("Logging"))
                        .SetMinimumLevel(LogLevel.Debug)
                        .AddConsole();
                });
            }
        }

        public void Configure(IApplicationBuilder app)
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
            if (args.Contains("--help"))
            {
                Console.WriteLine("Options: [libuv] [-c<cpuset>] [-t<threadcount>] [ta] [ic] [noda] [nott]");
                Console.WriteLine("  General:");
                Console.WriteLine("\tlibuv    Use libuv Transport instead of Linux Transport");
                Console.WriteLine("\tsock     Use Sockets Transport instead of Linux Transport");
                Console.WriteLine("\t-t<tc>   Number of transport threads");
                Console.WriteLine("\t-z<th>   Threshold for using zero-copy");
                Console.WriteLine("\tnott     Defer requests to thread pool");
                Console.WriteLine("  Linux transport specific:");
                // Console.WriteLine("\tta       Set thread affinity");
                // Console.WriteLine("\tic       Receive on incoming cpu (implies ta)");
                // Console.WriteLine("\t-c<cpus> Cpus for transport threads (implies ta, count = default for -t)");
                Console.WriteLine("\tnoda     No deferred accept");
                Console.WriteLine("\tnods     No deferred send");
                Console.WriteLine("\taior     Receive using Linux aio");
                Console.WriteLine("\taios     Send using Linux aio");
                return;
            }

            TaskScheduler.UnobservedTaskException += (sender, e) =>
            {
                Console.WriteLine("Unobserved exception: {0}", e.Exception);
            };

            bool libuv = args.Contains("libuv");
            bool sock = args.Contains("sock");
            // bool ta = args.Contains("ta");
            // bool ic = args.Contains("ic");
            bool ds = !args.Contains("nods");
            bool da = !args.Contains("noda");
            bool tt = !args.Contains("nott");
            bool aior = args.Contains("aior");
            bool aios = args.Contains("aios");
            _log = args.Contains("log");
            int threadCount = 0;
            int zeroCopyThreshold = LinuxTransportOptions.NoZeroCopy;
            // CpuSet cpuSet = default(CpuSet);
            foreach (var arg in args)
            {
                // if (arg.StartsWith("-c"))
                // {
                //     cpuSet = CpuSet.Parse(arg.Substring(2));
                //     ta = true;
                // }
                //else
                if (arg.StartsWith("-t"))
                {
                    threadCount = int.Parse(arg.Substring(2));
                }
                else if (arg.StartsWith("-z"))
                {
                    zeroCopyThreshold = int.Parse(arg.Substring(2));
                }
            }
            // if (ic)
            // {
            //     ta = true;
            // }
            if (threadCount == 0)
            {
                // threadCount = (libuv || cpuSet.IsEmpty) ? Environment.ProcessorCount : cpuSet.Cpus.Length;
                threadCount = Environment.ProcessorCount;
            }

            Console.WriteLine($"Server GC is {(GCSettings.IsServerGC ? "enabled" : "disabled")}");
            if (libuv)
            {
                Console.WriteLine($"Using Libuv: ThreadCount={threadCount}, UseTransportThread={tt}");
            }
            else if (sock)
            {
                System.Console.WriteLine($"Using Sockets: IOQueueCount={threadCount}");
            }
            else
            {
                // Console.WriteLine($"Using Linux Transport: Cpus={cpuSet}, ThreadCount={threadCount}, IncomingCpu={ic}, SetThreadAffinity={ta}, DeferAccept={da}, UseTransportThread={tt}");
                Console.WriteLine($"Using Linux Transport: ThreadCount={threadCount}, DeferAccept={da}, UseTransportThread={tt}, ZeroCopyThreshold={zeroCopyThreshold}, DeferSend={ds}");
            }

            var hostBuilder = new WebHostBuilder()
                .UseKestrel(options =>
                {
                    options.ApplicationSchedulingMode = tt ? SchedulingMode.Inline : SchedulingMode.ThreadPool;
                })
                .UseStartup<Startup>();

            if (libuv)
            {
                hostBuilder = hostBuilder.UseLibuv(options => options.ThreadCount = threadCount);
            }
            else if (sock)
            {
                hostBuilder = hostBuilder.UseSockets(options => options.IOQueueCount = threadCount);
            }
            else
            {
                hostBuilder = hostBuilder.UseLinuxTransport(options =>
                {
                    options.ThreadCount = threadCount;
                    //options.SetThreadAffinity = ta;
                    //options.ReceiveOnIncomingCpu = ic;
                    options.DeferAccept = da;
                    options.DeferSend = ds;
                    options.ZeroCopyThreshold = zeroCopyThreshold;
                    options.ZeroCopy = true;
                    options.AioReceive = aior;
                    options.AioSend = aios;
                    //options.CpuSet = cpuSet;
                });
            }

            var host = hostBuilder.Build();
            host.Run();
        }
    }
}
