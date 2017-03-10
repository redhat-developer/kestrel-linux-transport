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
        private const int ListenBacklog = 128;
        private const int EventBufferLength = 512;
        private static PipeOptions DefaultPipeOptions = new PipeOptions();

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
        private ConcurrentDictionary<int, TSocket> _sockets;
        private List<TSocket> _acceptSockets;
        private EPoll _epoll;
        private PipeEndPair _pipeEnds;
        private Thread _thread;
        private TaskCompletionSource<object> _stateChangeCompletion;

        public TransportThread(IConnectionHandler connectionHandler, TransportOptions options)
        {
            if (connectionHandler == null)
            {
                throw new ArgumentNullException(nameof(connectionHandler));
            }
            _connectionHandler = connectionHandler;
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
                        Flags = SocketFlags.TypeAccept,
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
                    if (_sockets.TryGetValue(key, out tsocket))
                    {
                        var type = tsocket.Flags & SocketFlags.TypeMask;
                        if (type == SocketFlags.TypeAccept && !doCloseAccept)
                        {
                            HandleAccept(tsocket);
                        }
                        else if (type == SocketFlags.TypeClient)
                        {
                            HandleClient(tsocket, (EPollEvents)events);
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

        private void HandleClient(TSocket tsocket, EPollEvents events)
        {
            if ((events & EPollEvents.Error) != 0)
            {
                events |= EPollEvents.Readable | EPollEvents.Writable;
            }
            lock (tsocket)
            {
                // Check what events were registered
                events = events & (EPollEvents)(SocketFlags.EPollEventMask & tsocket.Flags);
                // Clear those events
                tsocket.Flags &= ~(SocketFlags)events;
                // Re-arm
                EPollEvents remaining = (EPollEvents)(tsocket.Flags & SocketFlags.EPollEventMask);
                if (remaining != 0)
                {
                    _epoll.Control(EPollOperation.Modify, tsocket.Socket, remaining | EPollEvents.OneShot, new EPollData() { Int1 = tsocket.Key, Int2 = tsocket.Key });
                }
            }
            if ((events & EPollEvents.Readable) != 0)
            {
                tsocket.CompleteReadable();
            }
            if ((events & EPollEvents.Writable) != 0)
            {
                tsocket.CompleteWritable();
            }
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
                        inputOptions: DefaultPipeOptions,
                        outputOptions: DefaultPipeOptions);
                tsocket.PipeReader = connectionContext.Output;

                _sockets.TryAdd(key, tsocket);

                ReadFromSocket(tsocket, connectionContext.Input);
                WriteToSocket(tsocket, connectionContext.Output);
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
                        if (buffer.IsEmpty && readResult.IsCompleted)
                        {
                            // EOF
                            break;
                        }

                        if (readResult.IsCancelled)
                        {
                            // TransportThread is stopping
                            break;
                        }

                        if (!buffer.IsEmpty)
                        {
                            var result = TrySend(tsocket.Socket, ref buffer);
                            if (result.IsSuccess)
                            {
                                if (result.Value != 0)
                                {
                                    end = buffer.Move(buffer.Start, result.Value);
                                }
                            }
                            else if (result == PosixResult.EAGAIN || result == PosixResult.EWOULDBLOCK)
                            {
                                bool stopping = await Writable(tsocket);
                                if (stopping)
                                {
                                    // TransportThread is stopping
                                    break;
                                }
                                else
                                {
                                    end = buffer.Start;
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
            // 1KB ioVectors, At +-4KB per Memory -> 256KB send
            const int MaxIOVecLength = 64;

            int ioVecAllocate = 0;
            foreach (var memory in buffer)
            {
                if (memory.Length == 0) // It happens...
                {
                    continue;
                }
                ioVecAllocate++;
                if (ioVecAllocate == MaxIOVecLength)
                {
                    break;
                }
            }
            if (ioVecAllocate == 0)
            {
                return new PosixResult(0);
            }
            var ioVectors = stackalloc IOVector[ioVecAllocate];
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
            }

            return socket.TrySend(ioVectors, ioVecAllocate);
        }

        private WritableAwaitable Writable(TSocket tsocket)
        {
            tsocket.ResetWritableAwaitable();
            RegisterForEvent(tsocket, EPollEvents.Writable);
            return tsocket.WritableAwaitable;
        }

        private async void ReadFromSocket(TSocket tsocket, IPipeWriter writer)
        {
            try
            {
                while (true)
                {
                    bool stopping = await Readable(tsocket);
                    if (stopping)
                    {
                        // TransportThread is stopping
                        break;
                    }

                    var availableBytes = tsocket.Socket.GetAvailableBytes();
                    if (availableBytes == 0)
                    {
                        // EOF
                        break;
                    }

                    // TODO: allocate to receive availableBytes
                    // WritableBuffer doesn't support non-contiguous writes
                    // https://github.com/dotnet/corefxlab/issues/1233
                    var buffer = writer.Alloc(2048);
                    try
                    {
                        // We need to call FlushAsync or Commit to end the write

                        var result = TryReceive(tsocket.Socket, availableBytes, ref buffer);
                        if (result.IsSuccess)
                        {
                            if (result.Value == 0)
                            {
                                // EOF
                                buffer.Commit();
                                break;
                            }
                            else
                            {
                                buffer.Advance(result.Value);
                                bool readerComplete = !await buffer.FlushAsync();
                                if (readerComplete)
                                {
                                    // The reader has stopped
                                    break;
                                }
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

        private unsafe PosixResult TryReceive(Socket socket, int availableBytes, ref WritableBuffer buffer)
        {
            void* pointer;
            if (!buffer.Memory.TryGetPointer(out pointer))
            {
                throw new InvalidOperationException("Pointer must be pinned");
            }
            IOVector ioVector = new IOVector { Base = (byte*) pointer, Count = new UIntPtr((uint)buffer.Memory.Length) };
            return socket.TryReceive(&ioVector, 1);
        }

        private ReadableAwaitable Readable(TSocket tsocket)
        {
            tsocket.ResetReadableAwaitable();
            RegisterForEvent(tsocket, EPollEvents.Readable);
            return tsocket.ReadableAwaitable;
        }

        private void RegisterForEvent(TSocket tsocket, EPollEvents ev)
        {
            lock (tsocket)
            {
                if (_state == State.Stopping)
                {
                    ThrowInvalidState();
                }
                SocketFlags oldFlags = tsocket.Flags;
                try
                {
                    EPollOperation operation = (oldFlags & SocketFlags.EPollRegistered) == 0 ? EPollOperation.Add : EPollOperation.Modify;
                    tsocket.Flags |= SocketFlags.EPollRegistered | (SocketFlags)ev;
                    EPollEvents events = (EPollEvents)(tsocket.Flags & SocketFlags.EPollEventMask);
                    _epoll.Control(operation, tsocket.Socket, events | EPollEvents.OneShot, new EPollData() { Int1 = tsocket.Key, Int2 = tsocket.Key });
                }
                catch
                {
                    tsocket.Flags = oldFlags;
                    throw;
                }
            }
        }

        private void CleanupSocket(TSocket tsocket, SocketShutdown shutdown)
        {
            lock (tsocket)
            {
                bool close = false;
                if (shutdown == SocketShutdown.Send)
                {
                    tsocket.Flags |= SocketFlags.ShutdownSend;
                    close = (tsocket.Flags & SocketFlags.ShutdownReceive) != 0;
                }
                else
                {
                    tsocket.Flags |= SocketFlags.ShutdownReceive;
                    close = (tsocket.Flags & SocketFlags.ShutdownSend) != 0;
                }

                if (close)
                {
                    TSocket removedSocket;
                    _sockets.TryRemove(tsocket.Key, out removedSocket);
                    // close causes remove from epoll (CLOEXEC)
                    tsocket.Socket.Dispose(); // will close (no concurrent users)
                }
                else
                {
                    tsocket.Socket.TryShutdown(shutdown);
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
                EPollEvents pending = EPollEvents.None;
                lock (tsocket)
                {
                    pending = (EPollEvents)(tsocket.Flags & SocketFlags.EPollEventMask);
                    tsocket.Flags &= ~(SocketFlags)pending;
                }
                if ((pending & EPollEvents.Readable) != 0)
                {
                    tsocket.CompleteReadable(stopping: true);
                }
                if ((pending & EPollEvents.Writable) != 0)
                {
                    tsocket.CompleteWritable(stopping: true);
                }
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