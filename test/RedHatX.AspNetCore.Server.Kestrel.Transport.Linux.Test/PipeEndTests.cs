using System;
using RedHatX.AspNetCore.Server.Kestrel.Transport.Linux;
using Tmds.Posix;
using Xunit;

namespace Tests
{
    public class PipeTests
    {
        [Fact]
        public void CreateAndDispose() 
        {
            var pair = PipeEnd.CreatePair(blocking: true);
            var readEnd = pair.ReadEnd;
            var writeEnd = pair.WriteEnd;
            Assert.NotNull(readEnd);
            Assert.NotNull(writeEnd);
            Assert.True(!readEnd.IsInvalid);
            Assert.True(!writeEnd.IsInvalid);
            readEnd.Dispose();
            writeEnd.Dispose();
        }

        [Fact]
        public void WriteAndRead() 
        {
            var pair = PipeEnd.CreatePair(blocking: true);
            var readEnd = pair.ReadEnd;
            var writeEnd = pair.WriteEnd;

            // Write
            var writeBytes = new byte[] { 1, 2, 3 };
            var writeResult = writeEnd.TryWrite(new ArraySegment<byte>(writeBytes));
            Assert.True(writeResult.IsSuccess);

            // Read
            var receiveBytes = new byte[] { 0, 0, 0, 0, 0 };
            var readResult = readEnd.TryRead(new ArraySegment<byte>(receiveBytes, 1, 4));
            Assert.True(readResult.IsSuccess);
            Assert.Equal(3, readResult.Value);
            Assert.Equal(new byte[] {0, 1, 2, 3, 0}, receiveBytes);

            // Clean-up
            readEnd.Dispose();
            writeEnd.Dispose();
        }

        [Fact]
        public void NonBlockingRead() 
        {
            var pair = PipeEnd.CreatePair(blocking: false);
            var readEnd = pair.ReadEnd;
            var writeEnd = pair.WriteEnd;

            var receiveBytes = new byte[] { 0, 0, 0, 0, 0 };
            var readResult = readEnd.TryRead(new ArraySegment<byte>(receiveBytes, 1, 4));
            Assert.True(readResult == PosixResult.EAGAIN);

            // Clean-up
            readEnd.Dispose();
            writeEnd.Dispose();
        }
    }
}
