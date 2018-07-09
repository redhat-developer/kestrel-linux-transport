using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Abstractions.Internal;
using Microsoft.Extensions.Logging;

namespace RedHat.AspNetCore.Server.Kestrel.Transport.Linux
{
    internal class Transport : ITransport
    {
        private enum State
        {
            Created,
            Binding,
            Bound,
            Unbinding,
            Unbound,
            Stopping,
            Stopped
        }
        // Kestrel LibuvConstants.ListenBacklog
        private const int ListenBacklog = 128;

        private readonly IEndPointInformation _endPoint;
        private readonly IConnectionDispatcher _connectionDispatcher;
        private readonly LinuxTransportOptions _transportOptions;
        private readonly ILoggerFactory _loggerFactory;
        private readonly ILogger _logger;
        private State _state;
        private readonly object _gate = new object();
        private ITransportActionHandler[] _threads;

        public Transport(IEndPointInformation ipEndPointInformation, IConnectionDispatcher connectionDispatcher, LinuxTransportOptions transportOptions, ILoggerFactory loggerFactory)
        {
            if (connectionDispatcher == null)
            {
                throw new ArgumentNullException(nameof(connectionDispatcher));
            }
            if (transportOptions == null)
            {
                throw new ArgumentException(nameof(transportOptions));
            }
            if (loggerFactory == null)
            {
                throw new ArgumentException(nameof(loggerFactory));
            }
            if (ipEndPointInformation == null)
            {
                throw new ArgumentException(nameof(ipEndPointInformation));
            }

            _endPoint = ipEndPointInformation;
            _connectionDispatcher = connectionDispatcher;
            _transportOptions = transportOptions;
            _loggerFactory = loggerFactory;
            _logger = loggerFactory.CreateLogger<Transport>();
            _threads = Array.Empty<TransportThread>();
        }

        public async Task BindAsync()
        {
            AcceptThread acceptThread;
            TransportThread[] transportThreads;
            lock (_gate)
            {
                if (_state != State.Created)
                {
                    ThrowInvalidOperation();
                }
                _state = State.Binding;

                IPEndPoint ipEndPoint;
                switch (_endPoint.Type)
                {
                    case ListenType.IPEndPoint:
                        ipEndPoint = _endPoint.IPEndPoint;
                        acceptThread = null;
                        transportThreads = CreateTransportThreads(ipEndPoint, acceptThread);
                        break;
                    case ListenType.SocketPath:
                    case ListenType.FileHandle:
                        Socket socket;
                        if (_endPoint.Type == ListenType.SocketPath)
                        {
                            socket = Socket.Create(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified, blocking: false);
                            File.Delete(_endPoint.SocketPath);
                            socket.Bind(_endPoint.SocketPath);
                            socket.Listen(ListenBacklog);
                        }
                        else
                        {
                            socket = new Socket((int)_endPoint.FileHandle);
                        }
                        ipEndPoint = null;
                        acceptThread = new AcceptThread(socket);
                        transportThreads = CreateTransportThreads(ipEndPoint, acceptThread);
                        break;
                    default:
                        throw new NotSupportedException($"Unknown ListenType: {_endPoint.Type}.");
                }

                _threads = new ITransportActionHandler[transportThreads.Length + (acceptThread != null ? 1 : 0)];
                _threads[0] = acceptThread;
                for (int i = 0; i < transportThreads.Length; i++)
                {
                    _threads[i + (acceptThread == null ? 0 : 1)] = transportThreads[i];
                }

                _logger.LogDebug($@"BindAsync {_endPoint}: TC:{_transportOptions.ThreadCount} TA:{_transportOptions.SetThreadAffinity} IC:{_transportOptions.ReceiveOnIncomingCpu} DA:{_transportOptions.DeferAccept}");
            }

            var tasks = new Task[transportThreads.Length];
            for (int i = 0; i < transportThreads.Length; i++)
            {
                tasks[i] = transportThreads[i].BindAsync();
            }
            try
            {
                await Task.WhenAll(tasks);

                if (acceptThread != null)
                {
                    await acceptThread.BindAsync();
                }

                lock (_gate)
                {
                    if (_state == State.Binding)
                    {
                        _state = State.Bound;
                    }
                    else
                    {
                        ThrowInvalidOperation();
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

        private TransportThread[] CreateTransportThreads(IPEndPoint ipEndPoint, AcceptThread acceptThread)
        {
            var threads = new TransportThread[_transportOptions.ThreadCount];
            IList<int> preferredCpuIds = null;
            if (_transportOptions.SetThreadAffinity)
            {
                preferredCpuIds = GetPreferredCpuIds();
            }
            int cpuIdx = 0;
            for (int i = 0; i < _transportOptions.ThreadCount; i++)
            {
                int cpuId = preferredCpuIds == null ? -1 : preferredCpuIds[cpuIdx++ % preferredCpuIds.Count];
                int threadId = Interlocked.Increment(ref s_threadId);
                var thread = new TransportThread(ipEndPoint, _connectionDispatcher, _transportOptions, acceptThread, threadId, cpuId, _loggerFactory);
                threads[i] = thread;
            }
            return threads;
        }

        private IList<int> GetPreferredCpuIds()
        {
            if (!_transportOptions.CpuSet.IsEmpty)
            {
                return _transportOptions.CpuSet.Cpus;
            }
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

        public async Task UnbindAsync()
        {
            lock (_gate)
            {
                if (_state <= State.Unbinding)
                {
                    _state = State.Unbinding;
                }
                else
                {
                    return;
                }
            }
            var tasks = new Task[_threads.Length];
            for (int i = 0; i < _threads.Length; i++)
            {
                tasks[i] = _threads[i].UnbindAsync();
            }
            await Task.WhenAll(tasks);
            lock (_gate)
            {
                if (_state == State.Unbinding)
                {
                    _state = State.Unbound;
                }
                else
                {
                    ThrowInvalidOperation();
                }
            }
        }

        public async Task StopAsync()
        {
            lock (_gate)
            {
                if (_state <= State.Stopping)
                {
                    _state = State.Stopping;
                }
                else
                {
                    return;
                }
            }
            var tasks = new Task[_threads.Length];
            for (int i = 0; i < _threads.Length; i++)
            {
                tasks[i] = _threads[i].StopAsync();
            }
            await Task.WhenAll(tasks);
            lock (_gate)
            {
                if (_state == State.Stopping)
                {
                    _state = State.Stopped;
                }
                else
                {
                    ThrowInvalidOperation();
                }
            }
        }

        private void ThrowInvalidOperation()
        {
            throw new InvalidOperationException($"Invalid operation: {_state}");
        }
    }
}