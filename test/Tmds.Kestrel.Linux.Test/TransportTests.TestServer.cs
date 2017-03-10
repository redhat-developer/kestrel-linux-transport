using System;
using System.IO.Pipelines;
using System.Net;
using System.Threading.Tasks;
using Kestrel;
using Tmds.Kestrel.Linux;

namespace Tests
{
    class TestServer : IConnectionHandler, IDisposable
    {
        public delegate void ConnectionHandler(IPipeReader input, IPipeWriter output);

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

        private PipeFactory _pipeFactory;
        private Transport _transport;
        private IPEndPoint _serverAddress;
        private ConnectionHandler _connectionHandler;

        public TestServer(ConnectionHandler connectionHandler, int threadCount = 1)
        {
            _connectionHandler = connectionHandler;
            _pipeFactory = new PipeFactory();
            _serverAddress = new IPEndPoint(IPAddress.Loopback, 0);
            var transportOptions = new TransportOptions()
            {
                ThreadCount = threadCount
            };
            _transport = new Transport(new IPEndPoint[] { _serverAddress }, this, transportOptions);
            _connectionHandler = connectionHandler;
        }

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

        public IConnectionContext OnConnection(IConnectionInformation connectionInfo, PipeOptions inputOptions, PipeOptions outputOptions)
        {
            var input = _pipeFactory.Create(inputOptions);
            var output = _pipeFactory.Create(outputOptions);

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