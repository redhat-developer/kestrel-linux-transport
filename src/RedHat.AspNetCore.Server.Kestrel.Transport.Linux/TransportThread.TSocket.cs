using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Runtime.CompilerServices;
using System.Threading;
using Tmds.Linux;
using Microsoft.AspNetCore.Connections;
using static Tmds.Linux.LibC;
using System.Threading.Tasks;

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
            private const int EventControlRegistered = (int)SocketFlags.EventControlRegistered;
            public const int EventControlPending = (int)SocketFlags.EventControlPending;

            // Copied from LibuvTransportOptions.MaxReadBufferSize
            private static readonly int PauseInputWriterThreashold = 1024 * 1024;
            // Copied from LibuvTransportOptions.MaxWriteBufferSize
            private static readonly int PauseOutputWriterThreashold = 64 * 1024;

            private readonly int MaxBufferSize;
            private readonly int BufferMargin;

            public readonly object         Gate = new object();
            private readonly ThreadContext _threadContext;
            public readonly int            Fd;
            private readonly Action       _onFlushedToApp;
            private readonly Action       _onReadFromApp;
            private readonly MemoryHandle[] _sendMemoryHandles;
            private readonly CancellationTokenSource _connectionClosedTokenSource;
            private readonly TaskCompletionSource<object> _waitForConnectionClosedTcs;

            public int                     ZeroCopyThreshold;

            private SocketFlags           _flags;
            private ValueTaskAwaiter<ReadResult>  _readAwaiter;
            private ValueTaskAwaiter<FlushResult> _flushAwaiter;
            private int                   _zeropCopyState;
            private SequencePosition      _zeroCopyEnd;
            private int                   _readState = CheckAvailable;

            public TSocket(ThreadContext threadContext, int fd, SocketFlags flags)
            {
                _threadContext = threadContext;

                MaxBufferSize = MemoryPool.MaxBufferSize;
                BufferMargin = MaxBufferSize / 4;

                Fd = fd;
                _flags = flags;
                _onFlushedToApp = new Action(OnFlushedToApp);
                _onReadFromApp = new Action(OnReadFromApp);
                _connectionClosedTokenSource = new CancellationTokenSource();
                ConnectionClosed = _connectionClosedTokenSource.Token;
                _waitForConnectionClosedTcs = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);

                if (!IsDeferSend)
                {
                    _sendMemoryHandles = new MemoryHandle[MaxIOVectorSendLength];
                }

                var inputOptions = new PipeOptions(MemoryPool, PipeScheduler.ThreadPool, PipeScheduler.Inline, PauseInputWriterThreashold, PauseInputWriterThreashold / 2, useSynchronizationContext: false);
                var outputOptions = new PipeOptions(MemoryPool, PipeScheduler.Inline, PipeScheduler.ThreadPool, PauseOutputWriterThreashold, PauseOutputWriterThreashold / 2, useSynchronizationContext: false);

                var pair = DuplexPipe.CreateConnectionPair(inputOptions, outputOptions);

                Transport = pair.Transport;
                Application = pair.Application;
            }

            public PipeWriter Input => Application.Output;

            public PipeReader Output => Application.Input;

            public bool IsDeferAccept => HasFlag(SocketFlags.DeferAccept);

            public bool IsDeferSend => HasFlag(SocketFlags.DeferSend);

            public SocketFlags Type => ((SocketFlags)_flags & SocketFlags.TypeMask);

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private bool HasFlag(SocketFlags flag) => HasFlag(_flags, flag);

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private bool HasFlag(SocketFlags flags, SocketFlags flag) => (_flags & flag) != 0;

            // must be called under Gate
            public int PendingEventState
            {
                get => (int)_flags;
                set => _flags = (SocketFlags)value;
            }

            public override void Abort()
            {
                CancelWriteToSocket();
            }

            public override async ValueTask DisposeAsync()
            {
                Transport.Input.Complete();
                Transport.Output.Complete();

                CompleteOutput(null);

                await _waitForConnectionClosedTcs.Task;
                _connectionClosedTokenSource.Dispose();
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
                    var ioVectors = stackalloc iovec[ioVectorLength];
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
                        RegisterFor(EPOLLERR);
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
                        RegisterFor(EPOLLOUT);
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
            private void RegisterFor(int ev)
            {
                // called under tsocket.Gate
                var pendingEventState = PendingEventState;
                bool registered = (pendingEventState & TSocket.EventControlRegistered) != 0;
                pendingEventState |= TSocket.EventControlRegistered | ev;
                PendingEventState = pendingEventState;

                if ((pendingEventState & TSocket.EventControlPending) == 0)
                {
                    _threadContext.UpdateEPollControl(this, pendingEventState, registered);
                }
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private void CleanupSocketEnd()
            {
                var hasCanceledConnectionClosedToken = false;

                lock (Gate)
                {
                    if (HasFlag(SocketFlags.CloseEnd))
                    {
                        hasCanceledConnectionClosedToken = true;
                    }

                    _flags = _flags + (int)SocketFlags.CloseEnd;
                    if (!HasFlag(SocketFlags.BothClosed))
                    {
                        return;
                    }
                }

                // First remove from the Dictionary, so we can't match with a new fd.
                bool lastSocket = _threadContext.RemoveSocket(Fd);

                // We are not using SafeHandles to increase performance.
                // We get here when both reading and writing has stopped
                // so we are sure this is the last use of the Socket.
                Close();

                if (!hasCanceledConnectionClosedToken)
                {
                    // Inform the application.
                    ThreadPool.UnsafeQueueUserWorkItem(state => ((TSocket)state).CancelConnectionClosedToken(), this);
                }

                if (lastSocket)
                {
                    _threadContext.StopThread();
                }
            }

            private void CancelConnectionClosedToken()
            {
                _connectionClosedTokenSource.Cancel();
                _waitForConnectionClosedTcs.SetResult(null);
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public unsafe int FillReceiveIOVector(ReceiveMemoryAllocation memoryAllocation, iovec* ioVectors, Span<MemoryHandle> handles)
            {
                PipeWriter writer = Input;
                int advanced = 0;
                Memory<byte> memory = writer.GetMemory(memoryAllocation.FirstMemorySize);
                int length = memory.Length;

                for (int i = 0; i < memoryAllocation.IovLength; i++)
                {
                    var bufferHandle = memory.Pin();
                    ioVectors[i].iov_base = bufferHandle.Pointer;
                    ioVectors[i].iov_len = length;
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
                var ioVectors = stackalloc iovec[memoryAllocation.IovLength];
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
            public unsafe (bool done, PosixResult receiveResult) InterpretReceiveResult(PosixResult result, ref int received, int advanced, iovec* ioVectors, int ioVectorLength)
            {
                PipeWriter writer = Input;
                if (result.IsSuccess)
                {
                    received += result.IntValue;
                    if (received >= advanced)
                    {
                        // We made it!
                        int finalAdvance = received - advanced;
                        int spaceRemaining = (int)(ioVectors[ioVectorLength - 1].iov_len) - finalAdvance;
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
                    var skip = (size_t)result.Value;
                    for (int i = 0; (i < ioVectorLength) && (skip > 0); i++)
                    {
                        var length = ioVectors[i].iov_len;
                        var skipped = skip < length ? skip : length;
                        ioVectors[i].iov_len = length - skipped;
                        ioVectors[i].iov_base = (byte*)ioVectors[i].iov_base + skipped;
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
                        RegisterFor(EPOLLIN);
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
            public unsafe void FillSendIOVector(ref ReadOnlySequence<byte> buffer, iovec* ioVectors, int ioVectorLength, Span<MemoryHandle> memoryHandles)
            {
                int i = 0;
                foreach (var memory in buffer)
                {
                    if (memory.Length == 0)
                    {
                        continue;
                    }
                    var bufferHandle = memory.Pin();
                    ioVectors[i].iov_base = bufferHandle.Pointer;
                    ioVectors[i].iov_len = memory.Length;
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
            private unsafe (PosixResult, bool zerocopyRegistered) TrySend(bool zerocopy, iovec* ioVectors, int ioVectorLength)
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
                        if ((PendingEventState & EPOLLIN) != 0)
                        {
                            PendingEventState |= EPOLLERR;
                            zeroCopyRegistered = true;
                        }
                    }
                }

                PosixResult rv = SocketInterop.Send(Fd, ioVectors, ioVectorLength, zerocopy ? MSG_ZEROCOPY : 0);

                if (zerocopy && rv.Value <= 0 && zeroCopyRegistered)
                {
                    lock (Gate)
                    {
                        PendingEventState &= ~EPOLLERR;
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

            public PosixResult TryReceiveSocket(out int socket, bool blocking)
                => SocketInterop.ReceiveSocket(Fd, out socket, blocking);

            public unsafe PosixResult TryAccept(out int socket, bool blocking)
                => SocketInterop.Accept(Fd, blocking, out socket);

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
                return result.IntValue;
            }

            public void SetSocketOption(int level, int optname, int value)
            {
                TrySetSocketOption(level, optname, value)
                    .ThrowOnError();
            }

            public unsafe PosixResult TrySetSocketOption(int level, int optname, int value)
            {
                return SocketInterop.SetSockOpt(Fd, level, optname, (byte*)&value, 4);
            }

            public unsafe void Bind(IPEndPointStruct endpoint)
            {
                sockaddr_storage addr;
                Socket.GetSockaddrInet(endpoint, &addr, out int length);

                int rv = bind(Fd, (sockaddr*)&addr, length);

                PosixResult.FromReturnValue(rv).ThrowOnError();
            }

            public void Listen(int backlog) => PosixResult.FromReturnValue(listen(Fd, backlog)).ThrowOnError();

            public void Close() => IOInterop.Close(Fd);

            public IPEndPointStruct GetLocalIPAddress(IPAddress reuseAddress = null)
            {
                IPEndPointStruct ep;
                TryGetLocalIPAddress(out ep, reuseAddress)
                    .ThrowOnError();
                return ep;
            }

            public unsafe PosixResult TryGetLocalIPAddress(out IPEndPointStruct ep, IPAddress reuseAddress = null)
                => SocketInterop.TryGetLocalIPAddress(Fd, out ep, reuseAddress);

            public unsafe PosixResult TryGetPeerIPAddress(out IPEndPointStruct ep)
                => SocketInterop.TryGetPeerIPAddress(Fd, out ep);

            // Copied from Kestrel's Libuv Transport
            internal class DuplexPipe : IDuplexPipe
            {
                public DuplexPipe(PipeReader reader, PipeWriter writer)
                {
                    Input = reader;
                    Output = writer;
                }

                public PipeReader Input { get; }

                public PipeWriter Output { get; }

                public static DuplexPipePair CreateConnectionPair(PipeOptions inputOptions, PipeOptions outputOptions)
                {
                    var input = new Pipe(inputOptions);
                    var output = new Pipe(outputOptions);

                    var transportToApplication = new DuplexPipe(output.Reader, input.Writer);
                    var applicationToTransport = new DuplexPipe(input.Reader, output.Writer);

                    return new DuplexPipePair(applicationToTransport, transportToApplication);
                }

                // This class exists to work around issues with value tuple on .NET Framework
                public readonly struct DuplexPipePair
                {
                    public IDuplexPipe Transport { get; }
                    public IDuplexPipe Application { get; }

                    public DuplexPipePair(IDuplexPipe transport, IDuplexPipe application)
                    {
                        Transport = transport;
                        Application = application;
                    }
                }
            }
        }
    }
}