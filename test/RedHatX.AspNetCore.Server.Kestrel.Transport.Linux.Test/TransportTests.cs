using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Server.Kestrel.Internal.System.IO.Pipelines;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Abstractions;
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
            using (var testServer = new TestServer())
            {
                await testServer.BindAsync();

                using (var client = testServer.ConnectTo())
                {
                    await testServer.UnbindAsync();
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
                    var buffer = output.Alloc(bufferSize);
                    buffer.Advance(bufferSize);
                    bytesWritten += bufferSize;

                    // If it takes 1 second to write, assume the socket
                    // is no longer writable
                    writeTimeout.Change(1000, Timeout.Infinite);
                    await buffer.FlushAsync();
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
        public async Task CompletingOutputCancelsInput()
        {
            var inputCompletedTcs = new TaskCompletionSource<object>();
            TestServerConnectionHandler connectionHandler = async (input, output) =>
            {
                output.Complete();
                bool expectedException = false;
                try
                {
                    await input.ReadAsync();
                }
                catch (ConnectionAbortedException)
                {
                    expectedException = true;
                    inputCompletedTcs.SetResult(null);
                }
                Assert.True(expectedException);
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
                        input.Advance(buffer.End);
                        break;
                    }
                    AssertCounter(ref buffer, ref bytesReceived, ref remainder);
                    input.Advance(buffer.End);
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
            const int sendLength = 1000000;
            TestServerConnectionHandler connectionHandler = async (input, output) =>
            {
                var buffer = output.Alloc(0);
                FillBuffer(ref buffer, sendLength / 4);
                await buffer.FlushAsync();
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

        private unsafe static void FillBuffer(ref WritableBuffer wb, int count)
        {
            for (int i = 0; i < count; i++)
            {
                wb.Ensure(4);
                void* pointer;
                Assert.True(wb.Buffer.TryGetPointer(out pointer));
                *(int*)pointer = i;
                wb.Advance(4);
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

        private static unsafe void AssertCounter(ref ReadableBuffer buffer, ref int bytesReceived, ref int remainderRef)
        {
            int remainder = remainderRef;
            int currentValue = bytesReceived / 4;
            foreach (var memory in buffer)
            {
                void* pointer;
                Assert.True(memory.TryGetPointer(out pointer));
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
            }
            remainderRef = remainder;
        }
    }
}