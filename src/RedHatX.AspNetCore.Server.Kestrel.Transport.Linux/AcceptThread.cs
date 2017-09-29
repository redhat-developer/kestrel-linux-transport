using System;
using System.IO;
using System.Threading.Tasks;
using System.Threading;

namespace RedHatX.AspNetCore.Server.Kestrel.Transport.Linux
{
    sealed class AcceptThread : ITransportActionHandler
    {
        private enum State
        {
            Initial,
            Started,
            Stopped
        }

        private Socket _socket;
        private State _state;
        private readonly object _gate = new object();
        private TaskCompletionSource<object> _stoppedTcs;
        private Thread _thread;
        private PipeEndPair _pipeEnds;
        private Socket[] _handlers;

        public AcceptThread(Socket socket)
        {
            _socket = socket;
            _state = State.Initial;
            _handlers = Array.Empty<Socket>();
        }

        public Socket CreateReceiveSocket()
        {
            lock (_gate)
            {
                if (_state != State.Initial)
                {
                    throw new InvalidOperationException($"Invalid operation: {_state}");
                }
                var pair = Socket.CreatePair(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified, blocking: false);
                var updatedHandlers = new Socket[_handlers.Length + 1];
                Array.Copy(_handlers, updatedHandlers, _handlers.Length);
                updatedHandlers[updatedHandlers.Length - 1] = pair.Socket1;
                _handlers = updatedHandlers;
                return pair.Socket2;
            }
        }

        public Task BindAsync()
        {
            lock (_gate)
            {
                if (_state != State.Initial)
                {
                    throw new InvalidOperationException($"Invalid operation: {_state}");
                }

                _stoppedTcs = new TaskCompletionSource<object>();
                try
                {
                    _pipeEnds = PipeEnd.CreatePair(blocking: false);
                    _thread = new Thread(AcceptThreadStart);;
                    _thread.Start();
                    _state = State.Started;
                    return _stoppedTcs.Task;
                }
                catch (System.Exception)
                {
                    _state = State.Stopped;
                    _stoppedTcs = null;
                    _socket.Dispose();
                    Cleanup();
                    throw;
                }
            }
        }

        public Task UnbindAsync()
        {
            lock (_gate)
            {
                if (_state != State.Stopped)
                {
                    _state = State.Stopped;

                    try
                    {
                        _pipeEnds.WriteEnd?.WriteByte(0);
                    }
                    catch (IOException ex) when (ex.HResult == PosixResult.EPIPE)
                    {}
                    catch (ObjectDisposedException)
                    {}
                }
                return (Task)_stoppedTcs?.Task ?? Task.CompletedTask;
            }
        }

        public Task StopAsync()
            => UnbindAsync();

        private void Cleanup()
        {
            _pipeEnds.Dispose();
            foreach (var handler in _handlers)
            {
                handler.Dispose();
            }
        }

        private unsafe void AcceptThreadStart(object state)
        {
            try
            {
                var socket = _socket;
                using (socket)
                {
                    using (EPoll epoll = EPoll.Create())
                    {
                        int epollFd = epoll.DangerousGetHandle().ToInt32();
                        const int acceptKey = 0;
                        const int pipeKey = 1;
                        // accept socket
                        epoll.Control(EPollOperation.Add, _socket, EPollEvents.Readable, new EPollData { Int1 = acceptKey, Int2 = acceptKey});
                        // add pipe
                        epoll.Control(EPollOperation.Add, _pipeEnds.ReadEnd, EPollEvents.Readable, new EPollData { Int1 = pipeKey, Int2 = pipeKey});

                        const int EventBufferLength = 1;
                        int notPacked = !EPoll.PackedEvents ? 1 : 0;
                        var buffer = stackalloc int[EventBufferLength * (3 + notPacked)];
                        int* key = &buffer[2];

                        bool running = true;
                        int nextHandler = 0;
                        var handlers = _handlers;
                        do
                        {
                            int numEvents = EPollInterop.EPollWait(epollFd, buffer, EventBufferLength, timeout: EPoll.TimeoutInfinite).Value;
                            if (numEvents == 1)
                            {
                                if (*key == acceptKey)
                                {
                                    var handler = handlers[nextHandler];
                                    nextHandler = (nextHandler + 1) % handlers.Length;
                                    socket.TryAcceptAndSendHandleTo(handler);
                                }
                                else
                                {
                                    running = false;
                                }
                            }
                        } while (running);
                    }
                }
            }
            catch (Exception e)
            {
                _stoppedTcs.SetException(e);
            }
            finally
            {
                Cleanup();
                _stoppedTcs.SetResult(null);
            }
        }
    }
}