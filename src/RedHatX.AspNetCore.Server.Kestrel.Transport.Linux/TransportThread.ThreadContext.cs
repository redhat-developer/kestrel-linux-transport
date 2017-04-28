using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Microsoft.AspNetCore.Server.Kestrel.Internal.System.IO.Pipelines;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Abstractions;
using Microsoft.Extensions.Logging;

namespace RedHatX.AspNetCore.Server.Kestrel.Transport.Linux
{
    sealed partial class TransportThread
    {
        sealed class ThreadContext : IScheduler
        {
            public ThreadContext(TransportThread transportThread, LinuxTransportOptions transportOptions, IConnectionHandler connectionHandler, ILogger logger)
            {
                TransportThread = transportThread;
                ConnectionHandler = connectionHandler;

                Sockets = new Dictionary<int, TSocket>();
                EPoll = EPoll.Create();
                EPollFd = EPoll.DangerousGetHandle().ToInt32();
                PipeFactory = new PipeFactory();
                Logger = logger;
                AcceptSockets = new List<TSocket>();
                _schedulerAdding = new Queue<ScheduledAction>(1024);
                _schedulerRunning = new Queue<ScheduledAction>(1024);
                PipeEnds = PipeEnd.CreatePair(blocking: false);
                _epollState = EPollBlocked;
                SendScheduler = transportOptions.DeferSend ? this as IScheduler : InlineScheduler.Default;
            }

            public readonly int EPollFd;
            public readonly ILogger Logger;
            public readonly IConnectionHandler ConnectionHandler;
            public readonly PipeEndPair PipeEnds;
            public readonly IScheduler SendScheduler;

            public readonly TransportThread TransportThread;
            // key is the file descriptor
            public readonly Dictionary<int, TSocket> Sockets;
            public readonly PipeFactory PipeFactory;
            public readonly List<TSocket> AcceptSockets;

            private readonly EPoll EPoll;
            private int _epollState;
            private readonly object _schedulerGate = new object();
            private Queue<ScheduledAction> _schedulerAdding;
            private Queue<ScheduledAction> _schedulerRunning;

            public void SetEpollNotBlocked()
            {
                Volatile.Write(ref _epollState, EPollNotBlocked);
            }

            void IScheduler.Schedule(Action action)
            {
                int epollState;
                lock (_schedulerGate)
                {
                    epollState = Interlocked.CompareExchange(ref _epollState, EPollNotBlocked, EPollBlocked);
                    _schedulerAdding.Enqueue(new ScheduledAction { Action = action });
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
                        scheduledAction.Action();
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
                (this as IScheduler).Schedule(() =>
                {
                    this.TransportThread.CloseAccept(this, Sockets);
                });
            }

            public void StopSockets()
            {
                try
                {
                    PipeEnds.WriteEnd.WriteByte(PipeStopSockets);
                }
                catch (ObjectDisposedException)
                {
                    // All sockets stopped already and the PipeEnd was disposed
                }
            }

            public void Dispose()
            {
                EPoll?.Dispose();
                PipeEnds.Dispose();
                PipeFactory?.Dispose();
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