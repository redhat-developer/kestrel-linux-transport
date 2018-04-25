using System;
using System.Buffers;
using System.IO;
using System.IO.Pipelines;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Abstractions.Internal;
using RedHatX.AspNetCore.Server.Kestrel.Transport.Linux;
using Xunit;

namespace Tests
{
    public abstract class TransportTestsBase
    {
        protected abstract TestServerOptions CreateOptions();

        private TestServer CreateTestServer(Action<TestServerOptions> configure = null)
        {
            TestServerOptions options = CreateOptions();
            configure?.Invoke(options);
            return new TestServer(options);
        }

        private TestServer CreateTestServer(TestServerConnectionDispatcher connectionDispatcher)
            => CreateTestServer(options => options.ConnectionDispatcher = connectionDispatcher);

        [InlineData(true)]
        [InlineData(false)]
        [Theory]
        public async Task Echo_DeferAccept(bool deferAccept)
        {
            using (var testServer = CreateTestServer(options => options.DeferAccept = deferAccept))
            {
                await testServer.BindAsync();
                using (var client = testServer.ConnectTo())
                {
                    // Send some bytes
                    byte[] sendBuffer = new byte[] { 1, 2, 3 };
                    client.Send(new ArraySegment<byte>(sendBuffer));

                    // Read the echo
                    byte[] receiveBuffer = new byte[10];
                    var received = client.Receive(new ArraySegment<byte>(receiveBuffer));
                    Assert.Equal(sendBuffer.Length, received);
                }
                await testServer.StopAsync();
            }
        }

        [InlineData(true)]
        [InlineData(false)]
        [Theory]
        public async Task Echo_CheckAvailable(bool checkAvailable)
        {
            using (var testServer = CreateTestServer(options => options.CheckAvailable = checkAvailable))
            {
                await testServer.BindAsync();
                using (var client = testServer.ConnectTo())
                {
                    // Send some bytes
                    byte[] sendBuffer = new byte[] { 1, 2, 3 };
                    client.Send(new ArraySegment<byte>(sendBuffer));

                    // Read the echo
                    byte[] receiveBuffer = new byte[10];
                    var received = client.Receive(new ArraySegment<byte>(receiveBuffer));
                    Assert.Equal(sendBuffer.Length, received);
                }
                await testServer.StopAsync();
            }
        }

        [Fact]
        public async Task MultiThread()
        {
            using (var testServer = CreateTestServer(options => options.ThreadCount = 2))
            {
                await testServer.BindAsync();
                await testServer.UnbindAsync();
                await testServer.StopAsync();
            }
        }

        [Fact]
        public async Task Unbind()
        {
            using (var testServer = CreateTestServer())
            {
                await testServer.BindAsync();
                await testServer.UnbindAsync();
                var exception = Assert.Throws<IOException>(() => testServer.ConnectTo());
                Assert.Equal(PosixResult.ECONNREFUSED, exception.HResult);
                await testServer.StopAsync();
            }
        }

        [Fact]
        public async Task StopDisconnectsClient()
        {
            var outputTcs = new TaskCompletionSource<PipeWriter>();
            TestServerConnectionDispatcher connectionDispatcher = async (input, output) =>
            {
                outputTcs.SetResult(output);
            };

            using (var testServer = CreateTestServer(connectionDispatcher))
            {
                await testServer.BindAsync();

                using (var client = testServer.ConnectTo())
                {
                    // Server shutdown:
                    await testServer.UnbindAsync();
                    // Complete existing connections
                    PipeWriter clientOutput = await outputTcs.Task;
                    clientOutput.Complete(new ConnectionAbortedException());
                    await testServer.StopAsync();

                    // receive returns EOF                
                    byte[] receiveBuffer = new byte[10];
                    var received = client.Receive(new ArraySegment<byte>(receiveBuffer));
                    Assert.Equal(0, received);

                    // send returns EPIPE
                    var exception = Assert.Throws<IOException>(() =>
                    {
                        for (int i = 0; i < 10; i++)
                        {
                            byte[] sendBuffer = new byte[] { 1, 2, 3 };
                            client.Send(new ArraySegment<byte>(sendBuffer));
                        }                    
                    });
                    Assert.Equal(PosixResult.EPIPE, exception.HResult);
                }

                await testServer.StopAsync();
            }
        }

        [Fact]
        public async Task Writable()
        {
            const int bufferSize = 2048;
            int bytesWritten = 0;
            var waitingForWritable = new TaskCompletionSource<object>();
            TestServerConnectionDispatcher connectionDispatcher = async (input, output) =>
            {
                Timer writeTimeout = new Timer(
                    // timeout -> we are waiting for the socket to become writable
                    o => waitingForWritable.SetResult(null),
                    null, Timeout.Infinite, Timeout.Infinite
                );

                do
                {
                    var memory = output.GetMemory(bufferSize);
                    output.Advance(bufferSize);
                    bytesWritten += bufferSize;

                    // If it takes 1 second to write, assume the socket
                    // is no longer writable
                    writeTimeout.Change(1000, Timeout.Infinite);
                    await output.FlushAsync();
                    // cancel the timeout
                    writeTimeout.Change(Timeout.Infinite, Timeout.Infinite);

                } while (!waitingForWritable.Task.IsCompleted);

                writeTimeout.Dispose();
                output.Complete();
                input.Complete();
            };

            using (var testServer = CreateTestServer(connectionDispatcher))
            {
                await testServer.BindAsync();
                using (var client = testServer.ConnectTo())
                {
                    // wait for the server to have sent so much data
                    // so it waiting for us to read some
                    await waitingForWritable.Task;

                    // read all the data
                    int receivedBytes = 0;
                    byte[] receiveBuffer = new byte[bufferSize];
                    while (receivedBytes < bytesWritten)
                    {
                        var received = client.Receive(new ArraySegment<byte>(receiveBuffer));
                        receivedBytes += received;
                    }
                }

                await testServer.StopAsync();
            }
        }

        [Fact]
        public async Task Write_Timeout()
        {
            const int bufferSize = 2048;
            var waitingForTimeout = new TaskCompletionSource<object>();
            TestServerConnectionDispatcher connectionDispatcher = async (input, output) =>
            {
                Timer writeTimeout = new Timer(
                    // timeout -> we are waiting for the socket to become writable
                    o => output.Complete(new Exception("Write timed out")),
                    null, Timeout.Infinite, Timeout.Infinite
                );

                try
                {
                    do
                    {
                        var memory = output.GetMemory(bufferSize);
                        output.Advance(bufferSize);

                        // If it takes 1 second to write, assume the socket
                        // is no longer writable
                        writeTimeout.Change(1000, Timeout.Infinite);
                        var flushResult = await output.FlushAsync();
                        // cancel the timeout
                        writeTimeout.Change(Timeout.Infinite, Timeout.Infinite);
                    } while (true);
                }
                catch
                { }

                waitingForTimeout.SetResult(null);

                writeTimeout.Dispose();
                output.Complete();
                input.Complete();
            };

            using (var testServer = CreateTestServer(connectionDispatcher))
            {
                await testServer.BindAsync();
                using (var client = testServer.ConnectTo())
                {
                    // wait for the server to timeout our connection
                    // because we aren't reading
                    await waitingForTimeout.Task;
                }

                await testServer.StopAsync();
            }
        }

        [Fact]
        public async Task CompletingOutputCancelsInput()
        {
            var inputCompletedTcs = new TaskCompletionSource<object>();
            TestServerConnectionDispatcher connectionDispatcher = async (input, output) =>
            {
                output.Complete();
                await input.ReadAsync();
                inputCompletedTcs.SetResult(null);
            };

            using (var testServer = CreateTestServer(connectionDispatcher))
            {
                await testServer.BindAsync();
                using (var client = testServer.ConnectTo())
                {
                    await inputCompletedTcs.Task;
                }

                await testServer.StopAsync();
            }
        }

        [Fact]
        public async Task Receive()
        {
            // client send 1M bytes which are an int counter
            // server receives and checkes the counting
            const int receiveLength = 1000000;
            TestServerConnectionDispatcher connectionDispatcher = async (input, output) =>
            {
                int bytesReceived = 0;
                int remainder = 0; // remaining bytes between ReadableBuffers
                while (true)
                {
                    var readResult = await input.ReadAsync();
                    var buffer = readResult.Buffer;
                    if (buffer.IsEmpty && readResult.IsCompleted)
                    {
                        input.AdvanceTo(buffer.End);
                        break;
                    }
                    AssertCounter(ref buffer, ref bytesReceived, ref remainder);
                    input.AdvanceTo(buffer.End);
                }
                Assert.Equal(receiveLength, bytesReceived);
                output.Complete();
                input.Complete();
            };

            using (var testServer = CreateTestServer(connectionDispatcher))
            {
                await testServer.BindAsync();
                using (var client = testServer.ConnectTo())
                {
                    var buffer = new byte[1000000];
                    FillBuffer(new ArraySegment<byte>(buffer), 0);
                    int offset = 0;
                    do
                    {
                        offset += client.Send(new ArraySegment<byte>(buffer, offset, buffer.Length - offset));
                    } while (offset != buffer.Length);
                    client.Shutdown(SocketShutdown.Send);

                    // wait for the server to stop
                    var receiveBuffer = new byte[1];
                    client.Receive(new ArraySegment<byte>(receiveBuffer));
                }

                await testServer.StopAsync();
            }
        }

        [Fact]
        public async Task Send()
        {
            // server send 1M bytes which are an int counter
            // client receives and checkes the counting
            const int sendLength = 1_000_000;
            TestServerConnectionDispatcher connectionDispatcher = async (input, output) =>
            {
                FillBuffer(output, sendLength / 4);
                await output.FlushAsync();
                output.Complete();
                input.Complete();
            };

            using (var testServer = CreateTestServer(connectionDispatcher))
            {
                await testServer.BindAsync();
                using (var client = testServer.ConnectTo())
                {
                    int totalReceived = 0;
                    var receiveBuffer = new byte[4000];
                    bool eof = false;
                    do
                    {
                        int offset = 0;
                        int received = 0;
                        do
                        {
                            var receive = client.Receive(new ArraySegment<byte>(receiveBuffer, offset, receiveBuffer.Length - offset));
                            received += receive;
                            offset += receive;
                            eof = receive == 0;
                        } while (!eof && offset != receiveBuffer.Length);

                        AssertCounter(new ArraySegment<byte>(receiveBuffer, 0, received), totalReceived / 4);
                        totalReceived += received;
                    } while (!eof);
                    Assert.True(totalReceived == sendLength);
                }

                await testServer.StopAsync();
            }
        }

        [Fact]
        public async Task UnixSocketListenType()
        {
            TestServerConnectionDispatcher connectionDispatcher = async (input, output) =>
            {
                int threadId = Thread.CurrentThread.ManagedThreadId;
                var data = Encoding.UTF8.GetBytes(threadId.ToString());

                output.Write(data);
                await output.FlushAsync();

                output.Complete();
                input.Complete();
            };

            using (var testServer = CreateTestServer(options =>
                                        { options.ConnectionDispatcher = connectionDispatcher;
                                          options.ThreadCount = 2;
                                          options.UnixSocketPath = $"{Path.GetTempPath()}/{Path.GetRandomFileName()}"; }))
            {
                await testServer.BindAsync();

                int[] threadIds = new int[4];
                for (int i = 0; i < 4; i++)
                {
                    using (var client = testServer.ConnectTo())
                    {
                        byte[] receiveBuffer = new byte[10];
                        int received = client.Receive(new ArraySegment<byte>(receiveBuffer));
                        int threadId;
                        Assert.NotEqual(0, received);
                        Assert.True(int.TryParse(Encoding.UTF8.GetString(receiveBuffer, 0, received), out threadId));
                        threadIds[i] = threadId;

                        // check if the server closed the client.
                        // this would fail if not all fds for this client are closed
                        received = client.Receive(new ArraySegment<byte>(receiveBuffer));
                        Assert.Equal(0, received);
                    }
                }

                // check we are doing round robin over 2 handling threads
                Assert.NotEqual(threadIds[0], threadIds[1]);
                Assert.Equal(threadIds[0], threadIds[2]);
                Assert.Equal(threadIds[1], threadIds[3]);

                await testServer.StopAsync();
            }
        }

        [Fact]
        public async Task FailedBindThrows()
        {
            int port = 50;
            using (var testServer = CreateTestServer(options =>
                                options.IPEndPoint = new IPEndPoint(IPAddress.Loopback, port)))
            {
                await Assert.ThrowsAnyAsync<Exception>(() => testServer.BindAsync());

                await testServer.StopAsync();
            }
        }

        [Fact]
        public async Task BatchedSendReceive()
        {
            // We block the TransportThread to ensure 2 clients are sending multiple buffers with random data.
            // These buffers are echoed back.
            // The clients verify they each receive the random data they sent.

            int connectionCount = 0;
            SemaphoreSlim clientsAcceptedSemaphore = new SemaphoreSlim(0, 1);
            SemaphoreSlim dataSentSemaphore = new SemaphoreSlim(0, 1);

            TestServerConnectionDispatcher connectionDispatcher = async (input, output) =>
            {
                connectionCount++;

                if (connectionCount == 3)
                {
                    // Ensure we accepted the clients.
                    clientsAcceptedSemaphore.Release();

                    // Now wait for clients to send data.
                    dataSentSemaphore.Wait();
                }

                // Echo
                while (true)
                {
                    var result = await input.ReadAsync();
                    var request = result.Buffer;

                    if (request.IsEmpty && result.IsCompleted)
                    {
                        input.AdvanceTo(request.End);
                        break;
                    }

                    // Clients send more data than what fits in a single segment.
                    Assert.True(!request.IsSingleSegment);

                    foreach (var memory in request)
                    {
                        output.Write(memory.Span);
                    }
                    await output.FlushAsync();
                    input.AdvanceTo(request.End);
                }

                output.Complete();
                input.Complete();
            };

            using (var testServer = CreateTestServer(connectionDispatcher))
            {
                await testServer.BindAsync();

                using (var client1 = testServer.ConnectTo())
                {
                    using (var client2 = testServer.ConnectTo())
                    {
                        using (var client3 = testServer.ConnectTo())
                        { }

                        // Wait for all clients to be accepted.
                        // The TransportThread will now be blocked.
                        clientsAcceptedSemaphore.Wait();

                        // Send data
                        var client1DataSent = new byte[10_000];
                        FillRandom(client1DataSent);
                        var client2DataSent = new byte[10_000];
                        FillRandom(client2DataSent);
                        client1.Send(new ArraySegment<byte>(client1DataSent));
                        client2.Send(new ArraySegment<byte>(client2DataSent));

                        // Unblock the TransportThread
                        dataSentSemaphore.Release();

                        // Receive echoed data.
                        var client1DataReceived = new byte[10_000];
                        var client2DataReceived = new byte[10_000];
                        client1.Receive(new ArraySegment<byte>(client1DataReceived));
                        client2.Receive(new ArraySegment<byte>(client2DataReceived));
                        Assert.Equal(client1DataSent, client1DataReceived);
                        Assert.Equal(client2DataSent, client2DataReceived);
                    }
                }

                await testServer.StopAsync();
            }
        }

        private static Random s_random = new System.Random();
        private void FillRandom(byte[] data)
        {
            for (int i = 0; i < data.Length; i++)
            {
                data[i] = (byte)s_random.Next(256);
            }
        }

        private unsafe static void FillBuffer(PipeWriter writer, int count)
        {
            for (int i = 0; i < count; i++)
            {
                var memory = writer.GetMemory(4);
                var bufferHandle = memory.Pin();
                void* pointer = bufferHandle.Pointer;
                *(int*)pointer = i;
                bufferHandle.Dispose();
                writer.Advance(4);
            }
        }

        private unsafe static void FillBuffer(ArraySegment<byte> segment, int value)
        {
            Assert.True(segment.Count % 4 == 0);
            fixed (byte* bytePtr = segment.Array)
            {
                int* intPtr = (int*)(bytePtr + segment.Offset);
                for (int i = 0; i < segment.Count / 4; i++)
                {
                    *intPtr++ = value++;
                }
            }
        }

        private unsafe static void AssertCounter(ArraySegment<byte> segment, int value)
        {
            Assert.True(segment.Count % 4 == 0);
            fixed (byte* bytePtr = segment.Array)
            {
                int* intPtr = (int*)(bytePtr + segment.Offset);
                for (int i = 0; i < segment.Count / 4; i++)
                {
                    Assert.Equal(value++, *intPtr++);
                }
            }
        }

        private static unsafe void AssertCounter(ref ReadOnlySequence<byte> buffer, ref int bytesReceived, ref int remainderRef)
        {
            int remainder = remainderRef;
            int currentValue = bytesReceived / 4;
            foreach (var memory in buffer)
            {
                var bufferHandle = memory.Pin();
                void* pointer = bufferHandle.Pointer;
                byte* pMemory = (byte*)pointer;
                int length = memory.Length;

                // remainder
                int offset = bytesReceived % 4;
                if (offset != 0)
                {
                    int read = Math.Min(length, 4 - offset);
                    byte* ptr = (byte*)&remainder;
                    ptr += offset;
                    for (int i = 0; i < read; i++)
                    {
                        *ptr++ = *pMemory++;
                    }
                    length -= read;
                    if (read == (4 - offset))
                    {
                        Assert.Equal(currentValue++, remainder);
                    }
                }

                // whole ints
                int* pMemoryInt = (int*)pMemory;
                int count = length / 4;
                for (int i = 0; i < count; i++)
                {
                    Assert.Equal(currentValue++, *pMemoryInt++);
                    length -= 4;
                }

                // remainder
                if (length != 0)
                {
                    pMemory = (byte*)pMemoryInt;
                    byte* ptr = (byte*)&remainder;
                    for (int i = 0; i < length; i++)
                    {
                        *ptr++ = *pMemory++;
                    }
                }
                bytesReceived += memory.Length;
                bufferHandle.Dispose();
            }
            remainderRef = remainder;
        }
    }

    public sealed class DefaultOptionsTransportTests : TransportTestsBase
    {
        protected override TestServerOptions CreateOptions() => new TestServerOptions();
    }

    public sealed class AioTransportTests : TransportTestsBase
    {
        protected override TestServerOptions CreateOptions()
            => new TestServerOptions()
            {
                AioReceive = true,
                AioSend = true
            };
    }
}