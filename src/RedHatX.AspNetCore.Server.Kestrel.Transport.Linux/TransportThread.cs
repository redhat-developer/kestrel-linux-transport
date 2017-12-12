using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Protocols;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Abstractions.Internal;
using Microsoft.Extensions.Logging;

namespace RedHatX.AspNetCore.Server.Kestrel.Transport.Linux
{
    sealed partial class TransportThread : ITransportActionHandler
    {
        private const int MSG_ZEROCOPY = 0x4000000;

        private const int MaxPooledBlockLength = MemoryPool.MaxPooledBlockLength;
        // 128 IOVectors, take up 2KB of stack, can send up to 512KB
        private const int MaxIOVectorSendLength = 128;
        // 128 IOVectors, take up 2KB of stack, can receive up to 512KB
        private const int MaxIOVectorReceiveLength = 128;
        private const int MaxSendLength = MaxIOVectorSendLength * MaxPooledBlockLength;
        private const int ListenBacklog     = 128;
        private const int EventBufferLength = 512;
        private const int EPollBlocked      = 1;
        private const int EPollNotBlocked   = 0;
        private const byte PipeStopThread     = 0;
        private const byte PipeActionsPending = 1;
        private const byte PipeStopSockets    = 2;

        private struct ScheduledAction
        {
            public Action<object> Action;
            public Object State;
        }

        enum State
        {
            Initial,
            Starting,
            Started,
            ClosingAccept,
            AcceptClosed,
            Stopping,
            Stopped
        }

        private readonly IConnectionHandler _connectionHandler;
        private readonly int _threadId;
        private readonly IPEndPoint _endPoint;
        private readonly LinuxTransportOptions _transportOptions;
        private readonly AcceptThread _acceptThread;
        private readonly ILoggerFactory _loggerFactory;
        private readonly object _gate = new object();
        private int _cpuId;
        private State _state;
        private Thread _thread;
        private TaskCompletionSource<object> _stateChangeCompletion;
        private ThreadContext _threadContext;

        public TransportThread(IPEndPoint endPoint, IConnectionHandler connectionHandler, LinuxTransportOptions options, AcceptThread acceptThread, int threadId, int cpuId, ILoggerFactory loggerFactory)
        {
            if (connectionHandler == null)
            {
                throw new ArgumentNullException(nameof(connectionHandler));
            }
            _connectionHandler = connectionHandler;
            _threadId = threadId;
            _cpuId = cpuId;
            _endPoint = endPoint;
            _transportOptions = options;
            _acceptThread = acceptThread;
            _loggerFactory = loggerFactory;
        }

        public Task BindAsync()
        {
            TaskCompletionSource<object> tcs;
            lock (_gate)
            {
                if (_state == State.Started)
                {
                    return Task.CompletedTask;
                }
                else if (_state == State.Starting)
                {
                    return _stateChangeCompletion.Task;
                }
                else if (_state != State.Initial)
                {
                    ThrowInvalidState();
                }
                try
                {
                    tcs = _stateChangeCompletion = new TaskCompletionSource<object>();
                    _state = State.Starting;

                    _thread = new Thread(PollThread);
                    _thread.Start();
                }
                catch
                {
                    _state = State.Stopped;
                    throw;
                }
            }
            return tcs.Task;
        }

        private static Socket CreateAcceptSocket(IPEndPoint endPoint, LinuxTransportOptions transportOptions, int cpuId, ThreadContext threadContext, ref SocketFlags flags, out int zeroCopyThreshold)
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
                if (transportOptions.ReceiveOnIncomingCpu)
                {
                    if (cpuId != -1)
                    {
                        if (!acceptSocket.TrySetSocketOption(SocketOptionLevel.Socket, SocketOptionName.IncomingCpu, cpuId))
                        {
                            threadContext.Logger.LogWarning($"Cannot enable nameof{SocketOptionName.IncomingCpu} for {endPoint}");
                        }
                    }
                }
                // Linux: allow bind during linger time
                acceptSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, 1);
                // Linux: allow concurrent binds and let the kernel do load-balancing
                acceptSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReusePort, 1);
                if (transportOptions.DeferAccept)
                {
                    // Linux: wait up to 1 sec for data to arrive before accepting socket
                    acceptSocket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.DeferAccept, 1);
                    flags |= SocketFlags.DeferAccept;
                }
                zeroCopyThreshold = LinuxTransportOptions.NoZeroCopy;
                if (transportOptions.ZeroCopy && transportOptions.ZeroCopyThreshold != LinuxTransportOptions.NoZeroCopy)
                {
                    if (acceptSocket.TrySetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ZeroCopy, 1))
                    {
                        zeroCopyThreshold = transportOptions.ZeroCopyThreshold;
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

        private static void AcceptOn(Socket acceptSocket, SocketFlags flags, int zeroCopyThreshold, ThreadContext threadContext)
        {
            TSocket tsocket = null;
            int fd = acceptSocket.DangerousGetHandle().ToInt32();
            var sockets = threadContext.Sockets;
            try
            {
                tsocket = new TSocket(threadContext, flags)
                {
                    Fd = fd,
                    Socket = acceptSocket,
                    ZeroCopyThreshold = zeroCopyThreshold
                };
                threadContext.AcceptSockets.Add(tsocket);
                lock (sockets)
                {
                    sockets.Add(tsocket.Fd, tsocket);
                }

                EPollInterop.EPollControl(threadContext.EPollFd,
                                          EPollOperation.Add,
                                          fd,
                                          EPollEvents.Readable,
                                          EPollData(fd));
            }
            catch
            {
                acceptSocket.Dispose();
                threadContext.AcceptSockets.Remove(tsocket);
                lock (sockets)
                {
                    sockets.Remove(fd);
                }
                throw;
            }
        }

        public async Task UnbindAsync()
        {
            TaskCompletionSource<object> tcs = null;
            lock (_gate)
            {
                if (_state == State.Initial)
                {
                    _state = State.Stopped;
                    return;
                }
                else if (_state == State.AcceptClosed || _state == State.Stopping || _state == State.Stopped)
                {
                    return;
                }
                else if (_state == State.ClosingAccept)
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
                if (_state == State.AcceptClosed || _state == State.Stopping || _state == State.Stopped)
                {
                    return;
                }
                else if (_state == State.ClosingAccept)
                {
                    tcs = _stateChangeCompletion;
                }
                else if (_state == State.Started)
                {
                    triggerStateChange = true;
                    tcs = _stateChangeCompletion = new TaskCompletionSource<object>();
                    _state = State.ClosingAccept;
                }
                else
                {
                    // Cannot happen
                    ThrowInvalidState();
                }
            }
            if (triggerStateChange)
            {
                _threadContext.CloseAccept();
            }
            await tcs.Task;
        }

        public async Task StopAsync()
        {
            lock (_gate)
            {
                if (_state == State.Initial)
                {
                    _state = State.Stopped;
                    return;
                }
            }

            await UnbindAsync();

            TaskCompletionSource<object> tcs = null;
            bool triggerStateChange = false;
            lock (_gate)
            {
                if (_state == State.Stopped)
                {
                    return;
                }
                else if (_state == State.Stopping)
                {
                    tcs = _stateChangeCompletion;
                }
                else if (_state == State.AcceptClosed)
                {
                    tcs = _stateChangeCompletion = new TaskCompletionSource<object>();
                    _state = State.Stopping;
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
                _threadContext.StopSockets();
            }
            await tcs.Task;
        }

        private ILogger CreateLogger()
        {
            return _loggerFactory.CreateLogger($"{nameof(TransportThread)}.{_threadId}");
        }

        private unsafe void PollThread(object obj)
        {
            ThreadContext threadContext = null;
            try
            {
                // .NET doesn't support setting thread affinity on Start
                // We could change it before starting the thread
                // so it gets inherited, but we don't know how many threads
                // the runtime may start.
                if (_cpuId != -1)
                {
                    Scheduler.SetCurrentThreadAffinity(_cpuId);
                }
                // objects are allocated on the PollThread heap
                int pipeKey;
                threadContext = new ThreadContext(this, _transportOptions, _connectionHandler, CreateLogger());
                threadContext.Initialize();
                {
                    // register pipe 
                    pipeKey = threadContext.PipeEnds.ReadEnd.DangerousGetHandle().ToInt32();
                    EPollInterop.EPollControl(threadContext.EPollFd,
                                            EPollOperation.Add,
                                            threadContext.PipeEnds.ReadEnd.DangerousGetHandle().ToInt32(),
                                            EPollEvents.Readable,
                                            EPollData(pipeKey));

                    Socket acceptSocket;
                    SocketFlags flags;
                    int zeroCopyThreshold;
                    if (_acceptThread != null)
                    {
                        flags = SocketFlags.TypePassFd;
                        acceptSocket = _acceptThread.CreateReceiveSocket();
                        zeroCopyThreshold = LinuxTransportOptions.NoZeroCopy;
                    }
                    else
                    {
                        flags = SocketFlags.TypeAccept;
                        acceptSocket = CreateAcceptSocket(_endPoint, _transportOptions, _cpuId, threadContext, ref flags, out zeroCopyThreshold);
                    }
                    // accept connections
                    AcceptOn(acceptSocket, flags, zeroCopyThreshold, threadContext);

                    _threadContext = threadContext;
                }
                int epollFd = threadContext.EPollFd;
                var readEnd = threadContext.PipeEnds.ReadEnd;
                int notPacked = !EPoll.PackedEvents ? 1 : 0;
                var buffer = stackalloc int[EventBufferLength * (3 + notPacked)];
                int statReadEvents = 0;
                int statWriteEvents = 0;
                int statAcceptEvents = 0;
                int statAccepts = 0;
                int statZeroCopySuccess = 0;
                int statZeroCopyCopied = 0;
                var sockets = threadContext.Sockets;

                var acceptableSockets = new List<TSocket>(1);
                var readableSockets = new List<TSocket>(EventBufferLength);
                var writableSockets = new List<TSocket>(EventBufferLength);
                var reregisterEventSockets = new List<TSocket>(EventBufferLength);
                var zeroCopyCompletions = new List<TSocket>(EventBufferLength);
                bool pipeReadable = false;

                CompleteStateChange(State.Started);
                bool running = true;
                do
                {
                    int numEvents = EPollInterop.EPollWait(epollFd, buffer, EventBufferLength, timeout: EPoll.TimeoutInfinite).Value;

                    // actions can be scheduled without unblocking epoll
                    threadContext.SetEpollNotBlocked();

                    // check events
                    // we don't handle them immediately:
                    // - this ensures we don't mismatch a closed socket with a new socket that have the same fd
                    //     ~ To have the same fd, the previous fd must be closed, which means it is removed from the epoll
                    //     ~ and won't show up in our next call to epoll.Wait.
                    //     ~ The old fd may be present in the buffer still, but lookup won't give a match, since it is removed
                    //     ~ from the dictionary before it is closed. If we were accepting already, a new socket could match.
                    // - this also improves cache/cpu locality of the lookup
                    int* ptr = buffer;
                    lock (sockets)
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
                            if (sockets.TryGetValue(key, out tsocket))
                            {
                                var type = tsocket.Type;
                                if (type == SocketFlags.TypeClient)
                                {
                                    lock (tsocket.EventLock)
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
                        zeroCopyCompletions[i].CompleteZeroCopy();
                    }
                    zeroCopyCompletions.Clear();

                    // handle accepts
                    statAcceptEvents += acceptableSockets.Count;
                    for (int i = 0; i < acceptableSockets.Count; i++)
                    {
                        statAccepts += HandleAccept(acceptableSockets[i], threadContext);
                    }
                    acceptableSockets.Clear();

                    // handle writes
                    statWriteEvents += writableSockets.Count;
                    for (int i = 0; i < writableSockets.Count; i++)
                    {
                        writableSockets[i].CompleteWritable();
                    }
                    writableSockets.Clear();

                    // handle reads
                    statReadEvents += readableSockets.Count;
                    for (int i = 0; i < readableSockets.Count; i++)
                    {
                        readableSockets[i].CompleteReadable();
                    }
                    readableSockets.Clear();

                    // reregister for events
                    for (int i = 0; i < reregisterEventSockets.Count; i++)
                    {
                        var tsocket = reregisterEventSockets[i];
                        lock (tsocket.EventLock)
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
                            result = readEnd.TryReadByte();
                            if (result.Value == PipeStopSockets)
                            {
                                StopSockets(threadContext.Sockets);
                            }
                            else if (result.Value == PipeStopThread)
                            {
                                running = false;
                            }
                        } while (result);
                        pipeReadable = false;
                    }

                    // scheduled work
                    threadContext.DoScheduledWork();

                } while (running);

                threadContext.Logger.LogInformation($"Thread {_threadId}: Stats A/AE:{statAccepts}/{statAcceptEvents} RE:{statReadEvents} WE:{statWriteEvents} ZCS/ZCC:{statZeroCopySuccess}/{statZeroCopyCopied}");
            }
            catch (Exception ex)
            {
                Environment.FailFast("TransportThread", ex);
            }
            finally
            {
                // We are not using SafeHandles for epoll to increase performance.
                // running == false when there are no more Sockets
                // so we are sure there are no more epoll users.
                threadContext?.Dispose();

                CompleteStateChange(State.Stopped);
            }
        }

        private static readonly IPAddress NotIPSocket = IPAddress.None;

        private static int HandleAccept(TSocket tacceptSocket, ThreadContext threadContext)
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

                    tsocket = new TSocket(threadContext, SocketFlags.TypeClient)
                    {
                        Fd = fd,
                        Socket = clientSocket,
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

                threadContext.ConnectionHandler.OnConnection(tsocket);

                var sockets = threadContext.Sockets;
                lock (sockets)
                {
                    sockets.Add(fd, tsocket);
                }

                WriteToSocket(tsocket);
                bool dataMayBeAvailable = (tacceptSocket.Flags & SocketFlags.DeferAccept) != 0;
                ReadFromSocket(tsocket, dataMayBeAvailable);

                return 1;
            }
            else
            {
                return 0;
            }
        }

        private static void CompleteWriteToSocket(Exception ex, object state)
        {
            if (ex != null)
            {
                var tsocket = (TSocket)state;
                tsocket.StopWriteToSocket();
            }
        }

        private static async void WriteToSocket(TSocket tsocket)
        {
            tsocket.Output.OnWriterCompleted(CompleteWriteToSocket, tsocket);
            try
            {
                while (true)
                {
                    var readResult = await tsocket.Output.ReadAsync();
                    ReadableBuffer buffer = readResult.Buffer;
                    ReadCursor end = buffer.Start;
                    try
                    {
                        if ((buffer.IsEmpty && readResult.IsCompleted) || readResult.IsCancelled)
                        {
                            // EOF or TransportThread stopped
                            break;
                        }
                        if (!buffer.IsEmpty)
                        {
                            bool zerocopy = buffer.Length >= tsocket.ZeroCopyThreshold;
                            (PosixResult result, bool zerocopyRegistered) = TrySend(tsocket, zerocopy, ref buffer);
                            if (result.Value == buffer.Length)
                            {
                                end = buffer.End;
                            }
                            else if (result.IsSuccess)
                            {
                                end = buffer.Move(buffer.Start, result.Value);
                            }
                            else if (result == PosixResult.EAGAIN || result == PosixResult.EWOULDBLOCK)
                            {
                                if (!await Writable(tsocket))
                                {
                                    // TransportThread stopped
                                    break;
                                }
                            }
                            else
                            {
                                tsocket.OutputError = result.AsException();
                                break;
                            }
                            if (result.Value > 0 && zerocopy)
                            {
                                if (!await ZeroCopyWritten(tsocket, zerocopyRegistered))
                                {
                                    // Don't advance when the zero-copy hasn't finished
                                    end = buffer.Start;
                                    break;
                                }
                            }
                        }
                    }
                    finally
                    {
                        // We need to call Advance to end the read
                        tsocket.Output.Advance(end);
                    }
                }
            }
            catch (Exception ex)
            {
                tsocket.OutputError = ex;
            }
            finally
            {
                tsocket.StopReadFromSocket();

                CleanupSocketEnd(tsocket);
            }
        }

        private static unsafe (PosixResult, bool zerocopyRegistered) TrySend(TSocket tsocket, bool zerocopy, ref ReadableBuffer buffer)
        {
            bool zeroCopyRegistered = false;
            int fd = tsocket.Fd;
            int ioVectorLength = 0;
            foreach (var memory in buffer)
            {
                if (memory.Length == 0)
                {
                    continue;
                }
                ioVectorLength++;
                if (ioVectorLength == MaxIOVectorSendLength)
                {
                    // No more room in the IOVector
                    break;
                }
            }
            if (ioVectorLength == 0)
            {
                return (new PosixResult(0), zeroCopyRegistered);
            }

            var ioVectors = stackalloc IOVector[ioVectorLength];
            int i = 0;
            foreach (var memory in buffer)
            {
                if (memory.Length == 0)
                {
                    continue;
                }
                var bufferHandle = memory.Retain(pin: true);
                ioVectors[i].Base = bufferHandle.Pointer;
                // It's ok to unpin the handle here because the memory is from the pool
                // we created, which is already pinned.
                bufferHandle.Dispose();
                ioVectors[i].Count = (void*)memory.Length;
                i++;
                if (i == ioVectorLength)
                {
                    // No more room in the IOVector
                    break;
                }
            }

            if (zerocopy)
            {
                // If we have a pending Readable event, it will report on the zero-copy completion too.
                lock (tsocket.EventLock)
                {
                    if ((tsocket.PendingEventState & EPollEvents.Readable) != EPollEvents.None)
                    {
                        tsocket.PendingEventState |= EPollEvents.Error;
                        zeroCopyRegistered = true;
                    }
                }
            }

            PosixResult rv = SocketInterop.Send(fd, ioVectors, ioVectorLength, zerocopy ? MSG_ZEROCOPY : 0);

            if (zerocopy && rv.Value <= 0 && zeroCopyRegistered)
            {
                lock (tsocket.EventLock)
                {
                    tsocket.PendingEventState &= ~EPollEvents.Error;
                }
                zeroCopyRegistered = false;
            }

            return (rv, zeroCopyRegistered);
        }

        private static WritableAwaitable Writable(TSocket tsocket) => new WritableAwaitable(tsocket);

        private static void RegisterForWritable(TSocket tsocket) => RegisterFor(tsocket, EPollEvents.Writable);

        private static ReadableAwaitable Readable(TSocket tsocket) => new ReadableAwaitable(tsocket);

        private static void RegisterForReadable(TSocket tsocket) => RegisterFor(tsocket, EPollEvents.Readable);

        private static ZeroCopyWrittenAwaitable ZeroCopyWritten(TSocket tsocket, bool registered) => new ZeroCopyWrittenAwaitable(tsocket, registered);

        private static void RegisterForZeroCopyWritten(TSocket tsocket) => RegisterFor(tsocket, EPollEvents.Error);

        private static void RegisterFor(TSocket tsocket, EPollEvents ev)
        {
            lock (tsocket.EventLock)
            {
                var pendingEventState = tsocket.PendingEventState;
                bool registered = (pendingEventState & TSocket.EventControlRegistered) != EPollEvents.None;
                pendingEventState |= TSocket.EventControlRegistered | ev;
                tsocket.PendingEventState = pendingEventState;

                if ((pendingEventState & TSocket.EventControlPending) == EPollEvents.None)
                {
                    UpdateEPollControl(tsocket, pendingEventState, registered);
                }
            }
        }

        // must be called under tsocket.EventLock
        private static void UpdateEPollControl(TSocket tsocket, EPollEvents flags, bool registered)
        {
            flags &= EPollEvents.Readable | EPollEvents.Writable | EPollEvents.Error;
            EPollInterop.EPollControl(tsocket.ThreadContext.EPollFd,
                        registered ? EPollOperation.Modify : EPollOperation.Add,
                        tsocket.Fd,
                        flags | EPollEvents.OneShot,
                        EPollData(tsocket.Fd));
        }

        private static async void ReadFromSocket(TSocket tsocket, bool dataMayBeAvailable)
        {
            Exception error = null;
            try
            {
                var availableBytes = dataMayBeAvailable ? tsocket.Socket.GetAvailableBytes() : 0;
                bool readable0 = true;
                if (availableBytes == 0
                 && (readable0 = await Readable(tsocket))) // Readable
                {
                    availableBytes = tsocket.Socket.GetAvailableBytes();
                }
                else if (!readable0)
                {
                    error = new ConnectionAbortedException();
                }
                while (availableBytes != 0)
                {
                    var buffer = tsocket.Input.Alloc(2048);
                    try
                    {
                        error = Receive(tsocket.Fd, availableBytes, ref buffer);
                        if (error != null)
                        {
                            break;
                        }
                        availableBytes = 0;
                        var flushResult = await buffer.FlushAsync();
                        bool readable = true;
                        if (!flushResult.IsCompleted // Reader hasn't stopped
                         && !flushResult.IsCancelled // TransportThread hasn't stopped
                         && (readable = await Readable(tsocket))) // Readable
                        {
                            availableBytes = tsocket.Socket.GetAvailableBytes();
                        }
                        else if (flushResult.IsCancelled || !readable)
                        {
                            error = new ConnectionAbortedException();
                        }
                    }
                    catch (Exception e)
                    {
                        availableBytes = 0;
                        buffer.Commit();
                        error = e;
                    }
                }
            }
            catch (Exception ex)
            {
                error = ex;
            }
            finally
            {
                // even when error == null, we call Abort
                // this mean receiving FIN causes Abort
                // rationale: https://github.com/aspnet/KestrelHttpServer/issues/1139#issuecomment-251748845
                tsocket.Input.Complete(error);

                CleanupSocketEnd(tsocket);
            }
        }

        private static unsafe Exception Receive(int fd, int availableBytes, ref WritableBuffer wb)
        {
            int ioVectorLength = availableBytes <= wb.Buffer.Length ? 1 :
                    Math.Min(1 + (availableBytes - wb.Buffer.Length + MaxPooledBlockLength - 1) / MaxPooledBlockLength, MaxIOVectorReceiveLength);
            var ioVectors = stackalloc IOVector[ioVectorLength];
            var allocated = 0;

            var advanced = 0;
            int ioVectorsUsed = 0;
            for (; ioVectorsUsed < ioVectorLength; ioVectorsUsed++)
            {
                wb.Ensure(1);
                var memory = wb.Buffer;
                var length = memory.Length;
                var bufferHandle = memory.Retain(pin: true);
                ioVectors[ioVectorsUsed].Base = bufferHandle.Pointer;
                ioVectors[ioVectorsUsed].Count = (void*)length;
                // It's ok to unpin the handle here because the memory is from the pool
                // we created, which is already pinned.
                bufferHandle.Dispose();
                allocated += length;

                if (allocated >= availableBytes)
                {
                    // Every Memory (except the last one) must be filled completely.
                    ioVectorsUsed++;
                    break;
                }

                wb.Advance(length);
                advanced += length;
            }
            var expectedMin = Math.Min(availableBytes, allocated);

            // Ideally we get availableBytes in a single receive
            // but we are happy if we get at least a part of it
            // and we are willing to take {MaxEAgainCount} EAGAINs.
            // Less data could be returned due to these reasons:
            // * TCP URG
            // * packet was not placed in receive queue (race with FIONREAD)
            // * ?
            const int MaxEAgainCount = 10;
            var eAgainCount = 0;
            var received = 0;
            do
            {
                var result = SocketInterop.Receive(fd, ioVectors, ioVectorsUsed);
                if (result.IsSuccess)
                {
                    received += result.Value;
                    if (received >= expectedMin)
                    {
                        // We made it!
                        wb.Advance(received - advanced);
                        return null;
                    }
                    eAgainCount = 0;
                    // Update ioVectors to match bytes read
                    var skip = result.Value;
                    for (int i = 0; (i < ioVectorsUsed) && (skip > 0); i++)
                    {
                        var length = (int)ioVectors[i].Count;
                        var skipped = Math.Min(skip, length);
                        ioVectors[i].Count = (void*)(length - skipped);
                        ioVectors[i].Base = (byte*)ioVectors[i].Base + skipped;
                        skip -= skipped;
                    }
                }
                else if (result == PosixResult.EAGAIN || result == PosixResult.EWOULDBLOCK)
                {
                    eAgainCount++;
                    if (eAgainCount == MaxEAgainCount)
                    {
                        return new NotSupportedException("Too many EAGAIN, unable to receive available bytes.");
                    }
                }
                else if (result == PosixResult.ECONNRESET)
                {
                    return new ConnectionResetException(result.ErrorDescription(), result.AsException());
                }
                else
                {
                    return result.AsException();
                }
            } while (true);
        }

        private static void CleanupSocketEnd(TSocket tsocket)
        {
            int fd;
            bool bothClosed = false;
            bool completeZeroCopy = false;
            lock (tsocket.EventLock)
            {
                bothClosed = tsocket.CloseEnd();
                if (!bothClosed)
                {
                    return;
                }

                fd = tsocket.Fd;
                // terminate pending zero copy
                if ((tsocket.PendingEventState & EPollEvents.Error) != EPollEvents.None)
                {
                    completeZeroCopy = true;
                    // Disconnect under lock and clear Error to ensure the EPoll thread will not try to complete this.
                    SocketInterop.Disconnect(fd);
                    tsocket.PendingEventState &= ~EPollEvents.Error;
                }
            }

            if (completeZeroCopy)
            {
                var result = SocketInterop.CompleteZeroCopyBlocking(fd, timeout: 60 * 1000 /* ms */);
                if (!result.IsSuccess)
                {
                    Environment.FailFast($"Could not terminate pending zerocopy: {result}");
                }
            }
            tsocket.Output.Complete(tsocket.OutputError);

            // First remove from the Dictionary, so we can't match with a new fd.
            tsocket.ThreadContext.RemoveSocket(fd);

            // We are not using SafeHandles to increase performance.
            // We get here when both reading and writing has stopped
            // so we are sure this is the last use of the Socket.
            tsocket.Socket.Dispose();
        }

        private void CloseAccept(ThreadContext threadContext, Dictionary<int, TSocket> sockets)
        {
            var acceptSockets = threadContext.AcceptSockets;
            lock (sockets)
            {
                for (int i = 0; i < acceptSockets.Count; i++)
                {
                    threadContext.RemoveSocket(acceptSockets[i].Fd);
                }
            }
            for (int i = 0; i < acceptSockets.Count; i++)
            {
                // close causes remove from epoll (CLOEXEC)
                acceptSockets[i].Socket.Dispose(); // will close (no concurrent users)
            }
            acceptSockets.Clear();
            CompleteStateChange(State.AcceptClosed);
        }

        private static void StopSockets(Dictionary<int, TSocket> sockets)
        {
            Dictionary<int, TSocket> clone;
            lock (sockets)
            {
                clone = new Dictionary<int, TSocket>(sockets);
            }
            foreach (var kv in clone)
            {
                var tsocket = kv.Value;
                tsocket.StopWriteToSocket();
                // this calls StopReadFromSocket
            }
        }

        private void ThrowInvalidState()
        {
            throw new InvalidOperationException($"nameof(TransportThread) is {_state}");
        }

        private void CompleteStateChange(State state)
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
                tcs?.SetResult(null);
            });
        }

        private static long EPollData(int fd) => (((long)(uint)fd) << 32) | (long)(uint)fd;
    }
}
