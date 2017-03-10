using System;
using System.IO.Pipelines;
using System.Net;
using System.Runtime.CompilerServices;
using System.Threading;
using Kestrel;

namespace Tmds.Kestrel.Linux
{
    partial class TransportThread
    {
        [Flags]
        enum SocketFlags
        {
            AwaitReadable   = EPollEvents.Readable, // 0x01, EPOLLIN
            AwaitWritable   = EPollEvents.Writable, // 0x04, EPOLLOUT

            EPollEventMask  = AwaitReadable | AwaitWritable,

            EPollRegistered = 0x08,

            ShutdownSend    = 0x10,
            ShutdownReceive = 0x20,

            TypeAccept      = 0x00,
            TypeClient      = 0x40,
            TypePipe        = 0x80,

            TypeMask        = 0xC0,

            Stopping         = 0x100
        }

        class TSocket : IReadableAwaiter, IWritableAwaiter, IConnectionInformation
        {
            private static readonly Action _completedSentinel = delegate { };

            public SocketFlags Flags;
            public int         Key;
            public Socket      Socket;
            public IPipeReader PipeReader;
            public IPEndPoint  PeerAddress;
            public IPEndPoint  LocalAddress;

            private Action _writableCompletion;
            bool IWritableAwaiter.IsCompleted
            {
                get
                {
                    return ReferenceEquals(_completedSentinel, Volatile.Read(ref _writableCompletion));
                }
            }
            bool IWritableAwaiter.GetResult()
            {
                return (Flags & SocketFlags.Stopping) != 0;
            }
            void IWritableAwaiter.OnCompleted(Action continuation)
            {
                if (continuation != null)
                {
                    var oldValue = Interlocked.CompareExchange(ref _writableCompletion, continuation, null);

                    if (ReferenceEquals(oldValue, _completedSentinel))
                    {
                        // already complete; calback sync
                        continuation.Invoke();
                    }
                }
            }
            public void CompleteWritable(bool stopping = false)
            {
                if (stopping)
                {
                    lock (this)
                    {
                        Flags |= SocketFlags.Stopping;
                    }
                }

                Action continuation = Interlocked.Exchange(ref _writableCompletion, _completedSentinel);
                if (continuation != null && !ReferenceEquals(continuation, _completedSentinel))
                {
                    continuation.Invoke();
                }
            }
            public void ResetWritableAwaitable()
            {
                Volatile.Write(ref _writableCompletion, null);
            }

            private Action _readableCompletion;
            bool IReadableAwaiter.IsCompleted
            {
                get
                {
                    return ReferenceEquals(_completedSentinel, Volatile.Read(ref _readableCompletion));
                }
            }
            bool IReadableAwaiter.GetResult()
            {
                return (Flags & SocketFlags.Stopping) != 0;
            }
            void IReadableAwaiter.OnCompleted(Action continuation)
            {
                if (continuation != null)
                {
                    var oldValue = Interlocked.CompareExchange(ref _readableCompletion, continuation, null);

                    if (ReferenceEquals(oldValue, _completedSentinel))
                    {
                        // already complete; calback sync
                        continuation.Invoke();
                    }
                }
            }
            public void CompleteReadable(bool stopping = false)
            {
                if (stopping)
                {
                    lock (this)
                    {
                        Flags |= SocketFlags.Stopping;
                    }
                }

                Action continuation = Interlocked.Exchange(ref _readableCompletion, _completedSentinel);
                if (continuation != null && !ReferenceEquals(continuation, _completedSentinel))
                {
                    continuation.Invoke();
                }
            }
            public void ResetReadableAwaitable()
            {
                Volatile.Write(ref _readableCompletion, null);
            }

            public ReadableAwaitable ReadableAwaitable => new ReadableAwaitable(this);
            public WritableAwaitable WritableAwaitable => new WritableAwaitable(this);

            IPEndPoint IConnectionInformation.RemoteEndPoint => PeerAddress;

            IPEndPoint IConnectionInformation.LocalEndPoint => LocalAddress;
        }

        interface IReadableAwaiter
        {
            bool IsCompleted { get; }

            bool GetResult();

            void OnCompleted(Action continuation);
        }

        interface IWritableAwaiter
        {
            bool IsCompleted { get; }

            bool GetResult();

            void OnCompleted(Action continuation);
        }

        struct ReadableAwaitable: ICriticalNotifyCompletion
        {
            private readonly IReadableAwaiter _awaiter;

            public ReadableAwaitable(TSocket awaiter)
            {
                _awaiter = awaiter;
            }

            public bool IsCompleted => _awaiter.IsCompleted;

            public bool GetResult() => _awaiter.GetResult();

            public ReadableAwaitable GetAwaiter() => this;

            public void UnsafeOnCompleted(Action continuation) => _awaiter.OnCompleted(continuation);

            public void OnCompleted(Action continuation) => _awaiter.OnCompleted(continuation);
        }

        struct WritableAwaitable: ICriticalNotifyCompletion
        {
            private readonly IWritableAwaiter _awaiter;

            public WritableAwaitable(IWritableAwaiter awaiter)
            {
                _awaiter = awaiter;
            }

            public bool IsCompleted => _awaiter.IsCompleted;

            public bool GetResult() => _awaiter.GetResult();

            public WritableAwaitable GetAwaiter() => this;

            public void UnsafeOnCompleted(Action continuation) => _awaiter.OnCompleted(continuation);

            public void OnCompleted(Action continuation) => _awaiter.OnCompleted(continuation);
        }
    }
}