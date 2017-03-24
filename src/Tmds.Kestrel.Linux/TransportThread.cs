using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using Kestrel;
using Tmds.Posix;

namespace Tmds.Kestrel.Linux
{
    partial class TransportThread
    {
        private const int ListenBacklog     = 128;
        private const int EventBufferLength = 512;
        // Highest bit set in EPollData for writable poll
        // the remaining bits of the EPollData are the key
        // of the _sockets dictionary.
        private const int DupKeyMask        = 1 << 31;
        private static PipeOptions s_inputPipeOptions = new PipeOptions()
        {
            // Don't prefetch: ReadAsync receives new bytes when all previous bytes are read
            // Retrieving new bytes is synchronous with the ReadAsync call
            MaximumSizeHigh = 1,
            MaximumSizeLow =  1,
            WriterScheduler = InlineScheduler.Default,
            ReaderScheduler = InlineScheduler.Default,
        };
        private static PipeOptions s_outputPipeOptions = new PipeOptions()
        {
            // Let the OS buffer.
            // FlushAsync will return when the bytes are sent to the socket
            MaximumSizeHigh = 1,
            MaximumSizeLow = 1,
            WriterScheduler = InlineScheduler.Default,
            ReaderScheduler = InlineScheduler.Default,
        };

        enum State
        {
            Initial,
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
        private ReadStrategy _readStrategy;

        public TransportThread(IConnectionHandler connectionHandler, TransportOptions options)
        {
            if (connectionHandler == null)
            {
                throw new ArgumentNullException(nameof(connectionHandler));
            }
            _connectionHandler = connectionHandler;
            _deferAccept = options.DeferAccept;
            _readStrategy = options.ReadStrategy;
        }

        public void Start()
        {
            lock (_gate)
            {
                if (_state != State.Initial)
                {
                    ThrowInvalidState();
                }
                try
                {
                    _sockets = new ConcurrentDictionary<int, TSocket>();
                    _acceptSockets = new List<TSocket>();

                    _epoll = EPoll.Create();

                    _pipeEnds = PipeEnd.CreatePair(blocking: false);
                    var tsocket = new TSocket()
                    {
                        Flags = SocketFlags.TypePipe,
                        Key = _pipeEnds.ReadEnd.DangerousGetHandle().ToInt32()
                    };
                    _sockets.TryAdd(tsocket.Key, tsocket);
                    _epoll.Control(EPollOperation.Add, _pipeEnds.ReadEnd, EPollEvents.Readable, new EPollData { Int1 = tsocket.Key, Int2 = tsocket.Key });

                    _state = State.Started;
   
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
        }

        public void AcceptOn(System.Net.IPEndPoint endPoint)
        {
            lock (_gate)
            {
                if (_state != State.Started)
                {
                    ThrowInvalidState();
                }

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
                        acceptSocket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.IPv6Only, 1);
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
                    tsocket = new TSocket()
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
        }

        public Task CloseAcceptAsync()
        {
            TaskCompletionSource<object> tcs;
            lock (_gate)
            {
                if (_state == State.Initial)
                {
                    _state = State.Stopped;
                    return Task.CompletedTask;
                }
                else if (_state == State.AcceptClosed || _state == State.Stopping || _state == State.Stopped)
                {
                    return Task.CompletedTask;
                }
                else if (_state == State.ClosingAccept)
                {
                    return _stateChangeCompletion.Task;
                }
                else if (_state != State.Started)
                {
                    // Cannot happen
                    ThrowInvalidState();
                }
                tcs = _stateChangeCompletion = new TaskCompletionSource<object>();
                _state = State.ClosingAccept;
            }
            _pipeEnds.WriteEnd.WriteByte(0);
            return tcs.Task;
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
                _pipeEnds.WriteEnd.WriteByte(0);
            }
            await tcs.Task;
        }

        private unsafe void PollThread(object obj)
        {
            // TODO: add try catch
            bool notPacked = !EPoll.PackedEvents;
            var buffer = stackalloc int[EventBufferLength * (notPacked ? 4 : 3)];
            bool running = true;
            while (running)
            {
                bool doCloseAccept = false;
                int numEvents = _epoll.Wait(buffer, EventBufferLength, timeout: EPoll.TimeoutInfinite);
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
                    int events = *ptr++; // 0
                    ptr++;               // 1
                    int key = *ptr++;    // 2
                    if (notPacked)
                        ptr++;           // 3
                    TSocket tsocket;
                    if (_sockets.TryGetValue(key & ~DupKeyMask, out tsocket))
                    {
                        var type = tsocket.Flags & SocketFlags.TypeMask;
                        if (type == SocketFlags.TypeClient)
                        {
                            bool read = (key & DupKeyMask) == 0;
                            if (read)
                            {
                                tsocket.CompleteReadable();
                            }
                            else
                            {
                                tsocket.CompleteWritable();
                            }
                        }
                        else if (type == SocketFlags.TypeAccept && !doCloseAccept)
                        {
                            HandleAccept(tsocket);
                        }
                        else // TypePipe
                        {
                            HandleState(ref running, ref doCloseAccept);
                        }
                    }
                }
                if (doCloseAccept)
                {
                    CloseAccept();
                }
            }
            Stop();
        }

        private void HandleAccept(TSocket tacceptSocket)
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

                    tsocket = new TSocket()
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
                    return;
                }

                var connectionContext = _connectionHandler.OnConnection(
                        connectionInfo: tsocket,
                        inputOptions: s_inputPipeOptions,
                        outputOptions: s_outputPipeOptions);
                tsocket.PipeReader = connectionContext.Output;
                tsocket.PipeWriter = connectionContext.Input;

                _sockets.TryAdd(key, tsocket);

                WriteToSocket(tsocket, connectionContext.Output);
                bool dataMayBeAvailable = (tacceptSocket.Flags & SocketFlags.DeferAccept) != 0;
                if (_readStrategy == ReadStrategy.Fixed)
                {
                    ReadFromSocketFixed(tsocket, connectionContext.Input, dataMayBeAvailable);
                }
                else // ReadStrategy.Available
                {
                    ReadFromSocketAvailable(tsocket, connectionContext.Input, dataMayBeAvailable);
                }
            }
        }

        private async void WriteToSocket(TSocket tsocket, IPipeReader reader)
        {
            try
            {
                while (true)
                {
                    var readResult = await reader.ReadAsync();
                    var buffer = readResult.Buffer;
                    ReadCursor end = buffer.End;
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
                                end = buffer.Move(buffer.Start, result.Value);
                            }
                            else if (result == PosixResult.EAGAIN || result == PosixResult.EWOULDBLOCK)
                            {
                                if (await Writable(tsocket))
                                {
                                    end = buffer.Start;
                                }
                                else
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
            // 64 IOVectors, take up 1KB of stack, can send up to 256KB
            const int MaxIOVectorLength = 64;
            int ioVectorLength = 0;
            foreach (var memory in buffer)
            {
                if (memory.Length == 0)
                {
                    continue;
                }
                ioVectorLength++;
                if (ioVectorLength == MaxIOVectorLength)
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
                if (memory.TryGetPointer(out pointer))
                {
                    ioVectors[i].Base = (byte*)pointer;
                    ioVectors[i].Count = new UIntPtr((uint)memory.Length);
                }
                else
                {
                    throw new InvalidOperationException("Memory needs to be pinned");
                }
                i++;
                if (i == ioVectorLength)
                {
                    // No more room in the IOVector
                    break;
                }
            }
            return socket.TrySend(ioVectors, ioVectorLength);
        }

        private WritableAwaitable Writable(TSocket tsocket)
        {
            tsocket.ResetWritableAwaitable();
            bool registered = tsocket.DupSocket != null;
            // To avoid having to synchronize the event mask with the Readable
            // we dup the socket.
            // In the EPollData we set the highest bit to indicate this is the
            // poll for writable.
            if (!registered)
            {
                tsocket.DupSocket = tsocket.Socket.Duplicate();
            }
            _epoll.Control(registered ? EPollOperation.Modify : EPollOperation.Add,
                            tsocket.DupSocket,
                            EPollEvents.Writable | EPollEvents.OneShot,
                            new EPollData{ Int1 = tsocket.Key | DupKeyMask, Int2 = tsocket.Key | DupKeyMask } );
            return tsocket.WritableAwaitable;
        }

        private async void ReadFromSocketAvailable(TSocket tsocket, IPipeWriter writer, bool dataMayBeAvailable)
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

        private async void ReadFromSocketFixed(TSocket tsocket, IPipeWriter writer, bool dataMayBeAvailable)
        {
            try
            {
                bool eof = false;
                if (!dataMayBeAvailable)
                {
                    eof = !await Readable(tsocket);
                }
                while (!eof)
                {
                    var buffer = writer.Alloc();
                    try
                    {
                        var result = TryReceive(tsocket.Socket, ref buffer);
                        if (result.IsSuccess)
                        {
                            if (result.Value != 0)
                            {
                                buffer.Advance(result.Value);
                                var flushResult = await buffer.FlushAsync();
                                eof = flushResult.IsCompleted || flushResult.IsCancelled;
                            }
                            else
                            {
                                buffer.Commit();
                                eof = true;
                            }
                        }
                        else if (result == PosixResult.EAGAIN || result == PosixResult.EWOULDBLOCK)
                        {
                            buffer.Commit();
                        }
                        else
                        {
                            result.ThrowOnError();
                        }
                    }
                    catch
                    {
                        buffer.Commit();
                        throw;
                    }
                    if (!eof)
                    {
                        eof = !await Readable(tsocket);
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

        private unsafe PosixResult TryReceive(Socket socket, ref WritableBuffer wb)
        {
            IOVector ioVector;
            wb.Ensure(2048);
            var memory = wb.Buffer;
            var length = wb.Buffer.Length;
            void* pointer;
            if (!wb.Buffer.TryGetPointer(out pointer))
            {
                throw new InvalidOperationException("Pointer must be pinned");
            }
            ioVector.Base = (byte*)pointer;
            ioVector.Count = new UIntPtr((uint)length);
            return socket.TryReceive(&ioVector, 1);
        }

        private unsafe void Receive(Socket socket, int availableBytes, ref WritableBuffer wb)
        {
            // WritableBuffer doesn't support non-contiguous writes	
            // https://github.com/dotnet/corefxlab/issues/1233
            // We do it anyhow, but we need to make sure we fill every Memory we allocate.

            // 64 IOVectors, take up 1KB of stack, can receive up to 256KB
            const int MaxIOVectorLength = 64;
            const int bytesPerMemory    = 4096 - 64; // MemoryPool._blockStride - MemoryPool._blockUnused
            int ioVectorLength = availableBytes <= wb.Buffer.Length ? 1 :
                    Math.Min(1 + (availableBytes - wb.Buffer.Length + bytesPerMemory - 1) / bytesPerMemory, MaxIOVectorLength);
            var ioVectors = stackalloc IOVector[ioVectorLength];

            var allocated = 0;
            var advanced = 0;
            int ioVectorsUsed = 0;
            for (; ioVectorsUsed < ioVectorLength; ioVectorsUsed++)
            {
                wb.Ensure(1);
                var memory = wb.Buffer;
                var length = wb.Buffer.Length;
                void* pointer;
                if (!wb.Buffer.TryGetPointer(out pointer))
                {
                    throw new InvalidOperationException("Pointer must be pinned");
                }

                ioVectors[ioVectorsUsed].Base = (byte*)pointer;
                ioVectors[ioVectorsUsed].Count = new UIntPtr((uint)length);
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
                    uint skip = (uint)result.Value;
                    for (int i = 0; (i < ioVectorsUsed) && (skip > 0); i++)
                    {
                        var length = ioVectors[i].Count.ToUInt32();
                        var skipped = Math.Min(skip, length);
                        ioVectors[i].Count = new UIntPtr(length - skipped);
                        ioVectors[i].Base += skipped;
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

        private ReadableAwaitable Readable(TSocket tsocket)
        {
            tsocket.ResetReadableAwaitable();
            bool registered = (tsocket.Flags & SocketFlags.EPollRegistered) != 0;
            if (!registered)
            {
                tsocket.AddFlags(SocketFlags.EPollRegistered);
            }
            _epoll.Control(registered ? EPollOperation.Modify : EPollOperation.Add,
                            tsocket.Socket,
                            EPollEvents.Readable | EPollEvents.OneShot,
                            new EPollData{ Int1 = tsocket.Key, Int2 = tsocket.Key });
            return tsocket.ReadableAwaitable;
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

        private void Stop()
        {
            _epoll.BlockingDispose();

            var pipeReadKey = _pipeEnds.ReadEnd.DangerousGetHandle().ToInt32();
            TSocket pipeReadSocket;
            _sockets.TryRemove(pipeReadKey, out pipeReadSocket);
            _pipeEnds.Dispose();

            foreach (var kv in _sockets)
            {
                var tsocket = kv.Value;
                tsocket.PipeReader.CancelPendingRead();
                tsocket.PipeWriter.CancelPendingFlush();
                tsocket.CompleteReadable(stopping: true);
                tsocket.CompleteWritable(stopping: true);
            }

            TaskCompletionSource<object> tcs;
            lock (_gate)
            {
                tcs = _stateChangeCompletion;
                _stateChangeCompletion = null;
                _state = State.Stopped;
            }
            tcs.SetResult(null);
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
            TaskCompletionSource<object> tcs;
            lock (_gate)
            {
                tcs = _stateChangeCompletion;
                _stateChangeCompletion = null;
                _state = State.AcceptClosed;
            }
            tcs.SetResult(null);
        }

        private unsafe void HandleState(ref bool running, ref bool doCloseAccept)
        {
            _pipeEnds.ReadEnd.TryReadByte();
            lock (_gate)
            {
                if (_state == State.ClosingAccept)
                {
                    doCloseAccept = true;
                }
                else if (_state == State.Stopping)
                {
                    running = false;
                }
            }
        }

        private void ThrowInvalidState()
        {
            throw new InvalidOperationException($"nameof(TransportThread) is {_state}");
        }
    }
}