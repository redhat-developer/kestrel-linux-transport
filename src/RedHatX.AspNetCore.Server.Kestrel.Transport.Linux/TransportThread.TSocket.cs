using System;
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

            EPollRegistered = 0x01,

            CloseEnd        = 0x02,
            BothClosed      = 0x04,

            TypeAccept      = 0x10,
            TypeClient      = 0x20,
            TypeFdPass      = 0x30,
            TypeMask        = 0x30,

            DeferAccept     = 0x40
        }

        class TSocket : TransportConnection
        {
            public TSocket(ThreadContext threadContext)
            {
                ThreadContext = threadContext;
            }
            private static readonly Action _stopSentinel = delegate { };

            private int _flags;
            public SocketFlags Flags
            {
                get { return (SocketFlags)_flags; }
                set { _flags = (int)value; }
            }

            public bool SetRegistered()
            {
                if ((_flags & (int)SocketFlags.EPollRegistered) != 0)
                {
                    return false;
                }
                else
                {
                    Interlocked.Add(ref _flags, (int)SocketFlags.EPollRegistered);
                    return true;
                }
            }

            public bool CloseEnd()
            {
                int value = Interlocked.Add(ref _flags, (int)SocketFlags.CloseEnd);
                return (value & (int)SocketFlags.BothClosed) != 0;
            }

            public ThreadContext ThreadContext;
            public int         Fd;
            public Socket      Socket;
            public Socket      DupSocket;

            private Action _writableCompletion;
            public bool SetWritableContinuation(Action continuation)
            {
                var oldValue = Interlocked.CompareExchange(ref _writableCompletion, continuation, null);
                return oldValue == null;
            }

            public bool IsWritable() => !ReferenceEquals(_writableCompletion, _stopSentinel);

            public void CompleteWritable()
            {
                Action continuation = Interlocked.Exchange(ref _writableCompletion, null);
                continuation.Invoke();
            }

            public void StopWriteToSocket()
            {
                Output.CancelPendingRead();
                // unblock Writable (may race with CompleteWritable)
                Action continuation = Interlocked.Exchange(ref _writableCompletion, _stopSentinel);
                continuation?.Invoke();
            }

            private Action _readableCompletion;
            public bool SetReadableContinuation(Action continuation)
            {
                var oldValue = Interlocked.CompareExchange(ref _readableCompletion, continuation, null);
                return oldValue == null;
            }

            public bool IsReadable() => !ReferenceEquals(_readableCompletion, _stopSentinel);

            public void CompleteReadable()
            {
                Action continuation = Interlocked.Exchange(ref _readableCompletion, null);
                continuation.Invoke();
            }

            public void StopReadFromSocket()
            {
                Input.CancelPendingFlush();
                // unblock Readable (may race with CompleteReadable)
                Action continuation = Interlocked.Exchange(ref _readableCompletion, _stopSentinel);
                continuation?.Invoke();
            }

            public override PipeFactory PipeFactory => ThreadContext.PipeFactory;

            public override IScheduler InputWriterScheduler => InlineScheduler.Default;

            public override IScheduler OutputReaderScheduler => ThreadContext.SendScheduler;
        }

        struct ReadableAwaitable: ICriticalNotifyCompletion
        {
            private readonly TSocket _tsocket;

            public ReadableAwaitable(TSocket awaiter)
            {
                _tsocket = awaiter;
            }

            public bool IsCompleted => false;

            public bool GetResult() => _tsocket.IsReadable();

            public ReadableAwaitable GetAwaiter() => this;

            public void UnsafeOnCompleted(Action continuation) => OnCompleted(continuation);

            public void OnCompleted(Action continuation)
            {
                if (_tsocket.SetReadableContinuation(continuation))
                {
                    TransportThread.RegisterForReadable(_tsocket);
                }
                else
                {
                    continuation();
                }
            }
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
            {
                if (_tsocket.SetWritableContinuation(continuation))
                {
                    TransportThread.RegisterForWritable(_tsocket);
                }
                else
                {
                    continuation();
                }
            }
        }
    }
}