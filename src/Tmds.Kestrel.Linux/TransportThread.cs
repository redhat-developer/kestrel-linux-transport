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
    sealed partial class TransportThread
    {
        private const int MaxPooledBlockLength = MemoryPool.MaxPooledBlockLength;
        // 32 IOVectors, take up 512B of stack, can send up to 128KB
        private const int MaxIOVectorSendLength = 32;
        // 32 IOVectors, take up 512B of stack, can receive up to 128KB
        private const int MaxIOVectorReceiveLength = 32;
        private const int MaxSendLength = MaxIOVectorSendLength * MaxPooledBlockLength;
        private const int ListenBacklog     = 128;
        private const int EventBufferLength = 512;
        private const int EPollBlocked      = 1;
        private const int EPollNotBlocked   = 0;
        // Highest bit set in EPollData for writable poll
        // the remaining bits of the EPollData are the key
        // of the _sockets dictionary.
        private const int DupKeyMask          = 1 << 31;
        private const byte PipeStopThread     = 0;
        private const byte PipeActionsPending = 1;

        private struct ScheduledAction
        {
            public Action Action;
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
        private readonly TransportOptions _transportOptions;
        private readonly ILoggerFactory _loggerFactory;
        private readonly object _gate = new object();
        private int _cpuId;
        private State _state;
        private Thread _thread;
        private TaskCompletionSource<object> _stateChangeCompletion;
        private ThreadContext _threadContext;

        public TransportThread(IPEndPoint endPoint, IConnectionHandler connectionHandler, TransportOptions options, int threadId, int cpuId, ILoggerFactory loggerFactory)
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
            _loggerFactory = loggerFactory;
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
                    tcs = _stateChangeCompletion = new TaskCompletionSource<object>();
                    _state = State.Starting;

                    // child will inherit parent affinity and will be running on right cpu
                    if (_cpuId != -1)
                    {
                        Scheduler.SetCurrentThreadAffinity(_cpuId);
                    }

                    _thread = new Thread(PollThread);
                    _thread.Start();

                    // reset parent affinity
                    if (_cpuId != -1)
                    {
                        Scheduler.ClearCurrentThreadAffinity();
                    }
                }
                catch
                {
                    _state = State.Stopped;
                    throw;
                }
            }
            return tcs.Task;
        }

        private static void AcceptOn(IPEndPoint endPoint, int cpuId, TransportOptions transportOptions, ThreadContext threadContext)
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
                tsocket = new TSocket(threadContext)
                {
                    Flags = flags,
                    Key = key,
                    Socket = acceptSocket
                };
                threadContext.AcceptSockets.Add(tsocket);
                threadContext.Sockets.TryAdd(tsocket.Key, tsocket);

                threadContext.EPoll.Control(EPollOperation.Add, acceptSocket, EPollEvents.Readable, new EPollData { Int1 = tsocket.Key, Int2 = tsocket.Key });
            }
            catch
            {
                acceptSocket.Dispose();
                threadContext.AcceptSockets.Remove(tsocket);
                threadContext.Sockets.TryRemove(key, out tsocket);
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
                _threadContext.Stop();
            }
            await tcs.Task;
        }

        private ILogger CreateLogger()
        {
            return _loggerFactory.CreateLogger("{nameof(TransportThread)}.{_threadId}");
        }

        private unsafe void PollThread(object obj)
        {
            // objects are allocated on the PollThread heap
            int pipeKey;
            var threadContext = new ThreadContext(this, _connectionHandler, CreateLogger());
            {
                // register pipe 
                pipeKey = threadContext.PipeEnds.ReadEnd.DangerousGetHandle().ToInt32();
                threadContext.EPoll.Control(EPollOperation.Add, threadContext.PipeEnds.ReadEnd, EPollEvents.Readable, new EPollData { Int1 = pipeKey, Int2 = pipeKey });
                // accept connections
                AcceptOn(_endPoint, _cpuId, _transportOptions, threadContext);

                _threadContext = threadContext;
                CompleteStateChange(State.Started);
            }

            var epoll = threadContext.EPoll;
            var readEnd = threadContext.PipeEnds.ReadEnd;
            bool notPacked = !EPoll.PackedEvents;
            var buffer = stackalloc int[EventBufferLength * (notPacked ? 4 : 3)];
            int statReadEvents = 0;
            int statWriteEvents = 0;
            int statAcceptEvents = 0;
            int statAccepts = 0;
            var sockets = threadContext.Sockets;
            bool running = true;
            do
            {
                int numEvents = epoll.Wait(buffer, EventBufferLength, timeout: EPoll.TimeoutInfinite);
                threadContext.SetEpollNotBlocked();
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
                        else
                        {
                            statAcceptEvents++;
                            statAccepts += HandleAccept(tsocket, threadContext);
                        }
                    }
                    else if (key == pipeKey)
                    {
                        PosixResult result;
                        do
                        {
                            result = readEnd.TryReadByte();
                            if (result.Value == PipeStopThread)
                            {
                                StopSockets(threadContext.Sockets);
                                running = false;
                            }
                        } while (result);
                    }
                }
                threadContext.DoScheduledWork();
            } while (running || sockets.Count != 0);

            threadContext.Logger.LogInformation($"Thread {_threadId}: Stats A/AE:{statAccepts}/{statAcceptEvents} RE:{statReadEvents} WE:{statWriteEvents}");

            threadContext.Dispose();

            CompleteStateChange(State.Stopped);
        }

        private static int HandleAccept(TSocket tacceptSocket, ThreadContext threadContext)
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

                    tsocket = new TSocket(threadContext)
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

                var connectionContext = threadContext.ConnectionHandler.OnConnection(tsocket);
                tsocket.PipeReader = connectionContext.Output;
                tsocket.PipeWriter = connectionContext.Input;

                threadContext.Sockets.TryAdd(key, tsocket);

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

        private static async void WriteToSocket(TSocket tsocket, IPipeReader reader)
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

        private static WritableAwaitable Writable(TSocket tsocket) => new WritableAwaitable(tsocket);

        private static void RegisterForWritable(TSocket tsocket)
        {
            var epoll = tsocket.ThreadContext.EPoll;
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

        private static ReadableAwaitable Readable(TSocket tsocket) => new ReadableAwaitable(tsocket);

        private static void RegisterForReadable(TSocket tsocket)
        {
            var epoll = tsocket.ThreadContext.EPoll;
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

        private static async void ReadFromSocket(TSocket tsocket, IPipeWriter writer, bool dataMayBeAvailable)
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

        private static unsafe void Receive(Socket socket, int availableBytes, ref WritableBuffer wb)
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

         private static void CleanupSocket(TSocket tsocket, SocketShutdown shutdown)
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
                    tsocket.ThreadContext.Sockets.TryRemove(tsocket.Key, out removedSocket);
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

        private void CloseAccept(List<TSocket> acceptSockets, ConcurrentDictionary<int, TSocket> sockets)
        {
            foreach (var acceptSocket in acceptSockets)
            {
                TSocket removedSocket;
                sockets.TryRemove(acceptSocket.Key, out removedSocket);
                // close causes remove from epoll (CLOEXEC)
                acceptSocket.Socket.Dispose(); // will close (no concurrent users)
            }
            acceptSockets.Clear();
            CompleteStateChange(State.AcceptClosed);
        }

        private static void StopSockets(ConcurrentDictionary<int, TSocket> sockets)
        {
            var clone = new Dictionary<int, TSocket>(sockets);
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
    }
}
