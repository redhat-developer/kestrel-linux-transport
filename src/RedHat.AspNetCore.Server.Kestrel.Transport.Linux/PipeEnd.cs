using System;
using System.Runtime.InteropServices;
using static Tmds.Linux.LibC;

namespace RedHat.AspNetCore.Server.Kestrel.Transport.Linux
{
    struct PipeEndPair
    {
        public PipeEnd ReadEnd;
        public PipeEnd WriteEnd;

        public void Dispose()
        {
            ReadEnd?.Dispose();
            WriteEnd?.Dispose();
        }
    }

    static class PipeInterop
    {
        public unsafe static PosixResult Pipe(out PipeEnd readEnd, out PipeEnd writeEnd, bool blocking)
        {
            int* fds = stackalloc int[2];
            int flags = O_CLOEXEC;
            if (!blocking)
            {
                flags |= O_NONBLOCK;
            }

            readEnd = new PipeEnd();
            writeEnd = new PipeEnd();

            int res = pipe2(fds, flags);

            if (res == 0)
            {
                readEnd.SetHandle(fds[0]);
                writeEnd.SetHandle(fds[1]);
            }
            else
            {
                readEnd = null;
                writeEnd = null;
            }

            return PosixResult.FromReturnValue(res);
        }
    }

    class PipeEnd : CloseSafeHandle
    {
        internal PipeEnd()
        {}

        public void WriteByte(byte b)
        {
            TryWriteByte(b)
                .ThrowOnError();
        }

        public unsafe PosixResult TryWriteByte(byte b)
        {
            return base.TryWrite(&b, 1);
        }

        public unsafe byte ReadByte()
        {
            byte b = 0;
            var result = base.TryRead(&b, 1);
            result.ThrowOnError();
            return b;
        }

        public unsafe PosixResult TryReadByte()
        {
            byte b;
            var result = base.TryRead(&b, 1);
            if (result.IsSuccess)
            {
                return new PosixResult(b);
            }
            else
            {
                return result;
            }
        }

        public int Write(ArraySegment<byte> buffer)
        {
            var result = TryWrite(buffer);
            result.ThrowOnError();
            return result.Value;
        }

        public new PosixResult TryWrite(ArraySegment<byte> buffer)
        {
            return base.TryWrite(buffer);
        }

        public int Read(ArraySegment<byte> buffer)
        {
            var result = TryRead(buffer);
            result.ThrowOnError();
            return result.Value;
        }

        public new PosixResult TryRead(ArraySegment<byte> buffer)
        {
            return base.TryRead(buffer);
        }

        public static PipeEndPair CreatePair(bool blocking)
        {
            PipeEnd readEnd;
            PipeEnd writeEnd;
            var result = PipeInterop.Pipe(out readEnd, out writeEnd, blocking);
            result.ThrowOnError();
            return new PipeEndPair { ReadEnd = readEnd, WriteEnd = writeEnd };
        }
    }
}