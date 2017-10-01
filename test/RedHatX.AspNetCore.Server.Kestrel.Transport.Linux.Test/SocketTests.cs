using System;
using System.Net;
using System.Threading.Tasks;
using RedHatX.AspNetCore.Server.Kestrel.Transport.Linux;
using Xunit;

namespace Tests
{
    public class SocketTests
    {
        private static ArraySegment<byte> s_data = new ArraySegment<byte>(new byte[] { 1, 2, 3 } );

        [Fact]
        public void Tcp() 
        {
            PosixResult result;

            // Create server socket
            var serverSocket = Socket.Create(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp, blocking: true);
            
            // Bind
            var serverAddress = new IPEndPointStruct(IPAddress.Loopback, 0);
            result = serverSocket.TryBind(serverAddress);
            Assert.True(result.IsSuccess);
            result = serverSocket.TryGetLocalIPAddress(out serverAddress);
            Assert.True(result.IsSuccess);

            // Listen
            result = serverSocket.TryListen(10);
            Assert.True(result.IsSuccess);

            // Create client socket
            var clientSocket = Socket.Create(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp, blocking: true);

            // Connect
            result = clientSocket.TryConnect(serverAddress);
            Assert.True(result.IsSuccess);

            // Accept client socket
            Socket acceptedSocket;
            result = serverSocket.TryAccept(out acceptedSocket, blocking: true);

            // Send
            result = acceptedSocket.TrySend(s_data);

            // Receive
            byte[] receiveBuffer = new byte[10];
            result = clientSocket.TryReceive(new ArraySegment<byte>(receiveBuffer));
            Assert.True(result.IsSuccess);
            Assert.Equal(s_data.Count, result.Value);

            // Close
            acceptedSocket.Dispose();
            serverSocket.Dispose();
            clientSocket.Dispose();
        }

        [Fact]
        public void BytesAvailable() 
        {
            Socket socket1, socket2;
            CreateConnectedSockets(out socket1, out socket2, blocking: true);

            // Send 5 bytes
            var buffer = new byte[] { 1, 2, 3, 4, 5 };
            socket1.Send(new ArraySegment<byte>(buffer));

            // Only read 1 byte
            socket2.Receive(new ArraySegment<byte>(buffer, 0, 1));

            // 4 bytes available
            var result = socket2.TryGetAvailableBytes();
            Assert.True(result.IsSuccess);
            Assert.Equal(4, result.Value);

            socket1.Dispose();
            socket2.Dispose();
        }

        [Fact]
        public void Blocking()
        {
            Socket socket1, socket2;
            CreateConnectedSockets(out socket1, out socket2, blocking: true);

            // Blocking receive
            byte[] receiveBuffer = new byte[10];
            var tcs = new TaskCompletionSource<PosixResult>();
            var receiveTask = Task.Run(() => tcs.SetResult(socket1.TryReceive(new ArraySegment<byte>(receiveBuffer))));

            // Unblock by sending
            var buffer = new byte[] { 1, 2, 3, 4, 5 };
            socket2.Send(new ArraySegment<byte>(buffer));

            Task.WaitAny(new [] { receiveTask, Task.Delay(1000) });

            Assert.True(receiveTask.IsCompleted);
            Assert.True(tcs.Task.IsCompleted);
            var result = tcs.Task.Result;
            Assert.True(result.IsSuccess);

            socket1.Dispose();
            socket2.Dispose();
        }

        [Fact]
        public void NonBlocking()
        {
            Socket socket1, socket2;
            CreateConnectedSockets(out socket1, out socket2, blocking: false);

            byte[] receiveBuffer = new byte[10];
            var result = socket1.TryReceive(new ArraySegment<byte>(receiveBuffer));
            Assert.True(result == PosixResult.EAGAIN);

            socket1.Dispose();
            socket2.Dispose();
        }

        [Fact]
        public void Address_IPv4()
        {
            Socket socket1, socket2;
            CreateConnectedSockets(out socket1, out socket2, blocking: false, ipv4: true);

            var socket1Local = socket1.GetLocalIPAddress();
            var socket1Peer = socket1.GetPeerIPAddress();
            var socket2Local = socket2.GetLocalIPAddress();
            var socket2Peer = socket2.GetPeerIPAddress();

            Assert.Equal(socket1Local, socket2Peer);
            Assert.Equal(socket2Local, socket1Peer);

            socket1.Dispose();
            socket2.Dispose();
        }

        // Travis CI doesn't have IPv6
        /*[Fact]
        public void Address_IPv6()
        {
            Socket socket1, socket2;
            CreateConnectedSockets(out socket1, out socket2, blocking: false, ipv4: false);

            var socket1Local = socket1.GetLocalIPAddress();
            var socket1Peer = socket1.GetPeerIPAddress();
            var socket2Local = socket2.GetLocalIPAddress();
            var socket2Peer = socket2.GetPeerIPAddress();

            Assert.Equal(socket1Local, socket2Peer);
            Assert.Equal(socket2Local, socket1Peer);

            socket1.Dispose();
            socket2.Dispose();
        }*/

        [Fact]
        public void Shutdown()
        {
            Socket socket1, socket2;
            CreateConnectedSockets(out socket1, out socket2, blocking: false);

            var result = socket1.TryShutdown(SocketShutdown.Receive);
            Assert.True(result.IsSuccess);
            result = socket1.TryShutdown(SocketShutdown.Send);
            Assert.True(result.IsSuccess);
            result = socket1.TryShutdown(SocketShutdown.Both);
            Assert.True(result.IsSuccess);

            socket1.Dispose();
            socket2.Dispose();
        }

        [Fact]
        public void SocketOptionInt()
        {
            var socket = Socket.Create(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp, blocking: true);
            
            // Set value to 1
            int value = 1;
            var result = socket.TrySetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.NoDelay, value);
            Assert.True(result.IsSuccess);

            // Check value is 1
            int readValue = 0;
            result = socket.TryGetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.NoDelay, ref readValue);
            Assert.True(result.IsSuccess);
            Assert.Equal(value, readValue);

            // Set value to 0
            value = 0;
            result = socket.TrySetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.NoDelay, value);
            Assert.True(result.IsSuccess);

            // Check value is 0
            readValue = 1;
            result = socket.TryGetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.NoDelay, ref readValue);
            Assert.True(result.IsSuccess);
            Assert.Equal(value, readValue);

            socket.Dispose();
        }

        [Fact]
        public void Duplicate()
        {
            var socket = Socket.Create(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp, blocking: true);
            var dup = socket.Duplicate();
            Assert.True(!dup.IsInvalid);
            dup.Dispose();
            socket.Dispose();
        }

        internal static void CreateConnectedSockets(out Socket socket1, out Socket socket2, bool blocking, bool ipv4 = true)
        {
            var serverSocket = Socket.Create(ipv4 ? AddressFamily.InterNetwork : AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp, blocking: true);
            var serverAddress = new IPEndPointStruct(ipv4 ? IPAddress.Loopback : IPAddress.IPv6Loopback , 0);
            serverSocket.Bind(serverAddress);
            serverAddress = serverSocket.GetLocalIPAddress();
            serverSocket.Listen(10);

            var clientSocket = Socket.Create(ipv4 ? AddressFamily.InterNetwork : AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp, blocking);
            clientSocket.TryConnect(serverAddress);

            Socket acceptedSocket;
            acceptedSocket = serverSocket.Accept(blocking);

            serverSocket.Dispose();
            socket1 = clientSocket;
            socket2 = acceptedSocket;
        }

        public void ReuseLocalAddress()
        {
            PosixResult result;

            // Create server socket
            var serverSocket = Socket.Create(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp, blocking: true);

            // Bind
            var serverAddress = new IPEndPointStruct(IPAddress.Loopback, 0);
            result = serverSocket.TryBind(serverAddress);
            Assert.True(result.IsSuccess);

            // Get address
            result = serverSocket.TryGetLocalIPAddress(out serverAddress);
            Assert.True(result.IsSuccess);

            // Get address (no reuse)
            IPEndPointStruct serverAddress2;
            var reuseAddress = IPAddress.None;
            result = serverSocket.TryGetLocalIPAddress(out serverAddress2, reuseAddress);
            Assert.True(result.IsSuccess);
            Assert.Equal(serverAddress.Address, serverAddress2.Address);
            Assert.NotEqual(serverAddress.Address, reuseAddress);
            Assert.False(object.ReferenceEquals(serverAddress.Address, serverAddress2.Address));

            // Get address (reuse)
            IPEndPointStruct serverAddress3;
            result = serverSocket.TryGetLocalIPAddress(out serverAddress3, reuseAddress: serverAddress2.Address);
            Assert.True(result.IsSuccess);
            Assert.True(object.ReferenceEquals(serverAddress2.Address, serverAddress3.Address));

            // Close
            serverSocket.Dispose();
        }

        [Fact]
        public void SocketPair()
        {
            SocketPair pair = Socket.CreatePair(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified, blocking: false);
            PosixResult result;

            // Send
            result = pair.Socket1.TrySend(s_data);

            // Receive
            byte[] receiveBuffer = new byte[10];
            result = pair.Socket2.TryReceive(new ArraySegment<byte>(receiveBuffer));
            Assert.True(result.IsSuccess);
            Assert.Equal(s_data.Count, result.Value);

            // Close
            pair.Dispose();
        }

        [Fact]
        public void PassHandle()
        {
            PosixResult result;

            // server socket
            var serverSocket = Socket.Create(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp, blocking: true);
            var serverAddress = new IPEndPointStruct(IPAddress.Loopback, 0);
            serverSocket.Bind(serverAddress);
            serverAddress = serverSocket.GetLocalIPAddress();
            serverSocket.Listen(10);

            // client connect
            var clientSocket = Socket.Create(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp, blocking: true);
            clientSocket.TryConnect(serverAddress);

            // accept and pass socket
            SocketPair pair = Socket.CreatePair(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified, blocking: false);
            result = serverSocket.TryAcceptAndSendHandleTo(pair.Socket1);
            Assert.True(result.IsSuccess);
            
            // receive accept socket
            Socket acceptSocket;
            result = pair.Socket2.TryReceiveSocket(out acceptSocket, blocking: true);
            Assert.True(result.IsSuccess);
            Assert.Equal(1, result.Value);

            // Send
            result = clientSocket.TrySend(s_data);

            // Receive
            byte[] receiveBuffer = new byte[10];
            result = acceptSocket.TryReceive(new ArraySegment<byte>(receiveBuffer));
            Assert.True(result.IsSuccess);
            Assert.Equal(s_data.Count, result.Value);

            // Close
            pair.Dispose();
            serverSocket.Dispose();
            clientSocket.Dispose();
            acceptSocket.Dispose();
        }
    }
}
