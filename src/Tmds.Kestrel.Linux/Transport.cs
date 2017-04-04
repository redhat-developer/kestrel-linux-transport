using System;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Server.Kestrel;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Abstractions;
using Microsoft.Extensions.Logging;

namespace Tmds.Kestrel.Linux
{
    public class Transport : ITransport
    {
        private static readonly TransportThread[] EmptyThreads = Array.Empty<TransportThread>();
        private IPEndPoint[] _listenEndPoints;
        private IConnectionHandler _connectionHandler;
        private TransportThread[] _threads;
        private TransportOptions _transportOptions;
        private ILogger _logger;

        public Transport(IEndPointInformation IEndPointInformation, IConnectionHandler connectionHandler, TransportOptions transportOptions, ILogger logger) :
            this(CreateEndPointsFromIEndPointInformation(IEndPointInformation), connectionHandler, transportOptions, logger)
        {}

        private static IPEndPoint[] CreateEndPointsFromIEndPointInformation(IEndPointInformation IEndPointInformation)
        {
            if (IEndPointInformation == null)
            {
                throw new ArgumentNullException(nameof(IEndPointInformation));
            }
            if (IEndPointInformation.Type != ListenType.IPEndPoint)
            {
                throw new NotSupportedException("Only IPEndPoints are supported.");
            }
            if (IEndPointInformation.IPEndPoint == null)
            {
                throw new ArgumentNullException(nameof(IEndPointInformation.IPEndPoint));
            }

            return new IPEndPoint[1] { IEndPointInformation.IPEndPoint };
        }

        public Transport(IPEndPoint[] listenEndPoints, IConnectionHandler connectionHandler, TransportOptions transportOptions, ILogger logger)
        {
            if (listenEndPoints == null)
            {
                throw new ArgumentNullException(nameof(listenEndPoints));
            }
            if (connectionHandler == null)
            {
                throw new ArgumentNullException(nameof(connectionHandler));
            }
            if (transportOptions == null)
            {
                throw new ArgumentException(nameof(transportOptions));
            }
            if (logger == null)
            {
                throw new ArgumentException(nameof(logger));
            }
            if (listenEndPoints.Length < 1)
            {
                throw new ArgumentException(nameof(listenEndPoints));
            }
            foreach (var listenEndPoint in listenEndPoints)
            {
                if (listenEndPoint == null)
                {
                    throw new ArgumentException(nameof(listenEndPoints));
                }
            }

            _listenEndPoints = listenEndPoints;
            _connectionHandler = connectionHandler;
            _transportOptions = transportOptions;
            _logger = logger;
        }

        public async Task BindAsync()
        {
            var threads = CreateTransportThreads();
            var original = Interlocked.CompareExchange(ref _threads, threads, null);
            ThrowIfInvalidState(state: original, starting: true);

            IPEndPoint[] endPoints = Interlocked.Exchange(ref _listenEndPoints, null);
            if (endPoints == null)
            {
                throw new InvalidOperationException("Already bound");
            }
            _logger.LogInformation($@"BindAsync TC:{_transportOptions.ThreadCount} TA:{_transportOptions.SetThreadAffinity} IC:{_transportOptions.ReceiveOnIncomingCpu} DA:{_transportOptions.DeferAccept} CW:{_transportOptions.CoalesceWrites}");

            var tasks = new Task[threads.Length];
            for (int i = 0; i < threads.Length; i++)
            {
                tasks[i] = threads[i].StartAsync();
            }
            try
            {
                await Task.WhenAll(tasks);
                for (int i = 0; i < threads.Length; i++)
                {
                    foreach (var listenEndPoint in endPoints)
                    {
                        threads[i].AcceptOn(listenEndPoint);
                    }
                }
            }
            catch
            {
                await StopAsync();
                throw;
            }
        }

        private static int s_threadId = 0;

        private TransportThread[] CreateTransportThreads()
        {
            var threads = new TransportThread[_transportOptions.ThreadCount];
            List<int> preferredCpuIds = null;
            if (_transportOptions.SetThreadAffinity)
            {
                preferredCpuIds = GetPreferredCpuIds();
            }
            int cpuIdx = 0;
            for (int i = 0; i < _transportOptions.ThreadCount; i++)
            {
                int cpuId = preferredCpuIds == null ? -1 : preferredCpuIds[cpuIdx++ % preferredCpuIds.Count];
                int threadId = Interlocked.Increment(ref s_threadId);
                var thread = new TransportThread(_connectionHandler, _transportOptions, threadId, cpuId, _logger);
                threads[i] = thread;
            }
            return threads;
        }

        private List<int> GetPreferredCpuIds()
        {
            var ids = new List<int>();
            bool found = true;
            int level = 0;
            do
            {
                found = false;
                foreach (var socket in CpuInfo.GetSockets())
                {
                    var cores = CpuInfo.GetCores(socket);
                    foreach (var core in cores)
                    {
                        var cpuIdIterator = CpuInfo.GetCpuIds(socket, core).GetEnumerator();
                        int d = 0;
                        while (cpuIdIterator.MoveNext())
                        {
                            if (d++ == level)
                            {
                                ids.Add(cpuIdIterator.Current);
                                found = true;
                                break;
                            }
                        }
                    }
                }
                level++;
            } while (found && ids.Count < _transportOptions.ThreadCount);
            return ids;
        }

        public Task UnbindAsync()
        {
            var threads = Volatile.Read(ref _threads);
            ThrowIfInvalidState(state: threads, starting: false);
            var tasks = new Task[threads.Length];
            for (int i = 0; i < threads.Length; i++)
            {
                tasks[i] = threads[i].CloseAcceptAsync();
            }
            return Task.WhenAll(tasks);
        }

        public Task StopAsync()
        {
            var threads = Volatile.Read(ref _threads);
            ThrowIfInvalidState(state: threads, starting: false);
            var tasks = new Task[threads.Length];
            for (int i = 0; i < threads.Length; i++)
            {
                tasks[i] = threads[i].StopAsync();
            }
            return Task.WhenAll(tasks);
        }

        public void Dispose()
        {
            var threads = Interlocked.Exchange(ref _threads, EmptyThreads);
            if (threads.Length == 0)
            {
                return;
            }
            var tasks = new Task[threads.Length];
            for (int i = 0; i < threads.Length; i++)
            {
                tasks[i] = threads[i].StopAsync();
            }
            try
            {
                Task.WaitAll(tasks);
            }
            finally
            {}
        }

        private void ThrowIfInvalidState(TransportThread[] state, bool starting)
        {
            if (state == EmptyThreads)
            {
                throw new ObjectDisposedException(nameof(Transport));
            }
            else if (state == null && !starting)
            {
                throw new InvalidOperationException("Not started");
            }
            else if (state != null && starting)
            {
                throw new InvalidOperationException("Already starting");
            }
        }

        // TODO: We'd like Kestrel to use these values for MaximumSize{Low,Heigh} but the abstraction
        //       doesn't support it.
        public static PipeOptions InputPipeOptions = new PipeOptions()
        {
            // Would be nice if we could set this to 1 to limit prefetching to a single receive
            // but we can't: https://github.com/dotnet/corefxlab/issues/1355
            MaximumSizeHigh = 2000,
            MaximumSizeLow =  2000,
            WriterScheduler = InlineScheduler.Default,
            ReaderScheduler = InlineScheduler.Default,
        };

        public static PipeOptions OutputPipeOptions = new PipeOptions()
        {
            // Buffer as much as we can send in a single system call
            // Wait until everything is sent.
            MaximumSizeHigh = TransportThread.MaxSendLength,
            MaximumSizeLow = 1,
            WriterScheduler = InlineScheduler.Default,
            ReaderScheduler = InlineScheduler.Default,
        };
    }
}