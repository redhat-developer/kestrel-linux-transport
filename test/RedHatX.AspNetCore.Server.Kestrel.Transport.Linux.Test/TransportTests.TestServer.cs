using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Protocols.Features;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Abstractions.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using RedHatX.AspNetCore.Server.Kestrel.Transport.Linux;

namespace Tests
{
    public delegate void TestServerConnectionHandler(PipeReader input, PipeWriter output);

    public class TestServerOptions
    {
        public int ThreadCount { get; set; } = 1;
        public bool DeferAccept { get; set; } = false;
        public bool CheckAvailable { get; set; } = true;
        public TestServerConnectionHandler ConnectionHandler { get; set; } = TestServer.Echo;
        public string UnixSocketPath { get; set; }
        public IPEndPoint IPEndPoint { get; set; }
        public bool AioSend { get; set; } = false;
        public bool AioReceive { get; set; } = false;
    }

    class TestServer : IConnectionHandler, IDisposable
    {
        private Transport _transport;
        private IPEndPoint _serverAddress;
        private string _unixSocketPath;
        private TestServerConnectionHandler _connectionHandler;

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
            _connectionHandler = options.ConnectionHandler;
            var transportOptions = new LinuxTransportOptions()
            {
                ThreadCount = options.ThreadCount,
                DeferAccept = options.DeferAccept,
                CheckAvailable = options.CheckAvailable,
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

        public TestServer(TestServerConnectionHandler connectionHandler) :
            this(new TestServerOptions() { ConnectionHandler = connectionHandler })
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

        public void OnConnection(TransportConnection connection)
        {
            var memoryPool = connection.MemoryPool;
            var input = new Pipe(GetInputPipeOptions(memoryPool, connection.InputWriterScheduler));
            var output = new Pipe(GetOutputPipeOptions(memoryPool, connection.OutputReaderScheduler));

            _connectionHandler(input.Reader, output.Writer);

            connection.Transport = new DuplexPipe(input.Reader, output.Writer);
            connection.Application = new DuplexPipe(output.Reader, input.Writer);
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
            _transport.Dispose(); 
        }

        public static async void Echo(PipeReader input, PipeWriter output)
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
                var client = Socket.Create(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified, blocking: true);
                client.Connect(_unixSocketPath);
                return client;
            }
            else if (_serverAddress != null)
            {
                var client = Socket.Create(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp, blocking: true);
                client.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.NoDelay, 1);
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
