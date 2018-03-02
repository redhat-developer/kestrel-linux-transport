using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Runtime.CompilerServices;
using System.Threading;
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
            WriteStopped    = 0x1000,
            ReadStopped     = 0x2000
        }

        class TSocket : TransportConnection
        {
            public const EPollEvents EventControlRegistered = (EPollEvents)SocketFlags.EventControlRegistered;
            public const EPollEvents EventControlPending = (EPollEvents)SocketFlags.EventControlPending;

            public TSocket(ThreadContext threadContext, SocketFlags flags)
            {
                ThreadContext = threadContext;
                _flags = (int)flags;
            }
            private static readonly Action _completedSentinel = delegate { };

            private int _flags;
            public SocketFlags Flags
            {
                get { return (SocketFlags)_flags; }
            }

            public SocketFlags Type => ((SocketFlags)_flags & SocketFlags.TypeMask);

            public int ZeroCopyThreshold;

            public readonly object Gate = new object();

            // must be called under Gate
            public EPollEvents PendingEventState
            {
                get => (EPollEvents)_flags;
                set => _flags = (int)value;
            }

            // must be called under Gate
            public bool CloseEnd()
            {
                _flags = _flags + (int)SocketFlags.CloseEnd;
                return (_flags & (int)SocketFlags.BothClosed) != 0;
            }

            public ThreadContext ThreadContext;
            public int         Fd;
            public Socket      Socket;
            public Exception   OutputError;

            private Action _writableCompletion;

            public bool RegisterForWritable(Action continuation)
            {
                // called under tsocket.Gate
                if ((Flags & SocketFlags.WriteStopped) != SocketFlags.None)
                {
                    return false;
                }
                _writableCompletion = continuation;
                TransportThread.RegisterForWritable(this);
                return true;
            }

            public bool IsWritable() => (Flags & SocketFlags.WriteStopped) == SocketFlags.None;

            public void CompleteWritable()
            {
                Action continuation = Interlocked.Exchange(ref _writableCompletion, null);
                continuation.Invoke();
            }

            public void StopWriteToSocket()
            {
                bool completeWritable = false;
                lock (Gate)
                {
                    var flags = Flags;
                    if ((flags & SocketFlags.WriteStopped) != SocketFlags.None)
                    {
                        return;
                    }
                    if ((Flags & SocketFlags.AwaitWritable) != SocketFlags.None)
                    {
                        completeWritable = true;
                    }
                    if ((Flags & SocketFlags.AwaitZeroCopy) != SocketFlags.None)
                    {
                        // Terminate pending zero copy
                        // Call it under Gate so it doesn't race with Close
                        SocketInterop.Disconnect(Fd);
                    }
                    flags &= ~SocketFlags.AwaitWritable;
                    flags |= SocketFlags.WriteStopped;
                    _flags = (int)flags;
                }
                if (completeWritable)
                {
                    _writableCompletion();
                }
            }

            public void StopReadFromSocket(Exception exception)
            {
                bool completeReadable = false;
                lock (Gate)
                {
                    var flags = Flags;
                    if ((flags & SocketFlags.ReadStopped) != SocketFlags.None)
                    {
                        return;
                    }
                    if ((Flags & SocketFlags.AwaitReadable) != SocketFlags.None)
                    {
                        completeReadable = true;
                    }
                    flags &= ~SocketFlags.AwaitReadable;
                    flags |= SocketFlags.ReadStopped;
                    _readResult = exception ?? TransportThread.EofSentinel;
                    _flags = (int)flags;
                }
                if (completeReadable)
                {
                    _receiveCompletion();
                }
            }

            private Action _receiveCompletion;
            private Exception _readResult;

            public bool RegisterForReceive(Action continuation)
            {
                // called under tsocket.Gate
                if ((Flags & SocketFlags.ReadStopped) != SocketFlags.None)
                {
                    return false;
                }
                _receiveCompletion = continuation;
                TransportThread.RegisterForReadable(this);
                return true;
            }

            public Exception ReceiveResult() => _readResult;

            public void CompleteReceive(Exception result)
            {
                _readResult = result;
                _receiveCompletion();
            }

            private Action _zeroCopyWrittenCompletion;

            public bool RegisterForZeroCopyWritten(bool registered, Action continuation)
            {
                // called under tsocket.Gate
                if (registered)
                {
                    var oldValue = Interlocked.CompareExchange(ref _zeroCopyWrittenCompletion, continuation, null);
                    if (ReferenceEquals(oldValue, _completedSentinel))
                    {
                        // Already completed, no need to register
                        Volatile.Write(ref _zeroCopyWrittenCompletion, null);
                        return false;
                    }
                    else
                    {
                        // Already registered
                        return true;
                    }
                }
                else
                {
                    // Register now
                    _zeroCopyWrittenCompletion = continuation;
                    TransportThread.RegisterForZeroCopyWritten(this);
                    return true;
                }
            }

            public void CompleteZeroCopy()
            {
                Action continuation = Interlocked.CompareExchange(ref _zeroCopyWrittenCompletion, _completedSentinel, null);
                if (!ReferenceEquals(continuation, null))
                {
                    Volatile.Write(ref _zeroCopyWrittenCompletion, null);
                    continuation();
                }
            }

            public bool IsZeroCopyFinished() => (Flags & SocketFlags.WriteStopped) == SocketFlags.None;

            public override MemoryPool MemoryPool => ThreadContext.MemoryPool;

            public override PipeScheduler InputWriterScheduler => PipeScheduler.Inline;

            public override PipeScheduler OutputReaderScheduler => ThreadContext.SendScheduler;
        }

        struct ReceiveAwaitable: ICriticalNotifyCompletion
        {
            private readonly TSocket _tsocket;

            public ReceiveAwaitable(TSocket awaiter)
            {
                _tsocket = awaiter;
            }

            public bool IsCompleted => false;

            public Exception GetResult() => _tsocket.ReceiveResult();

            public ReceiveAwaitable GetAwaiter() => this;

            public void UnsafeOnCompleted(Action continuation) => OnCompleted(continuation);

            public void OnCompleted(Action continuation)
                => TransportThread.OnReceiveCompleted(_tsocket, continuation);
        }

        struct WritableAwaitable: ICriticalNotifyCompletion
        {
            private readonly TSocket _tsocket;

            public WritableAwaitable(TSocket awaiter)
            {
                _tsocket = awaiter;
            }

            public bool IsCompleted => false;

            public bool GetResult() => _tsocket.IsWritable();

            public WritableAwaitable GetAwaiter() => this;

            public void UnsafeOnCompleted(Action continuation) => OnCompleted(continuation);

            public void OnCompleted(Action continuation)
                => TransportThread.OnWritableCompleted(_tsocket, continuation);
        }

        struct ZeroCopyWrittenAwaitable: ICriticalNotifyCompletion
        {
            private readonly TSocket _tsocket;
            private readonly bool _registered;

            public ZeroCopyWrittenAwaitable(TSocket awaiter, bool registered)
            {
                _tsocket = awaiter;
                _registered = registered;
            }

            public bool IsCompleted => false;

            public bool GetResult() => _tsocket.IsZeroCopyFinished();

            public ZeroCopyWrittenAwaitable GetAwaiter() => this;

            public void UnsafeOnCompleted(Action continuation)
                => OnCompleted(continuation);

            public void OnCompleted(Action continuation)
                => TransportThread.OnZeroCopyWrittenCompleted(_tsocket, _registered, continuation);
        }
    }
}