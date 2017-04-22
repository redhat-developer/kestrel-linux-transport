using System;
using System.Net;
using System.Runtime.CompilerServices;
using System.Threading;
using Microsoft.AspNetCore.Server.Kestrel.Internal.System.IO.Pipelines;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Abstractions;

namespace RedHatX.AspNetCore.Server.Kestrel.Transport.Linux
{
    sealed partial class TransportThread
    {
        [Flags]
        enum SocketFlags
        {
            None            = 0,

            EPollRegistered = 0x01,

            ShutdownSend    = 0x02,
            ShutdownReceive = 0x04,

            TypeAccept      = 0x10,
            TypeClient      = 0x20,
            TypeMask        = 0x30,

            DeferAccept     = 0x40
        }

        class TSocket : IConnectionInformation
        {
            public TSocket(ThreadContext threadContext)
            {
                ThreadContext = threadContext;
            }
            private static readonly Action _canceledSentinel = delegate { };

            private int _flags;
            public SocketFlags Flags
            {
                get { return (SocketFlags)_flags; }
                set { _flags = (int)value; }
            }

            public SocketFlags AddFlags(SocketFlags flags)
            {
                int guess;
                int oldValue = _flags;
                do
                {
                    guess = oldValue;
                    oldValue = Interlocked.CompareExchange(ref _flags, guess | (int)flags, guess);
                } while (oldValue != guess);
                return (SocketFlags)oldValue;
            }

            public ThreadContext ThreadContext;
            public int         Key;
            public Socket      Socket;
            public Socket      DupSocket;
            public IPipeReader PipeReader;
            public IPipeWriter PipeWriter;
            public IPEndPoint  PeerAddress;
            public IPEndPoint  LocalAddress;
            public IConnectionContext ConnectionContext;

            private Action _writableCompletion;
            public bool SetWritableContinuation(Action continuation)
            {
                var oldValue = Interlocked.CompareExchange(ref _writableCompletion, continuation, null);
                return oldValue == null;
            }

            public bool IsWriteCancelled() => ReferenceEquals(_writableCompletion, _canceledSentinel);

            public void CancelWritable()
            {
                Action continuation = Interlocked.Exchange(ref _writableCompletion, _canceledSentinel);
                continuation?.Invoke();
            }

            public void CompleteWritable()
            {
                Action continuation = Volatile.Read(ref _writableCompletion);
                if (!ReferenceEquals(continuation, _canceledSentinel))
                {
                    Volatile.Write(ref _writableCompletion, null);
                    continuation.Invoke();
                }
            }

            private Action _readableCompletion;
            public bool SetReadableContinuation(Action continuation)
            {
                var oldValue = Interlocked.CompareExchange(ref _readableCompletion, continuation, null);
                return oldValue == null;
            }

            public bool IsReadCancelled() => ReferenceEquals(_readableCompletion, _canceledSentinel);

            public void CancelReadable()
            {
                Action continuation = Interlocked.Exchange(ref _readableCompletion, _canceledSentinel);
                continuation?.Invoke();
            }

            public void CompleteReadable()
            {
                Action continuation = Volatile.Read(ref _readableCompletion);
                if (!ReferenceEquals(continuation, _canceledSentinel))
                {
                    Volatile.Write(ref _readableCompletion, null);
                    continuation.Invoke();
                }
            }

            IPEndPoint IConnectionInformation.RemoteEndPoint => PeerAddress;

            IPEndPoint IConnectionInformation.LocalEndPoint => LocalAddress;

            PipeFactory IConnectionInformation.PipeFactory => ThreadContext.PipeFactory;

            IScheduler IConnectionInformation.InputWriterScheduler => InlineScheduler.Default;

            IScheduler IConnectionInformation.OutputReaderScheduler => ThreadContext.SendScheduler;
        }

        struct ReadableAwaitable: ICriticalNotifyCompletion
        {
            private readonly TSocket _tsocket;

            public ReadableAwaitable(TSocket awaiter)
            {
                _tsocket = awaiter;
            }

            public bool IsCompleted => false;

            public bool GetResult() => !_tsocket.IsReadCancelled();

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

            public bool GetResult() => !_tsocket.IsWriteCancelled();

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