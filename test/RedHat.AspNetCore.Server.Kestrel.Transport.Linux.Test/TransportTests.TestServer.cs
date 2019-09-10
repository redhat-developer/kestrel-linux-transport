using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.Extensions.Logging;
using RedHat.AspNetCore.Server.Kestrel.Transport.Linux;
using Xunit;
using static Tmds.Linux.LibC;
using Socket = RedHat.AspNetCore.Server.Kestrel.Transport.Linux.Socket;

namespace Tests
{
    public delegate Task TestServerConnectionDispatcher(PipeReader input, PipeWriter output, ConnectionContext connection);

    public class TestServerOptions
    {
        public int ThreadCount { get; set; } = 1;
        public bool DeferAccept { get; set; } = false;
        public TestServerConnectionDispatcher ConnectionDispatcher { get; set; } = TestServer.Echo;
        public string UnixSocketPath { get; set; }
        public IPEndPoint IPEndPoint { get; set; }
        public bool AioSend { get; set; } = false;
        public bool AioReceive { get; set; } = false;
        public PipeScheduler ApplicationSchedulingMode { get; set; } = PipeScheduler.ThreadPool;
    }

    internal class DuplexPipe : IDuplexPipe
    {
        public DuplexPipe(PipeReader reader, PipeWriter writer)
        {
            Input = reader;
            Output = writer;
        }

        public PipeReader Input { get; }

        public PipeWriter Output { get; }
    }

    class TestServer : IDisposable
    {
        private Transport _transport;
        private IPEndPoint _serverAddress;
        private string _unixSocketPath;
        private TestServerConnectionDispatcher _connectionDispatcher;
        private Task _acceptLoopTask;

        public TestServer(TestServerOptions options = null)
        {
            options = options ?? new TestServerOptions();
            _connectionDispatcher = options.ConnectionDispatcher;
            var transportOptions = new LinuxTransportOptions()
            {
                ThreadCount = options.ThreadCount,
                DeferAccept = options.DeferAccept,
                AioReceive = options.AioReceive,
                AioSend = options.AioSend,
                ApplicationSchedulingMode = options.ApplicationSchedulingMode,
            };
            var loggerFactory = new LoggerFactory();
            EndPoint endPoint = null;
            if (options.UnixSocketPath != null)
            {
                _unixSocketPath = options.UnixSocketPath;
                endPoint = new UnixDomainSocketEndPoint(_unixSocketPath);
            }
            else
            {
                _serverAddress = options.IPEndPoint ?? new IPEndPoint(IPAddress.Loopback, 0);
                endPoint = _serverAddress;
            }
            _transport = new Transport(endPoint, transportOptions, loggerFactory);
        }

        public TestServer(TestServerConnectionDispatcher connectionDispatcher) :
            this(new TestServerOptions() { ConnectionDispatcher = connectionDispatcher })
        {}

        public async Task BindAsync()
        {
            await _transport.BindAsync();

            // Make sure continuations don't need to post to xunit's MaxConcurrencySyncContext.
            _acceptLoopTask = Task.Run(AcceptLoopAsync);
        }

        public async Task UnbindAsync()
        {
            await _transport.UnbindAsync();
            await _acceptLoopTask;
        }

        public ValueTask StopAsync()
        {
            return _transport.DisposeAsync();
        }

        private async Task AcceptLoopAsync()
        {
            while (true)
            {
                var connection = await _transport.AcceptAsync();

                if (connection == null)
                {
                    break;
                }

                _ = OnConnection(connection);
            }
        }

        private async Task OnConnection(ConnectionContext connection)
        {
            // Handle the connection
            await _connectionDispatcher(connection.Transport.Input, connection.Transport.Output, connection);

            // Wait for the transport to close
            await CancellationTokenAsTask(connection.ConnectionClosed);

            await connection.DisposeAsync();
        }

        private static Task CancellationTokenAsTask(CancellationToken token)
        {
            if (token.IsCancellationRequested)
            {
                return Task.CompletedTask;
            }

            // Transports already dispatch prior to tripping ConnectionClosed
            // since application code can register to this token.
            var tcs = new TaskCompletionSource<object>();
            token.Register(state => ((TaskCompletionSource<object>)state).SetResult(null), tcs);
            return tcs.Task;
        }

        public void Dispose()
        {
            ValueTask stopTask = _transport.DisposeAsync();
            // Tests must have called StopAsync already.
            Assert.True(stopTask.IsCompleted);
        }

        public static async Task Echo(PipeReader input, PipeWriter output, ConnectionContext connection)
        {
            try
            {
                while (true)
                {
                    var result = await input.ReadAsync();
                    var request = result.Buffer;

                    if (request.IsEmpty && result.IsCompleted)
                    {
                        input.AdvanceTo(request.End);
                        break;
                    }

                    foreach (var memory in request)
                    {
                        output.Write(memory.Span);
                    }
                    await output.FlushAsync();
                    input.AdvanceTo(request.End);
                }
            }
            catch
            { }
            finally
            {
                input.Complete();
                output.Complete();
            }
        }

        public Socket ConnectTo()
        {
            if (_unixSocketPath != null)
            {
                var client = Socket.Create(AF_UNIX, SOCK_STREAM, 0, blocking: true);
                client.Connect(_unixSocketPath);
                return client;
            }
            else if (_serverAddress != null)
            {
                var client = Socket.Create(AF_INET, SOCK_STREAM, IPPROTO_TCP, blocking: true);
                client.SetSocketOption(SOL_TCP, TCP_NODELAY, 1);
                client.Connect(_serverAddress);
                return client;
            }
            else
            {
                return null;
            }
        }
    }
}
