using System;
using System.Buffers;
using System.IO;
using System.IO.Pipelines;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Protocols;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Abstractions.Internal;
using RedHatX.AspNetCore.Server.Kestrel.Transport.Linux;
using Xunit;

namespace Tests
{
    public class TransportTests
    {
        [InlineData(true)]
        [InlineData(false)]
        [Theory]
        public async Task Echo(bool deferAccept)
        {
            using (var testServer = new TestServer(new TestServerOptions() { DeferAccept = deferAccept }))
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
            }
        }

        [Fact]
        public async Task MultiThread()
        {
            using (var testServer = new TestServer(new TestServerOptions() { ThreadCount = 2 }))
            {
                await testServer.BindAsync();
                await testServer.UnbindAsync();
                await testServer.StopAsync();
            }
        }

        [Fact]
        public async Task Unbind()
        {
            using (var testServer = new TestServer())
            {
                await testServer.BindAsync();
                await testServer.UnbindAsync();
                var exception = Assert.Throws<IOException>(() => testServer.ConnectTo());
                Assert.Equal(PosixResult.ECONNREFUSED, exception.HResult);
            }
        }

        [Fact]
        public async Task StopDisconnectsClient()
        {
            var outputTcs = new TaskCompletionSource<PipeWriter>();
            TestServerConnectionHandler connectionHandler = async (input, output) =>
            {
                outputTcs.SetResult(output);
            };
            using (var testServer = new TestServer(connectionHandler))
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
            }
        }

        [Fact]
        public async Task Writable()
        {
            const int bufferSize = 2048;
            int bytesWritten = 0;
            var waitingForWritable = new TaskCompletionSource<object>();
            TestServerConnectionHandler connectionHandler = async (input, output) =>
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

            using (var testServer = new TestServer(connectionHandler))
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
            }
        }

        [Fact]
        public async Task Write_Timeout()
        {
            const int bufferSize = 2048;
            var waitingForTimeout = new TaskCompletionSource<object>();
            TestServerConnectionHandler connectionHandler = async (input, output) =>
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

            using (var testServer = new TestServer(connectionHandler))
            {
                await testServer.BindAsync();
                using (var client = testServer.ConnectTo())
                {
                    // wait for the server to timeout our connection
                    // because we aren't reading
                    await waitingForTimeout.Task;
                }
            }
        }

        [Fact]
        public async Task CompletingOutputCancelsInput()
        {
            var inputCompletedTcs = new TaskCompletionSource<object>();
            TestServerConnectionHandler connectionHandler = async (input, output) =>
            {
                output.Complete();
                await input.ReadAsync();
                inputCompletedTcs.SetResult(null);
            };

            using (var testServer = new TestServer(connectionHandler))
            {
                await testServer.BindAsync();
                using (var client = testServer.ConnectTo())
                {
                    await inputCompletedTcs.Task;
                }
            }
        }

        [Fact]
        public async Task Receive()
        {
            // client send 1M bytes which are an int counter
            // server receives and checkes the counting
            const int receiveLength = 1000000;
            TestServerConnectionHandler connectionHandler = async (input, output) =>
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

            using (var testServer = new TestServer(new TestServerOptions() { ConnectionHandler = connectionHandler}))
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
            }
        }

        [Fact]
        public async Task Send()
        {
            // server send 1M bytes which are an int counter
            // client receives and checkes the counting
            const int sendLength = 1_000_000;
            TestServerConnectionHandler connectionHandler = async (input, output) =>
            {
                FillBuffer(output, sendLength / 4);
                await output.FlushAsync();
                output.Complete();
                input.Complete();
            };

            using (var testServer = new TestServer(connectionHandler))
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
            }
        }

        [Fact]
        public async Task UnixSocketListenType()
        {
            TestServerConnectionHandler connectionHandler = async (input, output) =>
            {
                int threadId = Thread.CurrentThread.ManagedThreadId;
                var data = Encoding.UTF8.GetBytes(threadId.ToString());

                output.Write(data);
                await output.FlushAsync();

                output.Complete();
                input.Complete();
            };

            using (var testServer = new TestServer(new TestServerOptions()
                                        { ConnectionHandler = connectionHandler,
                                        ThreadCount = 2,
                                        UnixSocketPath = $"{Path.GetTempPath()}/{Path.GetRandomFileName()}" }))
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
            }
        }

        private unsafe static void FillBuffer(PipeWriter writer, int count)
        {
            for (int i = 0; i < count; i++)
            {
                var memory = writer.GetMemory(4);
                var bufferHandle = memory.Retain(pin: true);
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

        private static unsafe void AssertCounter(ref ReadOnlyBuffer<byte> buffer, ref int bytesReceived, ref int remainderRef)
        {
            int remainder = remainderRef;
            int currentValue = bytesReceived / 4;
            foreach (var memory in buffer)
            {
                var bufferHandle = memory.Retain(pin: true);
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
}