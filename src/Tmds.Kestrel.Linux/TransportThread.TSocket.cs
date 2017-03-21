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
            None            = 0,

            EPollRegistered = 0x01,

            ShutdownSend    = 0x02,
            ShutdownReceive = 0x04,

            Stopping        = 0x08,

            TypeAccept      = 0x10,
            TypeClient      = 0x20,
            TypePipe        = 0x30,
            TypeMask        = 0x30,

            DeferAccept     = 0x40
        }

        class TSocket : IReadableAwaiter, IWritableAwaiter, IConnectionInformation
        {
            private static readonly Action _completedSentinel = delegate { };

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

            public int         Key;
            public Socket      Socket;
            public Socket      DupSocket;
            public IPipeReader PipeReader;
            public IPipeWriter PipeWriter;
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
                return (Flags & SocketFlags.Stopping) == 0;
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
                    AddFlags(SocketFlags.Stopping);
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
                return (Flags & SocketFlags.Stopping) == 0;
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
                    AddFlags(SocketFlags.Stopping);
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