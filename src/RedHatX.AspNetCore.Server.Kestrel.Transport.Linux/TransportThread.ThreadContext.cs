using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipelines;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Abstractions.Internal;
using Microsoft.Extensions.Logging;

namespace RedHatX.AspNetCore.Server.Kestrel.Transport.Linux
{
    sealed partial class TransportThread
    {
        sealed class ThreadContext : PipeScheduler
        {
            private const int MemoryAlignment = 8;

            public unsafe ThreadContext(TransportThread transportThread, LinuxTransportOptions transportOptions, IConnectionHandler connectionHandler, ILogger logger)
            {
                TransportThread = transportThread;
                ConnectionHandler = connectionHandler;

                Sockets = new Dictionary<int, TSocket>();
                Logger = logger;
                AcceptSockets = new List<TSocket>();
                _schedulerAdding = new Queue<ScheduledAction>(1024);
                _schedulerRunning = new Queue<ScheduledAction>(1024);
                _scheduledSendAdding = new List<ScheduledSend>(1024);
                _scheduledSendRunning = new List<ScheduledSend>(1024);
                _epollState = EPollBlocked;
                if (transportOptions.AioReceive | transportOptions.AioSend)
                {
                    _aioEventsMemory = AllocMemory(sizeof(AioEvent) * TransportThread.EventBufferLength);
                    _aioCbsMemory = AllocMemory(sizeof(AioCb) * TransportThread.EventBufferLength);
                    _aioCbsTableMemory = AllocMemory(sizeof(AioCb*) * TransportThread.EventBufferLength);
                    _ioVectorTableMemory = AllocMemory(sizeof(IOVector) * TransportThread.IoVectorsPerAioSocket * TransportThread.EventBufferLength);
                    for (int i = 0; i < TransportThread.EventBufferLength; i++)
                    {
                        AioCbsTable[i] = &AioCbs[i];
                    }
                    AioResults = new Exception[TransportThread.EventBufferLength];
                }
            }

            private unsafe IntPtr AllocMemory(int length)
            {
                IntPtr res = Marshal.AllocHGlobal(length + MemoryAlignment - 1);
                Span<byte> span = new Span<byte>(Align(res), length);
                span.Clear();
                return res;
            }

            public void Initialize()
            {
                // These members need to be Disposed
                EPoll = EPoll.Create();
                EPollFd = EPoll.DangerousGetHandle().ToInt32();
                MemoryPool = TransportThread.CreateMemoryPool();
                PipeEnds = PipeEnd.CreatePair(blocking: false);
                if (_aioEventsMemory != IntPtr.Zero)
                {
                    AioInterop.IoSetup(EventBufferLength, out AioContext).ThrowOnError();
                }
            }

            public int EPollFd;
            public readonly ILogger Logger;
            public readonly IConnectionHandler ConnectionHandler;
            public PipeEndPair PipeEnds;

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
            private List<ScheduledSend> _scheduledSendAdding;
            private List<ScheduledSend> _scheduledSendRunning;

            private IntPtr _aioEventsMemory;
            public unsafe AioEvent* AioEvents => (AioEvent*)Align(_aioEventsMemory);
            private IntPtr _aioCbsMemory;
            public unsafe AioCb* AioCbs => (AioCb*)Align(_aioCbsMemory);
            private IntPtr _aioCbsTableMemory;
            public unsafe AioCb** AioCbsTable => (AioCb**)Align(_aioCbsTableMemory);
            private IntPtr _ioVectorTableMemory;
            public unsafe IOVector* IoVectorTable => (IOVector*)Align(_ioVectorTableMemory);
            public IntPtr AioContext;
            public Exception[] AioResults;

            private unsafe void* Align(IntPtr p)
            {
                ulong pointer = (ulong)p;
                pointer += MemoryAlignment - 1;
                pointer &= ~(ulong)(MemoryAlignment - 1);
                return (void*)pointer;
            }

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

            public void ScheduleSend(TSocket socket)
            {
                int epollState;
                lock (_schedulerGate)
                {
                    epollState = Interlocked.CompareExchange(ref _epollState, EPollNotBlocked, EPollBlocked);
                    _scheduledSendAdding.Add(new ScheduledSend { Socket = socket });
                }
                if (epollState == EPollBlocked)
                {
                    PipeEnds.WriteEnd.WriteByte(PipeActionsPending);
                }
            }

            public void DoScheduledWork()
            {
                Queue<ScheduledAction> queue;
                List<ScheduledSend> sendQueue;
                lock (_schedulerGate)
                {
                    queue = _schedulerAdding;
                    _schedulerAdding = _schedulerRunning;
                    _schedulerRunning = queue;

                    sendQueue = _scheduledSendAdding;
                    _scheduledSendAdding = _scheduledSendRunning;
                    _scheduledSendRunning = sendQueue;
                }
                if (sendQueue.Count > 0)
                {
                    TransportThread.PerformSends(sendQueue);
                }
                while (queue.Count != 0)
                {
                    var scheduledAction = queue.Dequeue();
                    scheduledAction.CallbackAdapter(scheduledAction.Callback, scheduledAction.State);
                }

                bool unblockEPoll = false;
                lock (_schedulerGate)
                {
                    if (_schedulerAdding.Count > 0 || _scheduledSendAdding.Count > 0)
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
                if (_aioEventsMemory != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(_aioEventsMemory);
                    _aioEventsMemory = IntPtr.Zero;
                }
                if (_aioCbsMemory != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(_aioCbsMemory);
                    _aioCbsMemory = IntPtr.Zero;
                }
                if (_aioCbsTableMemory != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(_aioCbsTableMemory);
                    _aioCbsTableMemory = IntPtr.Zero;
                }
                if (_ioVectorTableMemory != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(_ioVectorTableMemory);
                    _ioVectorTableMemory = IntPtr.Zero;
                }
                if (AioContext != IntPtr.Zero)
                {
                    AioInterop.IoDestroy(AioContext);
                    AioContext = IntPtr.Zero;
                }
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