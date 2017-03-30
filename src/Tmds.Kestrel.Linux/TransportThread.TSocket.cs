using System;
using System.IO.Pipelines;
using System.Net;
using System.Runtime.CompilerServices;
using System.Threading;
using Microsoft.AspNetCore.Server.Kestrel;
using Microsoft.AspNetCore.Server.Kestrel.Internal.Http;
using Microsoft.AspNetCore.Server.Kestrel.Transport;

namespace Tmds.Kestrel.Linux
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

            Stopping        = 0x08,

            TypeAccept      = 0x10,
            TypeClient      = 0x20,
            TypePipe        = 0x30,
            TypeMask        = 0x30,

            DeferAccept     = 0x40
        }

        // TODO: we don't support this and it looks like this will be removed from the
        //       IConnectionInformation interface
        class UnsupportedTimeoutControl : ITimeoutControl
        {
            public void CancelTimeout()
            {
            }

            public void ResetTimeout(long milliseconds, TimeoutAction timeoutAction)
            {
            }

            public void SetTimeout(long milliseconds, TimeoutAction timeoutAction)
            {
            }
        }

        class TSocket : IWritableAwaiter, IConnectionInformation
        {
            private TransportThread _thread;
            public TSocket(TransportThread thread)
            {
                _thread = thread;
            }
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
            public bool GetReadableResult()
            {
                return (Flags & SocketFlags.Stopping) == 0;
            }
            public void SetReadableContinuation(Action continuation)
            {
                Volatile.Write(ref _readableCompletion, continuation);
            }
            public void CompleteReadable(bool stopping = false)
            {
                if (stopping)
                {
                    AddFlags(SocketFlags.Stopping);
                }

                Action continuation = Interlocked.Exchange(ref _readableCompletion, null);
                continuation?.Invoke();
            }

            public WritableAwaitable WritableAwaitable => new WritableAwaitable(this);

            IPEndPoint IConnectionInformation.RemoteEndPoint => PeerAddress;

            IPEndPoint IConnectionInformation.LocalEndPoint => LocalAddress;

            // TODO: who is using this?
            ListenOptions IConnectionInformation.ListenOptions => _thread._listenOptions;

            PipeFactory IConnectionInformation.PipeFactory => _thread._pipeFactory;

            IScheduler IConnectionInformation.InputWriterScheduler => InlineScheduler.Default;

            IScheduler IConnectionInformation.OutputWriterScheduler => InlineScheduler.Default;

            private static ITimeoutControl _timeoutControl = new UnsupportedTimeoutControl();
            ITimeoutControl IConnectionInformation.TimeoutControl => _timeoutControl;
        }

        interface IWritableAwaiter
        {
            bool IsCompleted { get; }

            bool GetResult();

            void OnCompleted(Action continuation);
        }

        struct ReadableAwaitable: ICriticalNotifyCompletion
        {
            private readonly TSocket _tsocket;
            private readonly EPoll _epoll;

            public ReadableAwaitable(TSocket awaiter, EPoll epoll)
            {
                _tsocket = awaiter;
                _epoll = epoll;
            }

            public bool IsCompleted => false;

            public bool GetResult() => _tsocket.GetReadableResult();

            public ReadableAwaitable GetAwaiter() => this;

            public void UnsafeOnCompleted(Action continuation) => OnCompleted(continuation);

            public void OnCompleted(Action continuation)
            {
                _tsocket.SetReadableContinuation(continuation);
                TransportThread.RegisterForReadable(_tsocket, _epoll);
            }
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