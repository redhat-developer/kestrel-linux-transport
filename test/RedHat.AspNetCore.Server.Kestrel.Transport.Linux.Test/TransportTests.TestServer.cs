using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Abstractions.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using RedHat.AspNetCore.Server.Kestrel.Transport.Linux;
using Xunit;
using static Tmds.LibC.Definitions;

namespace Tests
{
    public delegate Task TestServerConnectionDispatcher(PipeReader input, PipeWriter output, TransportConnection connection);

    public class TestServerOptions
    {
        public int ThreadCount { get; set; } = 1;
        public bool DeferAccept { get; set; } = false;
        public TestServerConnectionDispatcher ConnectionDispatcher { get; set; } = TestServer.Echo;
        public string UnixSocketPath { get; set; }
        public IPEndPoint IPEndPoint { get; set; }
        public bool AioSend { get; set; } = false;
        public bool AioReceive { get; set; } = false;
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

    class TestServer : IConnectionDispatcher, IDisposable
    {
        private Transport _transport;
        private IPEndPoint _serverAddress;
        private string _unixSocketPath;
        private TestServerConnectionDispatcher _connectionDispatcher;

        private class EndPointInfo : IEndPointInformation
        {
            public ListenType Type { get; set; }
            public IPEndPoint IPEndPoint { get; set; }
            public string SocketPath { get; set; }
            public ulong FileHandle { get => 0; }
            public FileHandleType HandleType { get => FileHandleType.Auto; set { } }
            public bool NoDelay { get => true; }
        }

        public TestServer(TestServerOptions options = null)
        {
            options = options ?? new TestServerOptions();
            _connectionDispatcher = options.ConnectionDispatcher;
            var transportOptions = new LinuxTransportOptions()
            {
                ThreadCount = options.ThreadCount,
                DeferAccept = options.DeferAccept,
                AioReceive = options.AioReceive,
                AioSend = options.AioSend
            };
            var loggerFactory = new LoggerFactory();
            loggerFactory.AddConsole((n, l) => false);
            IEndPointInformation endPoint = null;
            if (options.UnixSocketPath != null)
            {
                _unixSocketPath = options.UnixSocketPath;
                endPoint = new EndPointInfo
                {
                    Type = ListenType.SocketPath,
                    SocketPath = _unixSocketPath
                };
            }
            else
            {
                _serverAddress = options.IPEndPoint ?? new IPEndPoint(IPAddress.Loopback, 0);
                endPoint = new EndPointInfo
                {
                    Type = ListenType.IPEndPoint,
                    IPEndPoint = _serverAddress
                };
            }
            _transport = new Transport(endPoint, this, transportOptions, loggerFactory);
        }

        public TestServer(TestServerConnectionDispatcher connectionDispatcher) :
            this(new TestServerOptions() { ConnectionDispatcher = connectionDispatcher })
        {}

        public Task BindAsync()
        {
            return _transport.BindAsync();
        }

        public Task UnbindAsync()
        {
            return _transport.UnbindAsync();
        }

        public Task StopAsync()
        {
            return _transport.StopAsync();
        }

        public async Task OnConnection(TransportConnection connection)
        {
            var memoryPool = connection.MemoryPool;
            var input = new Pipe(GetInputPipeOptions(memoryPool, connection.InputWriterScheduler));
            var output = new Pipe(GetOutputPipeOptions(memoryPool, connection.OutputReaderScheduler));

            connection.Transport = new DuplexPipe(input.Reader, output.Writer);
            connection.Application = new DuplexPipe(output.Reader, input.Writer);

            // Handle the connection
            await _connectionDispatcher(input.Reader, output.Writer, connection);

            // Wait for the transport to close
            await CancellationTokenAsTask(connection.ConnectionClosed);
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

        // copied from Kestrel
        private const long _maxRequestBufferSize = 1024 * 1024;
        private const long _maxResponseBufferSize = 64 * 1024;

        private PipeOptions GetInputPipeOptions(MemoryPool<byte> memoryPool, PipeScheduler writerScheduler) => new PipeOptions
        (
            pool: memoryPool,
            readerScheduler: PipeScheduler.Inline,
            writerScheduler: writerScheduler,
            pauseWriterThreshold: _maxRequestBufferSize,
            resumeWriterThreshold: _maxRequestBufferSize
        );

        private PipeOptions GetOutputPipeOptions(MemoryPool<byte> memoryPool, PipeScheduler readerScheduler) => new PipeOptions
        (
            pool: memoryPool,
            readerScheduler: readerScheduler,
            writerScheduler: PipeScheduler.Inline,
            pauseWriterThreshold: _maxResponseBufferSize,
            resumeWriterThreshold: _maxResponseBufferSize
        );

        public void Dispose()
        {
            Task stopTask = _transport.StopAsync();
            // Tests must have called StopAsync already.
            Assert.True(stopTask.IsCompleted);
        }

        public static async Task Echo(PipeReader input, PipeWriter output, TransportConnection connection)
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
