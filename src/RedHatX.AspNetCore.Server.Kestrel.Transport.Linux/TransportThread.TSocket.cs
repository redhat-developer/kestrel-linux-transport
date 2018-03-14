using System;
using System.Buffers;
using System.Collections;
using System.IO.Pipelines;
using System.Net;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Protocols;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Abstractions.Internal;

namespace RedHatX.AspNetCore.Server.Kestrel.Transport.Linux
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
            private const int ZeroCopyNone = 0;
            private const int ZeroCopyComplete = 1;
            private const int ZeroCopyAwait = 2;
            private const int MinAllocBufferSize = 2048;
            private const int MSG_ZEROCOPY = 0x4000000;
            // 128 IOVectors, take up 2KB of stack, can receive/send up to 512KB
            private const int MaxIOVectorSendLength = 128;
            private const int MaxIOVectorReceiveLength = 128;
            private const EPollEvents EventControlRegistered = (EPollEvents)SocketFlags.EventControlRegistered;
            public const EPollEvents EventControlPending = (EPollEvents)SocketFlags.EventControlPending;

            private static readonly int MaxPooledBlockLength;
            static TSocket()
            {
                using (var memoryPool = KestrelMemoryPool.Create())
                {
                    MaxPooledBlockLength = memoryPool.MaxBufferSize;
                }
            }

            public readonly object         Gate = new object();
            private readonly ThreadContext _threadContext;
            public readonly Socket         Socket; // TODO: remove
            public readonly int            Fd;
            private readonly Action       _onFlushedToApp;
            private readonly Action       _onReadFromApp;

            public int                     ZeroCopyThreshold;

            private SocketFlags           _flags;
            private Exception             _outputCompleteError;
            private Exception             _inputCompleteError;
            private ValueTaskAwaiter<ReadResult>  _readAwaiter;
            private ValueTaskAwaiter<FlushResult> _flushAwaiter;
            private int                   _zeropCopyState;
            private SequencePosition      _zeroCopyEnd;

            public TSocket(ThreadContext threadContext, Socket socket, int fd, SocketFlags flags)
            {
                Socket = socket;
                _threadContext = threadContext;
                Fd = fd;
                _flags = flags;
                _onFlushedToApp = new Action(OnFlushedToApp);
                _onReadFromApp = new Action(() => OnReadFromApp(loop: false));
            }

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

            private void CancelReadFromSocket(Exception exception)
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
                    _inputCompleteError = exception ?? TransportConstants.EofSentinel;
                    _flags = flags;
                }
                if (completeReadable)
                {
                    OnReceiveFromSocket(_inputCompleteError);
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
                            loop = OnReadFromApp(loop);
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
                    OnReadFromApp(loop: false);
                }
            }

            public void DoDeferedSend()
            {
                OnReadFromApp(loop: false);
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

            private unsafe bool OnReadFromApp(bool loop)
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
                    FillSendIOVector(ref buffer, ioVectors, ioVectorLength);
                    bool zerocopy = buffer.Length >= ZeroCopyThreshold;

                    (PosixResult result, bool zeroCopyRegistered) = TrySend(zerocopy, ioVectors, ioVectorLength);

                    return HandleReadResult(ref buffer, result, loop, zerocopy, zeroCopyRegistered);
                }
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public bool HandleReadResult(ref ReadOnlySequence<byte> buffer, PosixResult result, bool loop, bool zerocopy, bool zeroCopyRegistered)
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
                else if (result == PosixResult.EAGAIN || result == PosixResult.EWOULDBLOCK)
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

            public void CompleteOutput(Exception e)
            {
                _outputCompleteError = e;
                CancelReadFromSocket(e);
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

            private static void CompleteWriteToSocket(Exception ex, object state)
            {
                if (ex != null)
                {
                    var tsocket = (TSocket)state;
                    tsocket.CancelWriteToSocket();
                }
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private void CleanupSocketEnd()
            {
                lock (Gate)
                {
                    _flags = _flags + (int)SocketFlags.CloseEnd;
                    if (!HasFlag(SocketFlags.BothClosed))
                    {
                        return;
                    }
                }

                Output.Complete(_outputCompleteError); // TODO: should this be called earlier?

                // First remove from the Dictionary, so we can't match with a new fd.
                _threadContext.RemoveSocket(Fd);

                // We are not using SafeHandles to increase performance.
                // We get here when both reading and writing has stopped
                // so we are sure this is the last use of the Socket.
                Socket.Dispose();
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public unsafe int FillReceiveIOVector(int availableBytes, IOVector* ioVectors, ref int ioVectorLength)
            {
                PipeWriter writer = Input;
                Memory<byte> memory = writer.GetMemory(MinAllocBufferSize);
                var allocated = 0;

                var advanced = 0;
                int ioVectorsUsed = 0;
                for (; ioVectorsUsed < ioVectorLength; ioVectorsUsed++)
                {
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

                    writer.Advance(length);
                    advanced += length;
                    memory = writer.GetMemory(1);
                }
                ioVectorLength = ioVectorsUsed;
                return advanced;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public int CalcIOVectorLengthForReceive(int availableBytes, int maxLength)
            {
                Memory<byte> memory = Input.GetMemory(MinAllocBufferSize);
                return availableBytes <= memory.Length ? 1 :
                       Math.Min(1 + (availableBytes - memory.Length + MaxPooledBlockLength - 1) / MaxPooledBlockLength, maxLength);
            }

            public unsafe Exception Receive(int availableBytes)
            {
                int ioVectorLength = CalcIOVectorLengthForReceive(availableBytes, MaxIOVectorReceiveLength);
                var ioVectors = stackalloc IOVector[ioVectorLength];
                int advanced = FillReceiveIOVector(availableBytes, ioVectors, ref ioVectorLength);

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
                    var result = SocketInterop.Receive(Fd, ioVectors, ioVectorLength);
                    (bool done, Exception retval) = InterpretReceiveResult(result, ref received, advanced, ioVectors, ioVectorLength);
                    if (done)
                    {
                        return retval;
                    }
                    else if (retval == TransportConstants.EAgainSentinel)
                    {
                        if (availableBytes == 0)
                        {
                            return TransportConstants.EAgainSentinel;
                        }
                        eAgainCount++;
                        if (eAgainCount == TransportConstants.MaxEAgainCount)
                        {
                            return new NotSupportedException("Too many EAGAIN, unable to receive available bytes.");
                        }
                    }
                    else
                    {
                        eAgainCount = 0;
                    }
                } while (true);
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public unsafe (bool done, Exception receiveResult) InterpretReceiveResult(PosixResult result, ref int received, int advanced, IOVector* ioVectors, int ioVectorLength)
            {
                PipeWriter writer = Input;
                if (result.IsSuccess)
                {
                    received += result.Value;
                    if (received >= advanced)
                    {
                        // We made it!
                        writer.Advance(received - advanced);
                        return (true, received == 0 ? TransportConstants.EofSentinel : null);
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
                    return (false, null);
                }
                else if (result == PosixResult.EAGAIN || result == PosixResult.EWOULDBLOCK)
                {
                    return (false, TransportConstants.EAgainSentinel);
                }
                else if (result == PosixResult.ECONNRESET)
                {
                    return (true, new ConnectionResetException(result.ErrorDescription(), result.AsException()));
                }
                else
                {
                    return (true, result.AsException());
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
                    OnReceiveFromSocket(_inputCompleteError);
                }
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void OnReceiveFromSocket(Exception result)
            {
                if (result == null)
                {
                    FlushToApp();
                }
                else if (result == TransportConstants.EAgainSentinel)
                {
                    ReceiveFromSocket();
                }
                else if (result == TransportConstants.EofSentinel)
                {
                    CompleteInput(null);
                }
                else
                {
                    CompleteInput(result);
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
                        error = new ConnectionAbortedException();
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
            public unsafe void FillSendIOVector(ref ReadOnlySequence<byte> buffer, IOVector* ioVectors, int ioVectorLength)
            {
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
                Output.OnWriterCompleted(CompleteWriteToSocket, this);
                ReadFromApp();
                // TODO: implement dataMayBeAvailable
                ReceiveFromSocket();
            }

            public void Stop() => CancelWriteToSocket();

            public override MemoryPool<byte> MemoryPool => _threadContext.MemoryPool;

            public override PipeScheduler InputWriterScheduler => PipeScheduler.Inline;

            public override PipeScheduler OutputReaderScheduler => PipeScheduler.Inline;
        }
    }
}