using System;
using System.IO.Pipelines;
using System.Net;
using System.Threading.Tasks;
using Kestrel;
using Tmds.Kestrel.Linux;

namespace Tests
{
    public delegate void TestServerConnectionHandler(IPipeReader input, IPipeWriter output);

    class TestServerOptions
    {
        public int ThreadCount { get; set; } = 1;
        public bool DeferAccept { get; set; } = false;
        public TestServerConnectionHandler ConnectionHandler { get; set; } = TestServer.Echo;
    }

    class TestServer : IConnectionHandler, IDisposable
    {

        class ConnectionContext : IConnectionContext
        {
            public ConnectionContext(string connectionId, IPipeWriter input, IPipeReader output)
            {
                ConnectionId = connectionId;
                Input = input;
                Output = output;
            }
            public string ConnectionId { get; }
            public IPipeWriter Input { get; }
            public IPipeReader Output { get; }
        }

        private Transport _transport;
        private IPEndPoint _serverAddress;
        private TestServerConnectionHandler _connectionHandler;

        public TestServer(TestServerOptions options = null)
        {
            options = options ?? new TestServerOptions();
            _connectionHandler = options.ConnectionHandler;
            _serverAddress = new IPEndPoint(IPAddress.Loopback, 0);
            var transportOptions = new TransportOptions()
            {
                ThreadCount = options.ThreadCount,
                DeferAccept = options.DeferAccept
            };
            _transport = new Transport(new IPEndPoint[] { _serverAddress }, this, transportOptions);
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

        public IConnectionContext OnConnection(PipeFactory factory, IConnectionInformation connectionInfo, PipeOptions inputOptions, PipeOptions outputOptions)
        {
            var input = factory.Create(inputOptions);
            var output = factory.Create(outputOptions);

            _connectionHandler(input.Reader, output.Writer);

            return new ConnectionContext(string.Empty, input.Writer, output.Reader);
        }

        public void Dispose()
        {
            _transport.Dispose(); 
        }

        public static async void Echo(IPipeReader input, IPipeWriter output)
        {
            while (true)
            {
                var result = await input.ReadAsync();
                var request = result.Buffer;

                if (request.IsEmpty && result.IsCompleted)
                {
                    input.Advance(request.End);
                    break;
                }

                int len = request.Length;
                var response = output.Alloc();
                response.Append(request);
                await response.FlushAsync();
                input.Advance(request.End);
            }
            input.Complete();
            output.Complete();
        }

        public Socket ConnectTo()
        {
            var client = Socket.Create(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp, blocking: true);
            client.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.NoDelay, 1);
            client.Connect(_serverAddress);
            return client;
        }
    }
}