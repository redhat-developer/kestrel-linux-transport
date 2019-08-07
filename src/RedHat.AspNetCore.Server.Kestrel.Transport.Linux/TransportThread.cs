using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Logging;
using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace RedHat.AspNetCore.Server.Kestrel.Transport.Linux
{
    sealed partial class TransportThread : ITransportActionHandler
    {
        private readonly object _gate = new object();
        private TransportThreadState _state;
        private TaskCompletionSource<object> _stateChangeCompletion;
        private Thread _thread;
        private ThreadContext _threadContext;

        public int ThreadId { get; }
        public IPEndPoint EndPoint { get; }
        public LinuxTransportOptions TransportOptions { get; }
        public AcceptThread AcceptThread { get; }
        public ILoggerFactory LoggerFactory { get; }
        public int CpuId { get; }

        public TransportThread(IPEndPoint endPoint, LinuxTransportOptions options, AcceptThread acceptThread, int threadId, int cpuId, ILoggerFactory loggerFactory)
        {
            ThreadId = threadId;
            CpuId = cpuId;
            EndPoint = endPoint;
            TransportOptions = options;
            AcceptThread = acceptThread;
            LoggerFactory = loggerFactory;
        }

        public Task BindAsync()
        {
            TaskCompletionSource<object> tcs;
            lock (_gate)
            {
                if (_state == TransportThreadState.Started)
                {
                    return Task.CompletedTask;
                }
                else if (_state == TransportThreadState.Starting)
                {
                    return _stateChangeCompletion.Task;
                }
                else if (_state != TransportThreadState.Initial)
                {
                    ThrowInvalidState();
                }
                try
                {
                    tcs = _stateChangeCompletion = new TaskCompletionSource<object>();
                    _state = TransportThreadState.Starting;

                    _thread = new Thread(PollThread);
                    _thread.IsBackground = true;
                    _thread.Start();
                }
                catch
                {
                    _state = TransportThreadState.Stopped;
                    throw;
                }
            }
            return tcs.Task;
        }

        public async Task UnbindAsync()
        {
            TaskCompletionSource<object> tcs = null;
            lock (_gate)
            {
                if (_state == TransportThreadState.Initial)
                {
                    _state = TransportThreadState.Stopped;
                    return;
                }
                else if (_state == TransportThreadState.AcceptClosed || _state == TransportThreadState.Stopping || _state == TransportThreadState.Stopped)
                {
                    return;
                }
                else if (_state == TransportThreadState.ClosingAccept)
                {
                    tcs = _stateChangeCompletion;
                }
            }
            if (tcs != null)
            {
                await tcs.Task;
                return;
            }
            try
            {
                await BindAsync();
            }
            catch
            {}
            bool triggerStateChange = false;
            lock (_gate)
            {
                if (_state == TransportThreadState.AcceptClosed || _state == TransportThreadState.Stopping || _state == TransportThreadState.Stopped)
                {
                    return;
                }
                else if (_state == TransportThreadState.ClosingAccept)
                {
                    tcs = _stateChangeCompletion;
                }
                else if (_state == TransportThreadState.Started)
                {
                    triggerStateChange = true;
                    tcs = _stateChangeCompletion = new TaskCompletionSource<object>();
                    _state = TransportThreadState.ClosingAccept;
                }
                else
                {
                    // Cannot happen
                    ThrowInvalidState();
                }
            }
            if (triggerStateChange)
            {
                _threadContext.RequestCloseAccept();
            }
            await tcs.Task;
        }

        public async Task StopAsync()
        {
            lock (_gate)
            {
                if (_state == TransportThreadState.Initial)
                {
                    _state = TransportThreadState.Stopped;
                    return;
                }
            }

            await UnbindAsync();

            TaskCompletionSource<object> tcs = null;
            bool triggerStateChange = false;
            lock (_gate)
            {
                if (_state == TransportThreadState.Stopped)
                {
                    return;
                }
                else if (_state == TransportThreadState.Stopping)
                {
                    tcs = _stateChangeCompletion;
                }
                else if (_state == TransportThreadState.AcceptClosed)
                {
                    tcs = _stateChangeCompletion = new TaskCompletionSource<object>();
                    _state = TransportThreadState.Stopping;
                    triggerStateChange = true;
                }
                else
                {
                    // Cannot happen
                    ThrowInvalidState();
                }
            }
            if (triggerStateChange)
            {
                _threadContext.RequestStopSockets();
            }
            await tcs.Task;
        }

        private unsafe void PollThread(object obj)
        {
            if (CpuId != -1)
            {
                SystemScheduler.SetCurrentThreadAffinity(CpuId);
            }

            using (ThreadContext context = new ThreadContext(this))
            {
                _threadContext = context;
                context.Run();
            }
        }

        private void ThrowInvalidState()
        {
            throw new InvalidOperationException($"nameof(TransportThread) is {_state}");
        }

        private void CompleteStateChange(TransportThreadState state, Exception error)
        {
            TaskCompletionSource<object> tcs;
            lock (_gate)
            {
                tcs = _stateChangeCompletion;
                _stateChangeCompletion = null;
                _state = state;
            }
            ThreadPool.QueueUserWorkItem(o =>
            {
                if (error != null)
                {
                    tcs?.SetException(error);
                }
                else
                {
                    tcs?.SetResult(null);
                }
            });
        }

        public async ValueTask<ConnectionContext> AcceptAsync(CancellationToken cancellationToken = default)
        {
            ValueTask<TSocket> acceptTask;

            lock (_gate)
            {
                if (_state > TransportThreadState.Started)
                {
                    return null;
                }
                else if (_state != TransportThreadState.Started)
                {
                    ThrowInvalidState();
                }

                acceptTask = _threadContext.AcceptAsync(cancellationToken);
            }

            return await acceptTask;
        }
    }
}
