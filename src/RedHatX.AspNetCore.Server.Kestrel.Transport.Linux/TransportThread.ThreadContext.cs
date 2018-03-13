using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipelines;
using System.Threading;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Abstractions.Internal;
using Microsoft.Extensions.Logging;

namespace RedHatX.AspNetCore.Server.Kestrel.Transport.Linux
{
    sealed partial class TransportThread
    {
        sealed class ThreadContext : PipeScheduler
        {
            public ThreadContext(TransportThread transportThread, LinuxTransportOptions transportOptions, IConnectionHandler connectionHandler, ILogger logger)
            {
                TransportThread = transportThread;
                ConnectionHandler = connectionHandler;

                Sockets = new Dictionary<int, TSocket>();
                Logger = logger;
                AcceptSockets = new List<TSocket>();
                _schedulerAdding = new Queue<ScheduledAction>(1024);
                _schedulerRunning = new Queue<ScheduledAction>(1024);
                _epollState = EPollBlocked;
                SendScheduler = transportOptions.DeferSend ? this as PipeScheduler : PipeScheduler.Inline;
            }

            public void Initialize()
            {
                // These members need to be Disposed
                EPoll = EPoll.Create();
                EPollFd = EPoll.DangerousGetHandle().ToInt32();
                MemoryPool = TransportThread.CreateMemoryPool();
                PipeEnds = PipeEnd.CreatePair(blocking: false);
            }

            public int EPollFd;
            public readonly ILogger Logger;
            public readonly IConnectionHandler ConnectionHandler;
            public PipeEndPair PipeEnds;
            public readonly PipeScheduler SendScheduler;

            public readonly TransportThread TransportThread;
            // key is the file descriptor
            public readonly Dictionary<int, TSocket> Sockets;
            public MemoryPool<byte> MemoryPool;
            public readonly List<TSocket> AcceptSockets;

            private EPoll EPoll;
            private int _epollState;
            private readonly object _schedulerGate = new object();
            private Queue<ScheduledAction> _schedulerAdding;
            private Queue<ScheduledAction> _schedulerRunning;

            public void SetEpollNotBlocked()
            {
                Volatile.Write(ref _epollState, EPollNotBlocked);
            }

            public override void Schedule<TState>(Action<TState> action, TState state)
            {
                // TODO: remove this function
                int epollState;
                lock (_schedulerGate)
                {
                    epollState = Interlocked.CompareExchange(ref _epollState, EPollNotBlocked, EPollBlocked);
                    _schedulerAdding.Enqueue(new ScheduledAction { Callback = action, State = state, CallbackAdapter = CallbackAdapter<TState>.PostCallbackAdapter });
                }
                if (epollState == EPollBlocked)
                {
                    PipeEnds.WriteEnd.WriteByte(PipeActionsPending);
                }
            }

            public void DoScheduledWork()
            {
                var loopsRemaining = 1; // actions may lead to more actions
                bool queueNotEmpty; 
                do
                {
                    Queue<ScheduledAction> queue;
                    lock (_schedulerGate)
                    {
                        queue = _schedulerAdding;
                        _schedulerAdding = _schedulerRunning;
                        _schedulerRunning = queue;
                    }
                    queueNotEmpty = queue.Count != 0;
                    while (queue.Count != 0)
                    {
                        var scheduledAction = queue.Dequeue();
                        scheduledAction.CallbackAdapter(scheduledAction.Callback, scheduledAction.State);
                    }
                } while (queueNotEmpty && --loopsRemaining > 0);

                bool unblockEPoll = false;
                lock (_schedulerGate)
                {
                    if (_schedulerAdding.Count > 0)
                    {
                        unblockEPoll = true;
                    }
                    else
                    {
                        Volatile.Write(ref _epollState, EPollBlocked);
                    }
                }
                if (unblockEPoll)
                {
                    PipeEnds.WriteEnd.WriteByte(PipeActionsPending);
                }
            }

            public void CloseAccept()
            {
                (this as PipeScheduler).Schedule<object>(_ =>
                {
                    this.TransportThread.CloseAccept(this, Sockets);
                }, null);
            }

            public void StopSockets()
            {
                try
                {
                    PipeEnds.WriteEnd.WriteByte(PipeStopSockets);
                }
                // All sockets stopped already and the PipeEnd was disposed
                catch (IOException ex) when (ex.HResult == PosixResult.EPIPE)
                {}
                catch (ObjectDisposedException)
                {}
            }

            public void Dispose()
            {
                EPoll?.Dispose();
                PipeEnds.Dispose();
                MemoryPool?.Dispose();
            }

            public void RemoveSocket(int tsocketKey)
            {
                var sockets = Sockets;
                lock (sockets)
                {
                    var initialCount = sockets.Count;
                    sockets.Remove(tsocketKey);
                    if (sockets.Count == 0)
                    {
                        PipeEnds.WriteEnd.WriteByte(PipeStopThread);
                    }
                }
            }
        }
    }
}