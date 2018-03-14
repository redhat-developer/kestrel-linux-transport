using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipelines;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Abstractions.Internal;
using Microsoft.Extensions.Logging;

namespace RedHatX.AspNetCore.Server.Kestrel.Transport.Linux
{
    sealed partial class TransportThread
    {
        sealed class ThreadContext : PipeScheduler, IDisposable
        {
            private static readonly IPAddress NotIPSocket = IPAddress.None;
            private const int IoVectorsPerAioSocket = 8;
            private const int ListenBacklog = 128;
            private  const int EventBufferLength = 512;
            private const int EPollBlocked = 1;
            private const int EPollNotBlocked = 0;
            private const byte PipeStopThread = 0;
            private const byte PipeActionsPending = 1;
            private const byte PipeStopSockets = 2;
            private const int MemoryAlignment = 8;

            private readonly int _epollFd;
            private readonly EPoll _epoll;

            private readonly TransportThread _transportThread;
            private readonly LinuxTransportOptions _transportOptions;
            private readonly ILogger _logger;
            private readonly IConnectionHandler _connectionHandler;
            // key is the file descriptor
            private readonly Dictionary<int, TSocket> _sockets;
            private readonly List<TSocket> _acceptSockets;

            private PipeEndPair _pipeEnds;
            private int _epollState;

            private readonly object _schedulerGate = new object();
            private Queue<ScheduledAction> _schedulerAdding;
            private Queue<ScheduledAction> _schedulerRunning;
            private List<ScheduledSend> _scheduledSendAdding;
            private List<ScheduledSend> _scheduledSendRunning;

            private readonly IntPtr _aioEventsMemory;
            private readonly IntPtr _aioCbsMemory;
            private readonly IntPtr _aioCbsTableMemory;
            private readonly IntPtr _ioVectorTableMemory;
            private readonly IntPtr _aioContext;
            private readonly ReadOnlySequence<byte>[] _aioSendBuffers;

            public readonly MemoryPool<byte> MemoryPool;

            private unsafe AioEvent* AioEvents => (AioEvent*)Align(_aioEventsMemory);
            private unsafe AioCb* AioCbs => (AioCb*)Align(_aioCbsMemory);
            private unsafe AioCb** AioCbsTable => (AioCb**)Align(_aioCbsTableMemory);
            private unsafe IOVector* IoVectorTable => (IOVector*)Align(_ioVectorTableMemory);


            public unsafe ThreadContext(TransportThread transportThread)
            {
                _transportThread = transportThread;
                _connectionHandler = transportThread.ConnectionHandler;
                _sockets = new Dictionary<int, TSocket>();
                _logger = _transportThread.LoggerFactory.CreateLogger($"{nameof(_transportThread)}.{_transportThread.ThreadId}"); ;
                _acceptSockets = new List<TSocket>();
                _transportOptions = transportThread.TransportOptions;
                _schedulerAdding = new Queue<ScheduledAction>(1024);
                _schedulerRunning = new Queue<ScheduledAction>(1024);
                _scheduledSendAdding = new List<ScheduledSend>(1024);
                _scheduledSendRunning = new List<ScheduledSend>(1024);
                _epollState = EPollBlocked;
                if (_transportOptions.AioReceive | _transportOptions.AioSend)
                {
                    _aioEventsMemory = AllocMemory(sizeof(AioEvent) * EventBufferLength);
                    _aioCbsMemory = AllocMemory(sizeof(AioCb) * EventBufferLength);
                    _aioCbsTableMemory = AllocMemory(sizeof(AioCb*) * EventBufferLength);
                    _ioVectorTableMemory = AllocMemory(sizeof(IOVector) * IoVectorsPerAioSocket * EventBufferLength);
                    for (int i = 0; i < EventBufferLength; i++)
                    {
                        AioCbsTable[i] = &AioCbs[i];
                    }
                    if (_transportOptions.AioSend)
                    {
                        _aioSendBuffers = new ReadOnlySequence<byte>[EventBufferLength];
                    }
                }

                // These members need to be Disposed
                _epoll = EPoll.Create();
                _epollFd = _epoll.DangerousGetHandle().ToInt32();
                MemoryPool = CreateMemoryPool();
                _pipeEnds = PipeEnd.CreatePair(blocking: false);
                if (_aioEventsMemory != IntPtr.Zero)
                {
                    AioInterop.IoSetup(EventBufferLength, out _aioContext).ThrowOnError();
                }
            }

            private Socket CreateAcceptSocket(IPEndPoint endPoint, ref SocketFlags flags, out int zeroCopyThreshold)
            {
                Socket acceptSocket = null;
                int port = endPoint.Port;
                try
                {
                    bool ipv4 = endPoint.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork;
                    acceptSocket = Socket.Create(ipv4 ? AddressFamily.InterNetwork : AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp, blocking: false);
                    if (!ipv4)
                    {
                        // Kestrel does mapped ipv4 by default.
                        acceptSocket.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.IPv6Only, 0);
                    }
                    if (_transportOptions.ReceiveOnIncomingCpu)
                    {
                        if (_transportThread.CpuId != -1)
                        {
                            if (!acceptSocket.TrySetSocketOption(SocketOptionLevel.Socket, SocketOptionName.IncomingCpu, _transportThread.CpuId))
                            {
                                _logger.LogWarning($"Cannot enable nameof{SocketOptionName.IncomingCpu} for {endPoint}");
                            }
                        }
                    }
                    // Linux: allow bind during linger time
                    acceptSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, 1);
                    // Linux: allow concurrent binds and let the kernel do load-balancing
                    acceptSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReusePort, 1);
                    if (_transportOptions.DeferAccept)
                    {
                        // Linux: wait up to 1 sec for data to arrive before accepting socket
                        acceptSocket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.DeferAccept, 1);
                        flags |= SocketFlags.DeferAccept;
                    }
                    zeroCopyThreshold = LinuxTransportOptions.NoZeroCopy;
                    if (_transportOptions.ZeroCopy && _transportOptions.ZeroCopyThreshold != LinuxTransportOptions.NoZeroCopy)
                    {
                        if (acceptSocket.TrySetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ZeroCopy, 1))
                        {
                            zeroCopyThreshold = _transportOptions.ZeroCopyThreshold;
                        }
                    }

                    acceptSocket.Bind(endPoint);
                    if (port == 0)
                    {
                        // When testing we want the OS to select a free port
                        port = acceptSocket.GetLocalIPAddress().Port;
                    }

                    acceptSocket.Listen(ListenBacklog);

                    endPoint.Port = port;
                    return acceptSocket;
                }
                catch
                {
                    acceptSocket?.Dispose();
                    throw;
                }
            }

            public unsafe void Run()
            {
                // register pipe 
                int pipeKey = _pipeEnds.ReadEnd.DangerousGetHandle().ToInt32();
                EPollInterop.EPollControl(_epollFd,
                                        EPollOperation.Add,
                                        _pipeEnds.ReadEnd.DangerousGetHandle().ToInt32(),
                                        EPollEvents.Readable,
                                        EPollData(pipeKey));

                // create accept socket
                {
                    Socket acceptSocket;
                    SocketFlags flags;
                    int zeroCopyThreshold;
                    if (_transportThread.AcceptThread != null)
                    {
                        flags = SocketFlags.TypePassFd;
                        acceptSocket = _transportThread.AcceptThread.CreateReceiveSocket();
                        zeroCopyThreshold = LinuxTransportOptions.NoZeroCopy;
                    }
                    else
                    {
                        flags = SocketFlags.TypeAccept;
                        acceptSocket = CreateAcceptSocket(_transportThread.EndPoint, ref flags, out zeroCopyThreshold);
                    }
                    if (_transportOptions.DeferSend)
                    {
                        flags |= SocketFlags.DeferSend;
                    }
                    // accept connections
                    AcceptOn(acceptSocket, flags, zeroCopyThreshold); // TODO: what happens when AcceptOn fails?
                }

                int notPacked = !EPoll.PackedEvents ? 1 : 0;
                var buffer = stackalloc int[EventBufferLength * (3 + notPacked)];
                int statReadEvents = 0;
                int statWriteEvents = 0;
                int statAcceptEvents = 0;
                int statAccepts = 0;
                int statZeroCopySuccess = 0;
                int statZeroCopyCopied = 0;

                var acceptableSockets = new List<TSocket>(1);
                var readableSockets = new List<TSocket>(EventBufferLength);
                var writableSockets = new List<TSocket>(EventBufferLength);
                var reregisterEventSockets = new List<TSocket>(EventBufferLength);
                var zeroCopyCompletions = new List<TSocket>(EventBufferLength);
                bool pipeReadable = false;

                CompleteStateChange(TransportThreadState.Started);
                bool running = true;
                do
                {
                    int numEvents = EPollInterop.EPollWait(_epollFd, buffer, EventBufferLength, timeout: EPoll.TimeoutInfinite).Value;

                    // actions can be scheduled without unblocking epoll
                    SetEpollNotBlocked();

                    // check events
                    // we don't handle them immediately:
                    // - this ensures we don't mismatch a closed socket with a new socket that have the same fd
                    //     ~ To have the same fd, the previous fd must be closed, which means it is removed from the epoll
                    //     ~ and won't show up in our next call to epoll.Wait.
                    //     ~ The old fd may be present in the buffer still, but lookup won't give a match, since it is removed
                    //     ~ from the dictionary before it is closed. If we were accepting already, a new socket could match.
                    // - this also improves cache/cpu locality of the lookup
                    int* ptr = buffer;
                    lock (_sockets)
                    {
                        for (int i = 0; i < numEvents; i++)
                        {
                            //   Packed             Non-Packed
                            //   ------             ------
                            // 0:Events       ==    Events
                            // 1:Int1 = Key         [Padding]
                            // 2:Int2 = Key   ==    Int1 = Key
                            // 3:~~~~~~~~~~         Int2 = Key
                            //                      ~~~~~~~~~~
                            EPollEvents events = (EPollEvents)ptr[0];
                            int key = ptr[2];
                            ptr += 3 + notPacked;
                            TSocket tsocket;
                            if (_sockets.TryGetValue(key, out tsocket))
                            {
                                var type = tsocket.Type;
                                if (type == SocketFlags.TypeClient)
                                {
                                    lock (tsocket.Gate)
                                    {
                                        var pendingEventState = tsocket.PendingEventState;

                                        // zero copy
                                        if ((pendingEventState & EPollEvents.Error & events) != EPollEvents.None)
                                        {
                                            var copyResult = SocketInterop.CompleteZeroCopy(tsocket.Fd);
                                            if (copyResult != PosixResult.EAGAIN)
                                            {
                                                events &= ~EPollEvents.Error;
                                                pendingEventState &= ~EPollEvents.Error;
                                                zeroCopyCompletions.Add(tsocket);
                                                if (copyResult == SocketInterop.ZeroCopyCopied)
                                                {
                                                    tsocket.ZeroCopyThreshold = LinuxTransportOptions.NoZeroCopy;
                                                    statZeroCopyCopied++;
                                                }
                                                else if (copyResult == SocketInterop.ZeroCopySuccess)
                                                {
                                                    statZeroCopySuccess++;
                                                }
                                                else
                                                {
                                                    Environment.FailFast($"Error occurred while trying to complete zero copy: {copyResult}");
                                                }
                                            }
                                        }

                                        // treat Error as Readable, Writable
                                        if ((events & EPollEvents.Error) != EPollEvents.None)
                                        {
                                            events |= EPollEvents.Readable | EPollEvents.Writable;
                                        }

                                        events &= pendingEventState & (EPollEvents.Readable | EPollEvents.Writable);
                                        // readable
                                        if ((events & EPollEvents.Readable) != EPollEvents.None)
                                        {
                                            readableSockets.Add(tsocket);
                                            pendingEventState &= ~EPollEvents.Readable;
                                        }
                                        // writable
                                        if ((events & EPollEvents.Writable) != EPollEvents.None)
                                        {
                                            writableSockets.Add(tsocket);
                                            pendingEventState &= ~EPollEvents.Writable;
                                        }

                                        // reregister
                                        tsocket.PendingEventState = pendingEventState;
                                        if ((pendingEventState & (EPollEvents.Readable | EPollEvents.Writable)) != EPollEvents.None)
                                        {
                                            tsocket.PendingEventState |= TSocket.EventControlPending;
                                            reregisterEventSockets.Add(tsocket);
                                        }
                                    }
                                }
                                else
                                {
                                    statAcceptEvents++;
                                    acceptableSockets.Add(tsocket);
                                }
                            }
                            else if (key == pipeKey)
                            {
                                pipeReadable = true;
                            }
                        }
                    }

                    // zero copy
                    for (int i = 0; i < zeroCopyCompletions.Count; i++)
                    {
                        zeroCopyCompletions[i].OnZeroCopyCompleted();
                    }
                    zeroCopyCompletions.Clear();

                    // handle accepts
                    statAcceptEvents += acceptableSockets.Count;
                    for (int i = 0; i < acceptableSockets.Count; i++)
                    {
                        statAccepts += HandleAccept(acceptableSockets[i]);
                    }
                    acceptableSockets.Clear();

                    // handle writes
                    statWriteEvents += writableSockets.Count;
                    for (int i = 0; i < writableSockets.Count; i++)
                    {
                        writableSockets[i].OnWritable(stopped: false);
                    }
                    writableSockets.Clear();

                    // handle reads
                    statReadEvents += readableSockets.Count;
                    if (!_transportOptions.AioReceive)
                    {
                        bool checkAvailable = _transportOptions.CheckAvailable;
                        for (int i = 0; i < readableSockets.Count; i++)
                        {
                            TSocket socket = readableSockets[i];
                            int availableBytes = !checkAvailable ? 0 : socket.Socket.GetAvailableBytes();
                            var receiveResult = socket.Receive(availableBytes);
                            socket.OnReceiveFromSocket(receiveResult);
                        }
                        readableSockets.Clear();
                    }
                    else if (readableSockets.Count > 0)
                    {
                        AioReceive(readableSockets);
                    }

                    // reregister for events
                    for (int i = 0; i < reregisterEventSockets.Count; i++)
                    {
                        var tsocket = reregisterEventSockets[i];
                        lock (tsocket.Gate)
                        {
                            var pendingEventState = tsocket.PendingEventState & ~TSocket.EventControlPending;
                            tsocket.PendingEventState = pendingEventState;
                            UpdateEPollControl(tsocket, pendingEventState, registered: true);
                        }
                    }
                    reregisterEventSockets.Clear();

                    // handle pipe
                    if (pipeReadable)
                    {
                        PosixResult result;
                        do
                        {
                            result = _pipeEnds.ReadEnd.TryReadByte();
                            if (result.Value == PipeStopSockets)
                            {
                                StopSockets();
                            }
                            else if (result.Value == PipeStopThread)
                            {
                                running = false;
                            }
                        } while (result);
                        pipeReadable = false;
                    }

                    // scheduled work
                    DoScheduledWork(_transportOptions.AioSend);

                } while (running);

                _logger.LogInformation($"Thread {_transportThread.ThreadId}: Stats A/AE:{statAccepts}/{statAcceptEvents} RE:{statReadEvents} WE:{statWriteEvents} ZCS/ZCC:{statZeroCopySuccess}/{statZeroCopyCopied}");
            }

            private unsafe void AioReceive(List<TSocket> readableSockets)
            {
                long PackReceiveState(int received, int advanced, int iovLength) => ((long)received << 32) + (advanced << 8) + (iovLength);
                (int received, int advanced, int iovLength) UnpackReceiveState(long data) => ((int)(data >> 32), (int)((data >> 8) & 0xffffff), (int)(data & 0xff));

                int readableSocketCount = readableSockets.Count;
                AioCb* aioCb = AioCbs;
                IOVector* ioVectors = IoVectorTable;
                PosixResult* receiveResults = stackalloc PosixResult[readableSocketCount];
                bool checkAvailable = _transportOptions.CheckAvailable;
                for (int i = 0; i < readableSocketCount; i++)
                {
                    TSocket socket = readableSockets[i];
                    int availableBytes = !checkAvailable ? 0 : socket.Socket.GetAvailableBytes();
                    int ioVectorLength = socket.CalcIOVectorLengthForReceive(availableBytes, IoVectorsPerAioSocket);
                    int advanced = socket.FillReceiveIOVector(availableBytes, ioVectors, ref ioVectorLength);

                    aioCb->Fd = socket.Fd;
                    aioCb->Data = PackReceiveState(0, advanced, ioVectorLength);
                    aioCb->OpCode = AioOpCode.PReadv;
                    aioCb->Buffer = ioVectors;
                    aioCb->Length = ioVectorLength;
                    aioCb++;

                    ioVectors += ioVectorLength;
                }
                int eAgainCount = 0;
                while (readableSocketCount > 0)
                {
                    IntPtr ctxp = _aioContext;
                    PosixResult res = AioInterop.IoSubmit(ctxp, readableSocketCount, AioCbsTable);
                    if (res != readableSocketCount)
                    {
                        throw new NotSupportedException("Unexpected IoSubmit retval " + res);
                    }

                    AioEvent* aioEvents = AioEvents;
                    AioInterop.IoGetEvents(ctxp, readableSocketCount, readableSocketCount, aioEvents, -1);
                    if (res != readableSocketCount)
                    {
                        throw new NotSupportedException("Unexpected IoGetEvents retval " + res);
                    }
                    int socketsRemaining = readableSocketCount;
                    bool allEAgain = true;
                    AioEvent* aioEvent = aioEvents;
                    for (int i = 0; i < readableSocketCount; i++)
                    {
                        PosixResult result = aioEvent->Result;
                        int socketIndex = i; // assumes in-order events
                        TSocket socket = readableSockets[socketIndex];
                        (int received, int advanced, int iovLength) = UnpackReceiveState(aioEvent->Data);
                        (bool done, PosixResult retval) = socket.InterpretReceiveResult(result, ref received, advanced, (IOVector*)aioEvent->AioCb->Buffer, iovLength);
                        if (done)
                        {
                            receiveResults[socketIndex] = retval;
                            socketsRemaining--;
                            aioEvent->AioCb->OpCode = AioOpCode.Noop;
                            allEAgain = false;
                        }
                        else if (retval != PosixResult.EAGAIN)
                        {
                            aioEvent->AioCb->Data = PackReceiveState(received, advanced, iovLength);
                            allEAgain = false;
                        }
                        aioEvent++;
                    }
                    if (socketsRemaining > 0)
                    {
                        if (allEAgain)
                        {
                            eAgainCount++;
                            if (eAgainCount == TransportConstants.MaxEAgainCount)
                            {
                                throw new NotSupportedException("Too many EAGAIN, unable to receive available bytes.");
                            }
                        }
                        else
                        {
                            aioCb = AioCbs;
                            AioCb* aioCbWriteAt = aioCb;
                            // The kernel doesn't handle Noop, we need to remove them from the aioCbs
                            for (int i = 0; i < readableSocketCount; i++)
                            {
                                if (aioCb[i].OpCode != AioOpCode.Noop)
                                {
                                    if (aioCbWriteAt != aioCb)
                                    {
                                        *aioCbWriteAt = *aioCb;
                                    }
                                    aioCbWriteAt++;
                                }
                                aioCb++;
                            }
                            readableSocketCount = socketsRemaining;
                            eAgainCount = 0;
                        }
                    }
                    else
                    {
                        readableSocketCount = 0;
                    }
                }
                for (int i = 0; i < readableSockets.Count; i++)
                {
                    readableSockets[i].OnReceiveFromSocket(receiveResults[i]);
                }
                readableSockets.Clear();
            }

            private void StopSockets()
            {
                Dictionary<int, TSocket> clone;
                lock (_sockets)
                {
                    clone = new Dictionary<int, TSocket>(_sockets);
                }
                foreach (var kv in clone)
                {
                    var tsocket = kv.Value;
                    tsocket.Stop();
                }
            }

            private int HandleAccept(TSocket tacceptSocket)
            {
                var type = tacceptSocket.Type;
                Socket clientSocket;
                PosixResult result;
                if (type == SocketFlags.TypeAccept)
                {
                    // TODO: should we handle more than 1 accept? If we do, we shouldn't be to eager
                    //       as that might give the kernel the impression we have nothing to do
                    //       which could interfere with the SO_REUSEPORT load-balancing.
                    result = tacceptSocket.Socket.TryAccept(out clientSocket, blocking: false);
                }
                else
                {
                    result = tacceptSocket.Socket.TryReceiveSocket(out clientSocket, blocking: false);
                    if (result.Value == 0)
                    {
                        // The socket passing us file descriptors has closed.
                        // We dispose our end so we get get removed from the epoll.
                        tacceptSocket.Socket.Dispose();
                        return 0;
                    }
                }
                if (result.IsSuccess)
                {
                    int fd;
                    TSocket tsocket;
                    try
                    {
                        fd = clientSocket.DangerousGetHandle().ToInt32();

                        bool ipSocket = !object.ReferenceEquals(tacceptSocket.LocalAddress, NotIPSocket);

                        // Store the last LocalAddress on the tacceptSocket so we might reuse it instead
                        // of allocating a new one for the same address.
                        IPEndPointStruct localAddress = default(IPEndPointStruct);
                        IPEndPointStruct remoteAddress = default(IPEndPointStruct);
                        if (ipSocket && clientSocket.TryGetLocalIPAddress(out localAddress, tacceptSocket.LocalAddress))
                        {
                            tacceptSocket.LocalAddress = localAddress.Address;
                            clientSocket.TryGetPeerIPAddress(out remoteAddress);
                        }
                        else
                        {
                            // This is not an IP socket.
                            tacceptSocket.LocalAddress = NotIPSocket;
                            ipSocket = false;
                        }

                        SocketFlags flags = SocketFlags.TypeClient | (tacceptSocket.IsDeferSend ? SocketFlags.DeferSend : SocketFlags.None);
                        tsocket = new TSocket(this, clientSocket, fd, flags)
                        {
                            RemoteAddress = remoteAddress.Address,
                            RemotePort = remoteAddress.Port,
                            LocalAddress = localAddress.Address,
                            LocalPort = localAddress.Port,
                            ZeroCopyThreshold = tacceptSocket.ZeroCopyThreshold
                        };

                        if (ipSocket)
                        {
                            clientSocket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.NoDelay, 1);
                        }
                    }
                    catch
                    {
                        clientSocket.Dispose();
                        return 0;
                    }

                    _connectionHandler.OnConnection(tsocket);

                    lock (_sockets)
                    {
                        _sockets.Add(fd, tsocket);
                    }

                    bool dataMayBeAvailable = tacceptSocket.IsDeferAccept;
                    tsocket.Start(dataMayBeAvailable);

                    return 1;
                }
                else
                {
                    return 0;
                }
            }


            private void AcceptOn(Socket acceptSocket, SocketFlags flags, int zeroCopyThreshold)
            {
                TSocket tsocket = null;
                int fd = acceptSocket.DangerousGetHandle().ToInt32();
                try
                {
                    tsocket = new TSocket(this, acceptSocket, fd, flags)
                    {
                        ZeroCopyThreshold = zeroCopyThreshold
                    };
                    _acceptSockets.Add(tsocket);
                    lock (_sockets)
                    {
                        _sockets.Add(tsocket.Fd, tsocket);
                    }

                    EPollInterop.EPollControl(_epollFd,
                                              EPollOperation.Add,
                                              fd,
                                              EPollEvents.Readable,
                                              EPollData(fd));
                }
                catch
                {
                    acceptSocket.Dispose();
                    _acceptSockets.Remove(tsocket);
                    lock (_sockets)
                    {
                        _sockets.Remove(fd);
                    }
                    throw;
                }
            }

            private unsafe IntPtr AllocMemory(int length)
            {
                IntPtr res = Marshal.AllocHGlobal(length + MemoryAlignment - 1);
                Span<byte> span = new Span<byte>(Align(res), length);
                span.Clear();
                return res;
            }

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
                    _pipeEnds.WriteEnd.WriteByte(PipeActionsPending);
                }
            }

            public void ScheduleSend(TSocket socket)
            {
                // TODO: remove this function
                int epollState;
                lock (_schedulerGate)
                {
                    epollState = Interlocked.CompareExchange(ref _epollState, EPollNotBlocked, EPollBlocked);
                    _scheduledSendAdding.Add(new ScheduledSend { Socket = socket });
                }
                if (epollState == EPollBlocked)
                {
                    _pipeEnds.WriteEnd.WriteByte(PipeActionsPending);
                }
            }

            public void DoScheduledWork(bool aioSend)
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
                    PerformSends(sendQueue, aioSend);
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
                    _pipeEnds.WriteEnd.WriteByte(PipeActionsPending);
                }
            }

            public void CloseAccept()
            {
                (this as PipeScheduler).Schedule<object>(_ =>
                {
                    this.CloseAccept(_sockets);
                }, null);
            }

            public void RequestStopSockets()
            {
                try
                {
                    _pipeEnds.WriteEnd.WriteByte(PipeStopSockets);
                }
                // All sockets stopped already and the PipeEnd was disposed
                catch (IOException ex) when (ex.HResult == PosixResult.EPIPE)
                { }
                catch (ObjectDisposedException)
                { }
            }

            public void Dispose()
            {
                _epoll?.Dispose();
                _pipeEnds.Dispose();
                MemoryPool?.Dispose();
                if (_aioEventsMemory != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(_aioEventsMemory);
                }
                if (_aioCbsMemory != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(_aioCbsMemory);
                }
                if (_aioCbsTableMemory != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(_aioCbsTableMemory);
                }
                if (_ioVectorTableMemory != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(_ioVectorTableMemory);
                }
                if (_aioContext != IntPtr.Zero)
                {
                    AioInterop.IoDestroy(_aioContext);
                }
            }

            public void RemoveSocket(int tsocketKey)
            {
                var sockets = _sockets;
                lock (sockets)
                {
                    var initialCount = sockets.Count;
                    sockets.Remove(tsocketKey);
                    if (sockets.Count == 0)
                    {
                        _pipeEnds.WriteEnd.WriteByte(PipeStopThread);
                    }
                }
            }

            private void CompleteStateChange(TransportThreadState state)
            {
                _transportThread.CompleteStateChange(state);
            }

            private unsafe void AioSend(List<ScheduledSend> sendQueue)
            {
                while (sendQueue.Count > 0)
                {
                    int sendCount = 0;
                    int completedCount = 0;
                    AioCb* aioCbs = AioCbs;
                    IOVector* ioVectors = IoVectorTable;
                    ReadOnlySequence<byte>[] sendBuffers = _aioSendBuffers;
                    for (int i = 0; i < sendQueue.Count; i++)
                    {
                        TSocket socket = sendQueue[i].Socket;
                        ReadOnlySequence<byte> buffer;
                        Exception error = socket.GetReadResult(out buffer);
                        if (error != null)
                        {
                            socket.CompleteOutput(error == TransportConstants.StopSentinel ? null : error);
                            completedCount++;
                        }
                        else
                        {
                            int ioVectorLength = socket.CalcIOVectorLengthForSend(ref buffer, IoVectorsPerAioSocket);
                            socket.FillSendIOVector(ref buffer, ioVectors, ioVectorLength);

                            aioCbs->Fd = socket.Fd;
                            aioCbs->Data = i;
                            aioCbs->OpCode = AioOpCode.PWritev;
                            aioCbs->Buffer = ioVectors;
                            aioCbs->Length = ioVectorLength;
                            aioCbs++;

                            sendBuffers[sendCount] = buffer;
                            sendCount++;
                            if (sendCount == EventBufferLength)
                            {
                                break;
                            }

                            ioVectors += ioVectorLength;
                        }
                    }
                    if (sendCount > 0)
                    {
                        IntPtr ctxp = _aioContext;
                        PosixResult res = AioInterop.IoSubmit(ctxp, sendCount, AioCbsTable);
                        if (res != sendCount)
                        {
                            throw new NotSupportedException("Unexpected IoSubmit Send retval " + res);
                        }

                        AioEvent* aioEvents = AioEvents;
                        AioInterop.IoGetEvents(ctxp, sendCount, sendCount, aioEvents, -1); // TODO user-space completion
                        if (res != sendCount)
                        {
                            throw new NotSupportedException("Unexpected IoGetEvents Send retval " + res);
                        }

                        AioEvent* aioEvent = aioEvents;
                        for (int i = 0; i < sendCount; i++)
                        {
                            PosixResult result = aioEvent->Result;
                            int socketIndex = (int)aioEvent->Data;
                            TSocket socket = sendQueue[socketIndex].Socket;
                            ReadOnlySequence<byte> buffer = sendBuffers[i]; // assumes in-order events
                            sendBuffers[i] = default;
                            socket.HandleReadResult(ref buffer, result, loop: false, zerocopy: false, zeroCopyRegistered: false);
                            aioEvent++;
                        }
                    }

                    sendQueue.RemoveRange(0, sendCount + completedCount);
                }
            }


            private unsafe void PerformSends(List<ScheduledSend> sendQueue, bool aioSend)
            {
                if (aioSend)
                {
                    AioSend(sendQueue);
                }
                else
                {
                    for (int i = 0; i < sendQueue.Count; i++)
                    {
                        sendQueue[i].Socket.DoDeferedSend();
                    }
                    sendQueue.Clear();
                }
            }

            private void CloseAccept(Dictionary<int, TSocket> sockets)
            {
                var acceptSockets = _acceptSockets;
                lock (sockets)
                {
                    for (int i = 0; i < acceptSockets.Count; i++)
                    {
                        RemoveSocket(acceptSockets[i].Fd);
                    }
                }
                for (int i = 0; i < acceptSockets.Count; i++)
                {
                    // close causes remove from epoll (CLOEXEC)
                    acceptSockets[i].Socket.Dispose(); // will close (no concurrent users)
                }
                acceptSockets.Clear();
                CompleteStateChange(TransportThreadState.AcceptClosed);
            }

            // must be called under tsocket.Gate
            public void UpdateEPollControl(TSocket tsocket, EPollEvents flags, bool registered)
            {
                flags &= EPollEvents.Readable | EPollEvents.Writable | EPollEvents.Error;
                EPollInterop.EPollControl(_epollFd,
                            registered ? EPollOperation.Modify : EPollOperation.Add,
                            tsocket.Fd,
                            flags | EPollEvents.OneShot,
                            EPollData(tsocket.Fd));
            }

            private static long EPollData(int fd) => (((long)(uint)fd) << 32) | (long)(uint)fd;

            internal static MemoryPool<byte> CreateMemoryPool()
            {
                return KestrelMemoryPool.Create();
            }

            private class CallbackAdapter<T>
            {
                public static readonly Action<object, object> PostCallbackAdapter = (callback, state) => ((Action<T>)callback).Invoke((T)state);
                public static readonly Action<object, object> PostAsyncCallbackAdapter = (callback, state) => ((Action<T>)callback).Invoke((T)state);
            }

            private struct ScheduledAction
            {
                public Action<object, object> CallbackAdapter;
                public object Callback;
                public Object State;
            }

            private struct ScheduledSend
            {
                public TSocket Socket;
            }
        }
    }
}