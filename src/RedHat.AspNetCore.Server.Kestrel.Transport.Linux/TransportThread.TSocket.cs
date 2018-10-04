using System;
using System.Buffers;
using System.Collections;
using System.IO.Pipelines;
using System.Net;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Abstractions.Internal;

namespace RedHat.AspNetCore.Server.Kestrel.Transport.Linux
{
    sealed partial class TransportThread
    {
        [Flags]
        enum SocketFlags
        {
            None            = 0,

            AwaitReadable = 0x01,    // EPOLLIN
            AwaitWritable = 0x04,    // EPOLLOUT
            AwaitZeroCopy = 0x08,    // EPOLLERR
            EventControlRegistered = 0x10, // EPOLLHUP
            EventControlPending = 1 << 30, // EPOLLONESHOT

            CloseEnd        = 0x20,
            BothClosed      = 0x40,

            TypeAccept      = 0x100,
            TypeClient      = 0x200,
            TypePassFd      = 0x300,
            TypeMask        = 0x300,

            DeferAccept     = 0x400,
            WriteCanceled    = 0x1000,
            ReadCanceled     = 0x2000,

            DeferSend       = 0x4000
        }

        class TSocket : TransportConnection
        {
            public struct ReceiveMemoryAllocation
            {
                public int FirstMemorySize;
                public int IovLength;
            }

            private const int ZeroCopyNone = 0;
            private const int ZeroCopyComplete = 1;
            private const int ZeroCopyAwait = 2;
            private const int MSG_ZEROCOPY = 0x4000000;
            private const int CheckAvailable = -1;
            private const int CheckAvailableIgnoreReceived = -2;
            // 8 IOVectors, take up 128B of stack, can receive/send up to 32KB
            public const int MaxIOVectorSendLength = 8;
            public const int MaxIOVectorReceiveLength = 8;
            private const EPollEvents EventControlRegistered = (EPollEvents)SocketFlags.EventControlRegistered;
            public const EPollEvents EventControlPending = (EPollEvents)SocketFlags.EventControlPending;

            private static readonly int MaxBufferSize = KestrelMemoryPool.MinimumSegmentSize;
            private static readonly int BufferMargin = MaxBufferSize / 4;

            public readonly object         Gate = new object();
            private readonly ThreadContext _threadContext;
            public readonly int            Fd;
            private readonly Action       _onFlushedToApp;
            private readonly Action       _onReadFromApp;
            private readonly MemoryHandle[] _sendMemoryHandles;
            private readonly CancellationTokenSource _connectionClosedTokenSource;

            public int                     ZeroCopyThreshold;

            private SocketFlags           _flags;
            private ValueTaskAwaiter<ReadResult>  _readAwaiter;
            private ValueTaskAwaiter<FlushResult> _flushAwaiter;
            private int                   _zeropCopyState;
            private SequencePosition      _zeroCopyEnd;
            private long                  _totalBytesWritten;
            private int                   _readState = CheckAvailable;
            public  Task                   MiddlewareTask;

            public TSocket(ThreadContext threadContext, int fd, SocketFlags flags)
            {
                _threadContext = threadContext;
                Fd = fd;
                _flags = flags;
                _onFlushedToApp = new Action(OnFlushedToApp);
                _onReadFromApp = new Action(OnReadFromApp);
                _connectionClosedTokenSource = new CancellationTokenSource();
                ConnectionClosed = _connectionClosedTokenSource.Token;
                if (!IsDeferSend)
                {
                    _sendMemoryHandles = new MemoryHandle[MaxIOVectorSendLength];
                }
            }

            public override long TotalBytesWritten => Interlocked.Read(ref _totalBytesWritten);

            public bool IsDeferAccept => HasFlag(SocketFlags.DeferAccept);

            public bool IsDeferSend => HasFlag(SocketFlags.DeferSend);

            public SocketFlags Type => ((SocketFlags)_flags & SocketFlags.TypeMask);

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private bool HasFlag(SocketFlags flag) => HasFlag(_flags, flag);

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private bool HasFlag(SocketFlags flags, SocketFlags flag) => (_flags & flag) != 0;

            // must be called under Gate
            public EPollEvents PendingEventState
            {
                get => (EPollEvents)_flags;
                set => _flags = (SocketFlags)value;
            }

            public override void Abort()
            {
                CancelWriteToSocket();
            }

            private void CancelWriteToSocket()
            {
                bool completeWritable = false;
                lock (Gate)
                {
                    var flags = _flags;
                    if (HasFlag(flags, SocketFlags.WriteCanceled))
                    {
                        return;
                    }
                    if (HasFlag(flags, SocketFlags.AwaitWritable))
                    {
                        completeWritable = true;
                    }
                    if (HasFlag(flags, SocketFlags.AwaitZeroCopy))
                    {
                        // Terminate pending zero copy
                        // Call it under Gate so it doesn't race with Close
                        SocketInterop.Disconnect(Fd);
                    }
                    flags &= ~SocketFlags.AwaitWritable;
                    flags |= SocketFlags.WriteCanceled;
                    _flags = flags;
                }
                if (completeWritable)
                {
                    OnWritable(stopped: true);
                }
            }

            private void CancelReadFromSocket()
            {
                bool completeReadable = false;
                lock (Gate)
                {
                    var flags = _flags;
                    if (HasFlag(flags, SocketFlags.ReadCanceled))
                    {
                        return;
                    }
                    if (HasFlag(flags, SocketFlags.AwaitReadable))
                    {
                        completeReadable = true;
                    }
                    flags &= ~SocketFlags.AwaitReadable;
                    flags |= SocketFlags.ReadCanceled;
                    _flags = flags;
                }
                if (completeReadable)
                {
                    CompleteInput(new ConnectionAbortedException());
                }
            }

            private void ReadFromApp()
            {
                bool deferSend = IsDeferSend;
                bool loop = !deferSend;
                do
                {
                    _readAwaiter = Output.ReadAsync().GetAwaiter();
                    if (_readAwaiter.IsCompleted)
                    {
                        if (deferSend)
                        {
                            _threadContext.ScheduleSend(this);
                        }
                        else
                        {
                            loop = OnReadFromApp(loop, _sendMemoryHandles);
                        }
                    }
                    else
                    {
                        _readAwaiter.UnsafeOnCompleted(_onReadFromApp);
                        loop = false;
                    }
                } while (loop);
            }

            private void OnReadFromApp()
            {
                if (IsDeferSend)
                {
                    _threadContext.ScheduleSend(this);
                }
                else
                {
                    OnReadFromApp(loop: false, _sendMemoryHandles);
                }
            }

            public void DoDeferedSend(Span<MemoryHandle> memoryHandles)
            {
                OnReadFromApp(loop: false, memoryHandles);
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public Exception GetReadResult(out ReadOnlySequence<byte> buffer)
            {
                try
                {
                    ReadResult readResult = _readAwaiter.GetResult();
                    buffer = readResult.Buffer;
                    if ((buffer.IsEmpty && readResult.IsCompleted) || readResult.IsCanceled)
                    {
                        // EOF or TransportThread stopped
                        return TransportConstants.StopSentinel;
                    }
                    else
                    {
                        return null;
                    }
                }
                catch (Exception e)
                {
                    buffer = default(ReadOnlySequence<byte>);
                    return e;
                }
            }

            private unsafe bool OnReadFromApp(bool loop, Span<MemoryHandle> memoryHandles)
            {
                ReadOnlySequence<byte> buffer;
                Exception error = GetReadResult(out buffer);
                if (error != null)
                {
                    if (error == TransportConstants.StopSentinel)
                    {
                        error = null;
                    }
                    CompleteOutput(error);
                    return false;
                }
                else
                {
                    int ioVectorLength = CalcIOVectorLengthForSend(ref buffer, MaxIOVectorSendLength);
                    var ioVectors = stackalloc IOVector[ioVectorLength];
                    FillSendIOVector(ref buffer, ioVectors, ioVectorLength, memoryHandles);
                    bool zerocopy = buffer.Length >= ZeroCopyThreshold;

                    (PosixResult result, bool zeroCopyRegistered) = TrySend(zerocopy, ioVectors, ioVectorLength);

                    for (int i = 0; i < ioVectorLength; i++)
                    {
                        memoryHandles[i].Dispose();
                    }

                    return HandleSendResult(ref buffer, result, loop, zerocopy, zeroCopyRegistered);
                }
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public bool HandleSendResult(ref ReadOnlySequence<byte> buffer, PosixResult result, bool loop, bool zerocopy, bool zeroCopyRegistered)
            {
                SequencePosition end;
                if (result.Value == buffer.Length)
                {
                    end = buffer.End;
                }
                else if (result.IsSuccess)
                {
                    end = buffer.GetPosition(result.Value);
                }
                else if (result == PosixResult.EAGAIN)
                {
                    Output.AdvanceTo(buffer.Start);
                    WaitSocketWritable();
                    return false;
                }
                else if (zerocopy && result == PosixResult.ENOBUFS)
                {
                    // We reached the max locked memory (ulimit -l), disable zerocopy.
                    end = buffer.Start;
                    ZeroCopyThreshold = LinuxTransportOptions.NoZeroCopy;
                }
                else
                {
                    CompleteOutput(result.AsException());
                    return false;
                }
                if (zerocopy && result.Value > 0)
                {
                    _zeroCopyEnd = end;
                    return WaitZeroCopyComplete(loop, zeroCopyRegistered);
                }
                if (result.Value > 0)
                {
                    Interlocked.Add(ref _totalBytesWritten, result.Value);
                }
                // We need to call Advance to end the read
                Output.AdvanceTo(end);
                if (!loop)
                {
                    ReadFromApp();
                }
                return loop;
            }

            private bool WaitZeroCopyComplete(bool loop, bool registered)
            {
                if (registered)
                {
                    int previousState = Interlocked.CompareExchange(ref _zeropCopyState, ZeroCopyAwait, ZeroCopyNone);
                    if (previousState == ZeroCopyComplete)
                    {
                        // registered, complete
                        return FinishZeroCopy(loop);
                    }
                    else
                    {
                        // registered, not completed
                        return false;
                    }
                }
                else
                {
                    // not registered
                    lock (Gate)
                    {
                        RegisterFor(EPollEvents.Error);
                    }
                    return false;
                }
            }

            public void OnZeroCopyCompleted()
            {
                int previousState = Interlocked.CompareExchange(ref _zeropCopyState, ZeroCopyAwait, ZeroCopyNone);
                if (previousState == ZeroCopyAwait)
                {
                    FinishZeroCopy(loop: false);
                }
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private bool FinishZeroCopy(bool loop)
            {
                Volatile.Write(ref _zeropCopyState, ZeroCopyNone);
                Output.AdvanceTo(_zeroCopyEnd);
                _zeroCopyEnd = default(SequencePosition);
                if (!loop)
                {
                    ReadFromApp();
                }
                return loop;
            }

            public void CompleteOutput(Exception outputError)
            {
                Output.Complete(outputError);
                CancelReadFromSocket();
                CleanupSocketEnd();
            }

            private void WaitSocketWritable()
            {
                bool stopped = false;
                lock (Gate)
                {
                    stopped = HasFlag(SocketFlags.WriteCanceled);
                    if (!stopped)
                    {
                        RegisterFor(EPollEvents.Writable);
                    }
                }
                if (stopped)
                {
                    OnWritable(true);
                }
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void OnWritable(bool stopped)
            {
                if (stopped)
                {
                    CompleteOutput(null);
                }
                else
                {
                    ReadFromApp();
                }
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private void RegisterFor(EPollEvents ev)
            {
                // called under tsocket.Gate
                var pendingEventState = PendingEventState;
                bool registered = (pendingEventState & TSocket.EventControlRegistered) != EPollEvents.None;
                pendingEventState |= TSocket.EventControlRegistered | ev;
                PendingEventState = pendingEventState;

                if ((pendingEventState & TSocket.EventControlPending) == EPollEvents.None)
                {
                    _threadContext.UpdateEPollControl(this, pendingEventState, registered);
                }
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private async void CleanupSocketEnd()
            {
                lock (Gate)
                {
                    _flags = _flags + (int)SocketFlags.CloseEnd;
                    if (!HasFlag(SocketFlags.BothClosed))
                    {
                        return;
                    }
                }

                // First remove from the Dictionary, so we can't match with a new fd.
                _threadContext.RemoveSocket(Fd);

                // We are not using SafeHandles to increase performance.
                // We get here when both reading and writing has stopped
                // so we are sure this is the last use of the Socket.
                Close();

                // Inform the application.
                ThreadPool.UnsafeQueueUserWorkItem(state => ((TSocket)state).CancelConnectionClosedToken(), this);

                // Only called after connection middleware is complete which means the ConnectionClosed token has fired.
                await MiddlewareTask;
                _connectionClosedTokenSource.Dispose();
            }

            private void CancelConnectionClosedToken()
            {
                _connectionClosedTokenSource.Cancel();
                _connectionClosedTokenSource.Dispose();
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public unsafe int FillReceiveIOVector(ReceiveMemoryAllocation memoryAllocation, IOVector* ioVectors, Span<MemoryHandle> handles)
            {
                PipeWriter writer = Input;
                int advanced = 0;
                Memory<byte> memory = writer.GetMemory(memoryAllocation.FirstMemorySize);
                int length = memory.Length;

                for (int i = 0; i < memoryAllocation.IovLength; i++)
                {
                    var bufferHandle = memory.Pin();
                    ioVectors[i].Base = bufferHandle.Pointer;
                    ioVectors[i].Count = (void*)length;
                    handles[i] = bufferHandle;

                    // Every Memory (except the last one) must be filled completely.
                    if (i != (memoryAllocation.IovLength - 1))
                    {
                        writer.Advance(length);
                        advanced += length;
                        memory = writer.GetMemory(MaxBufferSize);
                        length = MaxBufferSize;
                    }
                }

                return advanced;
            }

            public unsafe PosixResult Receive(Span<MemoryHandle> handles)
            {
                ReceiveMemoryAllocation memoryAllocation = DetermineMemoryAllocationForReceive(MaxIOVectorReceiveLength);
                var ioVectors = stackalloc IOVector[memoryAllocation.IovLength];
                int advanced = FillReceiveIOVector(memoryAllocation, ioVectors, handles);

                try
                {
                    // Ideally we get availableBytes in a single receive
                    // but we are happy if we get at least a part of it
                    // and we are willing to take {MaxEAgainCount} EAGAINs.
                    // Less data could be returned due to these reasons:
                    // * TCP URG
                    // * packet was not placed in receive queue (race with FIONREAD)
                    // * ?
                    var eAgainCount = 0;
                    var received = 0;
                    do
                    {
                        var result = SocketInterop.Receive(Fd, ioVectors, memoryAllocation.IovLength);
                        (bool done, PosixResult retval) = InterpretReceiveResult(result, ref received, advanced, ioVectors, memoryAllocation.IovLength);
                        if (done)
                        {
                            return retval;
                        }
                        else if (retval == PosixResult.EAGAIN)
                        {
                            eAgainCount++;
                            if (eAgainCount == TransportConstants.MaxEAgainCount)
                            {
                                return TransportConstants.TooManyEAgain;
                            }
                        }
                        else
                        {
                            eAgainCount = 0;
                        }
                    } while (true);
                }
                finally
                {
                    for (int i = 0; i < memoryAllocation.IovLength; i++)
                    {
                        handles[i].Dispose();
                    }
                }
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public unsafe (bool done, PosixResult receiveResult) InterpretReceiveResult(PosixResult result, ref int received, int advanced, IOVector* ioVectors, int ioVectorLength)
            {
                PipeWriter writer = Input;
                if (result.IsSuccess)
                {
                    received += result.Value;
                    if (received >= advanced)
                    {
                        // We made it!
                        int finalAdvance = received - advanced;
                        int spaceRemaining = (int)(ioVectors[ioVectorLength - 1].Count) - finalAdvance;
                        if (spaceRemaining == 0)
                        {
                            // We used up all room, assume there is a remainder to be read.
                            _readState = CheckAvailableIgnoreReceived;
                        }
                        else
                        {
                            if (_readState == CheckAvailableIgnoreReceived)
                            {
                                // We've read the remainder.
                                _readState = CheckAvailable;
                            }
                            else
                            {
                                _readState = received;
                            }
                        }
                        writer.Advance(finalAdvance);
                        return (true, new PosixResult(received == 0 ? 0 : 1));
                    }
                    // Update ioVectors to match bytes read
                    var skip = result.Value;
                    for (int i = 0; (i < ioVectorLength) && (skip > 0); i++)
                    {
                        var length = (int)ioVectors[i].Count;
                        var skipped = Math.Min(skip, length);
                        ioVectors[i].Count = (void*)(length - skipped);
                        ioVectors[i].Base = (byte*)ioVectors[i].Base + skipped;
                        skip -= skipped;
                    }
                    return (false, new PosixResult(1));
                }
                else if (result == PosixResult.EAGAIN)
                {
                    return (advanced == 0, result);
                }
                else if (result == PosixResult.ECONNRESET)
                {
                    return (true, result);
                }
                else
                {
                    return (true, result);
                }
            }

            private void ReceiveFromSocket()
            {
                bool stopped = false;
                lock (Gate)
                {
                    stopped = HasFlag(SocketFlags.ReadCanceled);
                    if (!stopped)
                    {
                        RegisterFor(EPollEvents.Readable);
                    }
                }
                if (stopped)
                {
                    CompleteInput(new ConnectionAbortedException());
                }
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void OnReceiveFromSocket(PosixResult result)
            {
                if (result.Value == 0)
                {
                    // EOF
                    CompleteInput(null);
                }
                else if (result.IsSuccess)
                {
                    // Data received
                    FlushToApp();
                }
                else if (result == PosixResult.EAGAIN)
                {
                    // EAGAIN
                    ReceiveFromSocket();
                }
                else
                {
                    // Error
                    Exception error;
                    if (result == PosixResult.ECONNRESET)
                    {
                        error = new ConnectionResetException(result.ErrorDescription(), result.AsException());
                    }
                    else if (result == TransportConstants.TooManyEAgain)
                    {
                        error = new NotSupportedException("Too many EAGAIN, unable to receive available bytes.");
                    }
                    else
                    {
                        error = result.AsException();
                    }
                    CompleteInput(error);
                }
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private void FlushToApp()
            {
                _flushAwaiter = Input.FlushAsync().GetAwaiter();
                if (_flushAwaiter.IsCompleted)
                {
                    OnFlushedToApp();
                }
                else
                {
                    _flushAwaiter.UnsafeOnCompleted(_onFlushedToApp);
                }
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private void OnFlushedToApp()
            {
                Exception error = null;
                try
                {
                    FlushResult flushResult = _flushAwaiter.GetResult();
                    if (flushResult.IsCompleted || // Reader has stopped
                        flushResult.IsCanceled)   // TransportThread has stopped
                    {
                        error = TransportConstants.StopSentinel;
                    }
                }
                catch (Exception e)
                {
                    error = e;
                }
                if (error == null)
                {
                    ReceiveFromSocket();
                }
                else
                {
                    if (error == TransportConstants.StopSentinel)
                    {
                        error = null;
                    }
                    CompleteInput(error);
                }
            }

            private void CompleteInput(Exception error)
            {
                Input.Complete(error);

                CleanupSocketEnd();
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public int CalcIOVectorLengthForSend(ref ReadOnlySequence<byte> buffer, int maxIOVectorSendLength)
            {
                int ioVectorLength = 0;
                foreach (var memory in buffer)
                {
                    if (memory.Length == 0)
                    {
                        continue;
                    }
                    ioVectorLength++;
                    if (ioVectorLength == maxIOVectorSendLength)
                    {
                        // No more room in the IOVector
                        break;
                    }
                }
                return ioVectorLength;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public unsafe void FillSendIOVector(ref ReadOnlySequence<byte> buffer, IOVector* ioVectors, int ioVectorLength, Span<MemoryHandle> memoryHandles)
            {
                int i = 0;
                foreach (var memory in buffer)
                {
                    if (memory.Length == 0)
                    {
                        continue;
                    }
                    var bufferHandle = memory.Pin();
                    ioVectors[i].Base = bufferHandle.Pointer;
                    ioVectors[i].Count = (void*)memory.Length;
                    memoryHandles[i] = bufferHandle;
                    i++;
                    if (i == ioVectorLength)
                    {
                        // No more room in the IOVector
                        break;
                    }
                }
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private unsafe (PosixResult, bool zerocopyRegistered) TrySend(bool zerocopy, IOVector* ioVectors, int ioVectorLength)
            {
                bool zeroCopyRegistered = false;

                if (zerocopy)
                {
                    lock (Gate)
                    {
                        // Don't start new zerocopies when writting stopped.
                        if (HasFlag(SocketFlags.WriteCanceled))
                        {
                            return (new PosixResult(PosixResult.ECONNABORTED), zeroCopyRegistered);
                        }

                        // If we have a pending Readable event, it will report on the zero-copy completion too.
                        if ((PendingEventState & EPollEvents.Readable) != EPollEvents.None)
                        {
                            PendingEventState |= EPollEvents.Error;
                            zeroCopyRegistered = true;
                        }
                    }
                }

                PosixResult rv = SocketInterop.Send(Fd, ioVectors, ioVectorLength, zerocopy ? MSG_ZEROCOPY : 0);

                if (zerocopy && rv.Value <= 0 && zeroCopyRegistered)
                {
                    lock (Gate)
                    {
                        PendingEventState &= ~EPollEvents.Error;
                    }
                    zeroCopyRegistered = false;
                }

                return (rv, zeroCopyRegistered);
            }

            public void Start(bool dataMayBeAvailable)
            {
                ReadFromApp();
                // TODO: implement dataMayBeAvailable
                ReceiveFromSocket();
            }

            public override MemoryPool<byte> MemoryPool => _threadContext.MemoryPool;

            public override PipeScheduler InputWriterScheduler => PipeScheduler.Inline;

            public override PipeScheduler OutputReaderScheduler => PipeScheduler.Inline;

            public PosixResult TryReceiveSocket(out int socket, bool blocking)
                => SocketInterop.ReceiveSocket(Fd, out socket, blocking);

            public unsafe PosixResult TryAccept(out int socket, bool blocking)
                => SocketInterop.Accept(Fd, null, 0, blocking, out socket);

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public ReceiveMemoryAllocation DetermineMemoryAllocationForReceive(int maxIovLength)
            {
                // In this function we try to avoid the 'GetAvailableBytes' system call.
                // If we read a small amount previously, we assume a single buffer is enough.
                int reserve = 0;
                int state = _readState;
                if (state > 0) // state is amount of bytes read previously
                {
                    // Make a guess based on what we read previously.
                    if (state + BufferMargin <= MaxBufferSize)
                    {
                        reserve = state + BufferMargin;
                    }
                    else if (state <= MaxBufferSize)
                    {
                        reserve = MaxBufferSize;
                    }
                }
                if (reserve == 0)
                {
                    // We didn't make a guess, get the available bytes.
                    reserve = GetAvailableBytes();
                    if (reserve == 0)
                    {
                        reserve = MaxBufferSize / 2;
                    }
                }
                // We need to make sure we can at least fill (IovLength -1) IOVs.
                // So if we are guessing, we can only return 1 IOV.
                // If we read GetAvailableBytes, we can return 1 IOV more than we need exactly.
                if (reserve <= MaxBufferSize)
                {
                    return new ReceiveMemoryAllocation { FirstMemorySize = reserve, IovLength = 1 };
                }
                else
                {
                    Memory<byte> memory =  Input.GetMemory(MaxBufferSize / 2);
                    int firstMemory = memory.Length;
                    int iovLength = Math.Min(1 + (reserve - memory.Length + MaxBufferSize - 1) / MaxBufferSize, maxIovLength);
                    return new ReceiveMemoryAllocation { FirstMemorySize = firstMemory, IovLength = iovLength };
                }
            }

            public int GetAvailableBytes()
            {
                PosixResult result = SocketInterop.GetAvailableBytes(Fd);
                result.ThrowOnError();
                return result.Value;
            }

            public void SetSocketOption(SocketOptionLevel optionLevel, SocketOptionName optionName, int value)
            {
                TrySetSocketOption(optionLevel, optionName, value)
                    .ThrowOnError();
            }

            public unsafe PosixResult TrySetSocketOption(SocketOptionLevel optionLevel, SocketOptionName optionName, int value)
            {
                return SocketInterop.SetSockOpt(Fd, optionLevel, optionName, (byte*)&value, 4);
            }

            public unsafe void Bind(IPEndPointStruct endpoint)
            {
                IPSocketAddress socketAddress = new IPSocketAddress(endpoint);
                SocketInterop.Bind(Fd, (byte*)&socketAddress, sizeof(IPSocketAddress)).ThrowOnError();
            }

            public void Listen(int backlog) => SocketInterop.Listen(Fd, backlog).ThrowOnError();

            public void Close() => IOInterop.Close(Fd);

            public IPEndPointStruct GetLocalIPAddress(IPAddress reuseAddress = null)
            {
                IPEndPointStruct ep;
                TryGetLocalIPAddress(out ep, reuseAddress)
                    .ThrowOnError();
                return ep;
            }

            public unsafe PosixResult TryGetLocalIPAddress(out IPEndPointStruct ep, IPAddress reuseAddress = null)
            {
                IPSocketAddress socketAddress;
                var rv = SocketInterop.GetSockName(Fd, (byte*)&socketAddress, sizeof(IPSocketAddress));
                if (rv.IsSuccess)
                {
                    ep = socketAddress.ToIPEndPoint(reuseAddress);
                }
                else
                {
                    ep = default(IPEndPointStruct);
                }
                return rv;
            }

            public unsafe PosixResult TryGetPeerIPAddress(out IPEndPointStruct ep)
            {
                IPSocketAddress socketAddress;
                var rv = SocketInterop.GetPeerName(Fd, (byte*)&socketAddress, sizeof(IPSocketAddress));
                if (rv.IsSuccess)
                {
                    ep = socketAddress.ToIPEndPoint();
                }
                else
                {
                    ep = default(IPEndPointStruct);
                }
                return rv;
            }
        }
    }
}