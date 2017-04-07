using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Server.Kestrel.Internal.System.IO.Pipelines;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Abstractions;
using Microsoft.Extensions.Logging;
using Tmds.Posix;

namespace Tmds.Kestrel.Linux
{
    sealed partial class TransportThread : IScheduler
    {
        private const int MaxPooledBlockLength = MemoryPool.MaxPooledBlockLength;
        // 32 IOVectors, take up 512B of stack, can send up to 128KB
        private const int MaxIOVectorSendLength = 32;
        // 32 IOVectors, take up 512B of stack, can receive up to 128KB
        private const int MaxIOVectorReceiveLength = 32;
        internal const int MaxSendLength = MaxIOVectorSendLength * MaxPooledBlockLength;
        private const int ListenBacklog     = 128;
        private const int EventBufferLength = 512;
        private const int EPollBlocked      = 1;
        private const int EPollNotBlocked   = 0;
        // Highest bit set in EPollData for writable poll
        // the remaining bits of the EPollData are the key
        // of the _sockets dictionary.
        private const int DupKeyMask        = 1 << 31;
        private const byte PipeStateChange    = 0;
        private const byte PipeActionsPending = 1;

        private struct ScheduledAction
        {
            public Action Action;
        }
        private readonly object _schedulerGate = new object();
        private Queue<ScheduledAction> _schedulerAdding;
        private Queue<ScheduledAction> _schedulerRunning;

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

        private State _state;
        private readonly object _gate = new object();
        // key is the file descriptor
        private ConcurrentDictionary<int, TSocket> _sockets;
        private List<TSocket> _acceptSockets;
        private EPoll _epoll;
        private PipeEndPair _pipeEnds;
        private Thread _thread;
        private TaskCompletionSource<object> _stateChangeCompletion;
        private bool _deferAccept;
        private int _threadId;
        private int _cpuId;
        private bool _receiveOnIncomingCpu;
        private PipeFactory _pipeFactory;
        private ILogger _logger;
        private IPEndPoint _endPoint;
        private int _writes;
        private int _epollState;

        public TransportThread(IPEndPoint endPoint, IConnectionHandler connectionHandler, TransportOptions options, int threadId, int cpuId, ILogger logger)
        {
            if (connectionHandler == null)
            {
                throw new ArgumentNullException(nameof(connectionHandler));
            }
            _connectionHandler = connectionHandler;
            _deferAccept = options.DeferAccept;
            _threadId = threadId;
            _cpuId = cpuId;
            _receiveOnIncomingCpu = options.ReceiveOnIncomingCpu;
            _logger = logger;
            _endPoint = endPoint;
        }

        public Task StartAsync()
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
                    _sockets = new ConcurrentDictionary<int, TSocket>();
                    _acceptSockets = new List<TSocket>();
                    _schedulerAdding = new Queue<ScheduledAction>(1024);
                    _schedulerRunning = new Queue<ScheduledAction>(1024);
                    _epoll = EPoll.Create();

                    _pipeEnds = PipeEnd.CreatePair(blocking: false);
                    int key = _pipeEnds.ReadEnd.DangerousGetHandle().ToInt32();
                    _epoll.Control(EPollOperation.Add, _pipeEnds.ReadEnd, EPollEvents.Readable, new EPollData { Int1 = key, Int2 = key });

                    tcs = _stateChangeCompletion = new TaskCompletionSource<object>();
                    _state = State.Starting;

                    _thread = new Thread(PollThread);
                    _thread.Start();
                }
                catch
                {
                    _state = State.Stopped;
                    _epoll?.Dispose();
                    _pipeEnds.Dispose();
                    throw;
                }
            }
            return tcs.Task;
        }

        private void AcceptOn(IPEndPoint endPoint)
        {
            Socket acceptSocket = null;
            int key = 0;
            int port = endPoint.Port;
            SocketFlags flags = SocketFlags.TypeAccept;
            try
            {
                bool ipv4 = endPoint.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork;
                acceptSocket = Socket.Create(ipv4 ? AddressFamily.InterNetwork : AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp, blocking: false);
                key = acceptSocket.DangerousGetHandle().ToInt32();
                if (!ipv4)
                {
                    // Don't do mapped ipv4
                    acceptSocket.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.IPv6Only, 1);
                }
                if (_receiveOnIncomingCpu)
                {
                    if (_cpuId != -1)
                    {
                        if (!acceptSocket.TrySetSocketOption(SocketOptionLevel.Socket, SocketOptionName.IncomingCpu, _cpuId))
                        {
                            _logger.LogWarning($"Thread {_threadId}: Cannot enable nameof{SocketOptionName.IncomingCpu} for {endPoint}");
                        }
                    }
                }
                // Linux: allow bind during linger time
                acceptSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, 1);
                // Linux: allow concurrent binds and let the kernel do load-balancing
                acceptSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReusePort, 1);
                if (_deferAccept)
                {
                    // Linux: wait up to 1 sec for data to arrive before accepting socket
                    acceptSocket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.DeferAccept, 1);
                    flags |= SocketFlags.DeferAccept;
                }

                acceptSocket.Bind(endPoint);
                if (port == 0)
                {
                    // When testing we want the OS to select a free port
                    port = acceptSocket.GetLocalIPAddress().Port;
                }

                acceptSocket.Listen(ListenBacklog);
            }
            catch
            {
                acceptSocket?.Dispose();
                throw;
            }

            TSocket tsocket = null;
            try
            {
                tsocket = new TSocket(this)
                {
                    Flags = flags,
                    Key = key,
                    Socket = acceptSocket
                };
                _acceptSockets.Add(tsocket);
                _sockets.TryAdd(tsocket.Key, tsocket);

                _epoll.Control(EPollOperation.Add, acceptSocket, EPollEvents.Readable, new EPollData { Int1 = tsocket.Key, Int2 = tsocket.Key });
            }
            catch
            {
                acceptSocket.Dispose();
                _acceptSockets.Remove(tsocket);
                _sockets.TryRemove(key, out tsocket);
                throw;
            }
            endPoint.Port = port;
        }

        public async Task CloseAcceptAsync()
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
                await StartAsync();
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
                _pipeEnds.WriteEnd.WriteByte(PipeStateChange);
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
                else if (_state == State.Stopped)
                {
                    return;
                }
            }

            await CloseAcceptAsync();

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
                _pipeEnds.WriteEnd.WriteByte(PipeStateChange);
            }
            await tcs.Task;
        }

        private unsafe void PollThread(object obj)
        {
            if (_cpuId != -1)
            {
                if (!Scheduler.TrySetCurrentThreadAffinity(_cpuId))
                {
                    _logger.LogWarning($"Thread {_threadId}: Failed to set Thread Affinity");
                    _cpuId = -1;
                }
                else
                {
                    _logger.LogInformation($"Thread {_threadId}: Bound to CPU {_cpuId}");
                }
            }

            AcceptOn(_endPoint);

            CompleteStateChange(State.Started);

            _pipeFactory = new PipeFactory();

            bool notPacked = !EPoll.PackedEvents;
            var buffer = stackalloc int[EventBufferLength * (notPacked ? 4 : 3)];

            int statReadEvents = 0;
            int statWriteEvents = 0;
            int statAcceptEvents = 0;
            int statAccepts = 0;
            var readEnd = _pipeEnds.ReadEnd;
            int pipeKey = readEnd.DangerousGetHandle().ToInt32();
            var epoll = _epoll;
            var sockets = _sockets;

            bool running = true;
            bool accepting = true;
            do
            {
                int numEvents = epoll.Wait(buffer, EventBufferLength, timeout: EPoll.TimeoutInfinite);
                Volatile.Write(ref _epollState, EPollNotBlocked);
                int* ptr = buffer;
                for (int i = 0; i < numEvents; i++)
                {
                    //   Packed             Non-Packed
                    //   ------             ------
                    // 0:Events       ==    Events
                    // 1:Int1 = Key         [Padding]
                    // 2:Int2 = Key   ==    Int1 = Key
                    // 3:~~~~~~~~~~         Int2 = Key
                    //                      ~~~~~~~~~~
                    int key = ptr[2];
                    ptr += notPacked ? 4 : 3;
                    TSocket tsocket;
                    if (sockets.TryGetValue(key & ~DupKeyMask, out tsocket))
                    {
                        var type = tsocket.Flags & SocketFlags.TypeMask;
                        if (type == SocketFlags.TypeClient)
                        {
                            bool read = (key & DupKeyMask) == 0;
                            if (read)
                            {
                                statReadEvents++;
                                tsocket.CompleteReadable();
                            }
                            else
                            {
                                statWriteEvents++;
                                tsocket.CompleteWritable();
                            }
                        }
                        else if (accepting)
                        {
                            statAcceptEvents++;
                            statAccepts += HandleAccept(tsocket);
                        }
                    }
                    else if (key == pipeKey)
                    {
                        PosixResult result;
                        do
                        {
                            result = readEnd.TryReadByte();
                            if (result.Value == PipeStateChange)
                            {
                                HandleState(ref running, ref accepting);
                            }
                        } while (result);
                    }
                }
                DoScheduledWork();
            } while (running || sockets.Count != 0);

            _logger.LogInformation($"Thread {_threadId}: Stats A/AE:{statAccepts}/{statAcceptEvents} RE:{statReadEvents} W:{_writes} WE:{statWriteEvents}");

            _epoll.Dispose();
            _pipeEnds.Dispose();
            _pipeFactory.Dispose();

            CompleteStateChange(State.Stopped);
        }

        private void DoScheduledWork()
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
                _pipeEnds.WriteEnd.WriteByte(PipeActionsPending);
            }
        }

        private int HandleAccept(TSocket tacceptSocket)
        {
            // TODO: should we handle more than 1 accept? If we do, we shouldn't be to eager
            //       as that might give the kernel the impression we have nothing to do
            //       which could interfere with the SO_REUSEPORT load-balancing.
            Socket clientSocket;
            var result = tacceptSocket.Socket.TryAccept(out clientSocket, blocking: false);
            if (result.IsSuccess)
            {
                int key;
                TSocket tsocket;
                try
                {
                    key = clientSocket.DangerousGetHandle().ToInt32();

                    tsocket = new TSocket(this)
                    {
                        Flags = SocketFlags.TypeClient,
                        Key = key,
                        Socket = clientSocket,
                        PeerAddress = clientSocket.GetPeerIPAddress(),
                        LocalAddress = clientSocket.GetLocalIPAddress()
                    };

                    clientSocket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.NoDelay, 1);
                }
                catch
                {
                    clientSocket.Dispose();
                    return 0;
                }

                var connectionContext = _connectionHandler.OnConnection(tsocket);
                tsocket.PipeReader = connectionContext.Output;
                tsocket.PipeWriter = connectionContext.Input;

                _sockets.TryAdd(key, tsocket);

                WriteToSocket(tsocket, connectionContext.Output);
                bool dataMayBeAvailable = (tacceptSocket.Flags & SocketFlags.DeferAccept) != 0;
                ReadFromSocket(tsocket, connectionContext.Input, dataMayBeAvailable);

                return 1;
            }
            else
            {
                return 0;
            }
        }

        private async void WriteToSocket(TSocket tsocket, IPipeReader reader)
        {
            try
            {
                while (true)
                {
                    var readResult = await reader.ReadAsync();
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
                            Interlocked.Increment(ref _writes);
                            var result = TrySend(tsocket.Socket, ref buffer);
                            if (result.IsSuccess && result.Value != 0)
                            {
                                end = result.Value == buffer.Length ? buffer.End : buffer.Move(buffer.Start, result.Value);
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
                                result.ThrowOnError();
                            }
                        }
                    }
                    finally
                    {
                        // We need to call Advance to end the read
                        reader.Advance(end);
                    }
                }
                reader.Complete();
            }
            catch (Exception ex)
            {
                reader.Complete(ex);
            }
            finally
            {
                CleanupSocket(tsocket, SocketShutdown.Send);
            }
        }

        private static unsafe PosixResult TrySend(Socket socket, ref ReadableBuffer buffer)
        {
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
                return new PosixResult(0);
            }

            var ioVectors = stackalloc IOVector[ioVectorLength];
            int i = 0;
            foreach (var memory in buffer)
            {
                if (memory.Length == 0)
                {
                    continue;
                }
                void* pointer;
                memory.TryGetPointer(out pointer);
                ioVectors[i].Base = pointer;
                ioVectors[i].Count = (void*)memory.Length;
                i++;
                if (i == ioVectorLength)
                {
                    // No more room in the IOVector
                    break;
                }
            }
            return socket.TrySend(ioVectors, ioVectorLength);
        }

        private WritableAwaitable Writable(TSocket tsocket) => new WritableAwaitable(tsocket, _epoll);

        private static void RegisterForWritable(TSocket tsocket, EPoll epoll)
        {
            bool registered = tsocket.DupSocket != null;
            // To avoid having to synchronize the event mask with the Readable
            // we dup the socket.
            // In the EPollData we set the highest bit to indicate this is the
            // poll for writable.
            if (!registered)
            {
                tsocket.DupSocket = tsocket.Socket.Duplicate();
            }
            epoll.Control(registered ? EPollOperation.Modify : EPollOperation.Add,
                            tsocket.DupSocket,
                            EPollEvents.Writable | EPollEvents.OneShot,
                            new EPollData{ Int1 = tsocket.Key | DupKeyMask, Int2 = tsocket.Key | DupKeyMask } );
        }

        private ReadableAwaitable Readable(TSocket tsocket) => new ReadableAwaitable(tsocket, _epoll);

        private static void RegisterForReadable(TSocket tsocket, EPoll epoll)
        {
            bool registered = (tsocket.Flags & SocketFlags.EPollRegistered) != 0;
            if (!registered)
            {
                tsocket.AddFlags(SocketFlags.EPollRegistered);
            }
            epoll.Control(registered ? EPollOperation.Modify : EPollOperation.Add,
                tsocket.Socket,
                EPollEvents.Readable | EPollEvents.OneShot,
                new EPollData{ Int1 = tsocket.Key, Int2 = tsocket.Key });
        }

        private async void ReadFromSocket(TSocket tsocket, IPipeWriter writer, bool dataMayBeAvailable)
        {
            try
            {
                var availableBytes = dataMayBeAvailable ? tsocket.Socket.GetAvailableBytes() : 0;
                if (availableBytes == 0
                 && await Readable(tsocket)) // Readable
                {
                    availableBytes = tsocket.Socket.GetAvailableBytes();
                }
                while (availableBytes != 0)
                {
                    var buffer = writer.Alloc(2048);
                    try
                    {
                        Receive(tsocket.Socket, availableBytes, ref buffer);
                        availableBytes = 0;
                        var flushResult = await buffer.FlushAsync();
                        if (!flushResult.IsCompleted // Reader hasn't stopped
                         && !flushResult.IsCancelled // TransportThread hasn't stopped
                         && await Readable(tsocket)) // Readable
                        {
                            availableBytes = tsocket.Socket.GetAvailableBytes();
                        }
                    }
                    catch
                    {
                        buffer.Commit();
                        throw;
                    }
                }
                writer.Complete();
            }
            catch (Exception ex)
            {
                writer.Complete(ex);
            }
            finally
            {
                CleanupSocket(tsocket, SocketShutdown.Receive);
            }
        }

        private unsafe void Receive(Socket socket, int availableBytes, ref WritableBuffer wb)
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
                void* pointer;
                wb.Buffer.TryGetPointer(out pointer);

                ioVectors[ioVectorsUsed].Base = pointer;
                ioVectors[ioVectorsUsed].Count = (void*)length;
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
                var result = socket.TryReceive(ioVectors, ioVectorsUsed);
                if (result.IsSuccess)
                {
                    received += result.Value;
                    if (received >= expectedMin)
                    {
                        // We made it!
                        wb.Advance(received - advanced);
                        return;
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
                        throw new NotSupportedException("Too many EAGAIN, unable to receive available bytes.");
                    }
                }
                else
                {
                    result.ThrowOnError();
                }
            } while (true);
        }

         private void CleanupSocket(TSocket tsocket, SocketShutdown shutdown)
        {
            // One caller will end up calling Shutdown, the other will call Dispose.
            // To ensure the Shutdown is executed against an open file descriptor
            // we manually increment/decrement the refcount on the safehandle.
            // We need to use a CER (Constrainted Execution Region) to ensure
            // the refcount is decremented.

            // This isn't available in .NET Core 1.x
            // RuntimeHelpers.PrepareConstrainedRegions();
            try
            { }
            finally
            {
                bool releaseRef = false;
                tsocket.Socket.DangerousAddRef(ref releaseRef);
                var previous = tsocket.AddFlags(shutdown == SocketShutdown.Send ? SocketFlags.ShutdownSend : SocketFlags.ShutdownReceive);
                var other = shutdown == SocketShutdown.Send ? SocketFlags.ShutdownReceive : SocketFlags.ShutdownSend;
                var close = (previous & other) != 0;
                if (close)
                {
                    TSocket removedSocket;
                    _sockets.TryRemove(tsocket.Key, out removedSocket);
                    tsocket.Socket.Dispose();
                    tsocket.DupSocket?.Dispose();
                }
                else
                {
                    tsocket.Socket.TryShutdown(shutdown);
                }
                // when CleanupSocket finished for both ends
                // the close will be invoked by the next statement
                // causing removal from the epoll
                if (releaseRef)
                {
                    tsocket.Socket.DangerousRelease();
                }
            }
        }

        private void CloseAccept()
        {
            foreach (var acceptSocket in _acceptSockets)
            {
                TSocket removedSocket;
                _sockets.TryRemove(acceptSocket.Key, out removedSocket);
                // close causes remove from epoll (CLOEXEC)
                acceptSocket.Socket.Dispose(); // will close (no concurrent users)
            }
            _acceptSockets.Clear();
            CompleteStateChange(State.AcceptClosed);
        }

        private unsafe void HandleState(ref bool running, ref bool accepting)
        {
            lock (_gate)
            {
                if (_state == State.ClosingAccept && accepting)
                {
                    accepting = false;
                    CloseAccept();
                }
                else if (_state == State.Stopping && running)
                {
                    running = false;
                    StopSockets();
                }
            }
        }

        private void StopSockets()
        {
            var clone = new Dictionary<int, TSocket>(_sockets);
            foreach (var kv in clone)
            {
                var tsocket = kv.Value;
                tsocket.PipeReader.CancelPendingRead();
                tsocket.PipeWriter.CancelPendingFlush();
                tsocket.CancelReadable();
                tsocket.CancelWritable();
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
            ThreadPool.QueueUserWorkItem(o => tcs.SetResult(null));
        }

        public void Schedule(Action action)
        {
            int epollState;
            lock (_schedulerGate)
            {
                epollState = Interlocked.CompareExchange(ref _epollState, EPollNotBlocked, EPollBlocked);
                _schedulerAdding.Enqueue(new ScheduledAction { Action = action });
            }
            if (epollState == EPollBlocked)
            {
                _pipeEnds.WriteEnd.WriteByte(PipeActionsPending);
            }
        }
    }
}
