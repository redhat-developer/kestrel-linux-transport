using System;
using System.Runtime.InteropServices;
using Tmds.Posix;

namespace RedHatX.AspNetCore.Server.Kestrel.Transport.Linux
{
    struct PipeEndPair : IDisposable
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
        [DllImport(Interop.Library, EntryPoint="RHXKL_Pipe")]
        public extern static PosixResult Pipe(out PipeEnd readEnd, out PipeEnd writeEnd, bool blocking);
    }

    class PipeEnd : CloseSafeHandle
    {
        private PipeEnd()
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