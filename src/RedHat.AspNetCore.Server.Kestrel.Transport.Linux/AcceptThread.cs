using System;
using System.IO;
using System.Threading.Tasks;
using System.Threading;
using static Tmds.Linux.LibC;
using Tmds.Linux;

namespace RedHat.AspNetCore.Server.Kestrel.Transport.Linux
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
        private int[] _handlers;

        public AcceptThread(Socket socket)
        {
            _socket = socket;
            _state = State.Initial;
            _handlers = Array.Empty<int>();
        }

        public int CreateReceiveSocket()
        {
            lock (_gate)
            {
                if (_state != State.Initial)
                {
                    throw new InvalidOperationException($"Invalid operation: {_state}");
                }
                var pair = Socket.CreatePair(AF_UNIX, SOCK_STREAM, 0, blocking: false);
                var updatedHandlers = new int[_handlers.Length + 1];
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
            return Task.CompletedTask;
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
                    catch (IOException ex) when (ex.HResult == EPIPE)
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
                IOInterop.Close(handler);
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
                        epoll.Control(EPOLL_CTL_ADD, _socket, EPOLLIN, acceptKey);
                        // add pipe
                        epoll.Control(EPOLL_CTL_ADD, _pipeEnds.ReadEnd, EPOLLIN, pipeKey);

                        epoll_event ev;
                        bool running = true;
                        int nextHandler = 0;
                        var handlers = _handlers;
                        do
                        {
                            int numEvents = EPollInterop.EPollWait(epollFd, &ev, 1, timeout: EPoll.TimeoutInfinite).IntValue;
                            if (numEvents == 1)
                            {
                                if (ev.data.fd == acceptKey)
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
                _stoppedTcs.TrySetResult(null);
            }
            catch (Exception e)
            {
                _stoppedTcs.SetException(e);
            }
            finally
            {
                Cleanup();
            }
        }
    }
}