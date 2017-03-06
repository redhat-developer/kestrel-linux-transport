// Copyright 2017 Tom Deseyn <tom.deseyn@gmail.com>
// This software is made available under the MIT License
// See LICENSE for details

using System;
using System.Runtime.InteropServices;
using Tmds.Posix;

namespace Tmds.Kestrel.Linux
{
    struct PipeEndPair
    {
        public PipeEnd ReadEnd;
        public PipeEnd WriteEnd;
    }

    static class PipeInterop
    {
        [DllImport(Interop.Library, EntryPoint="TmdsKL_Pipe")]
        public extern static PosixResult Pipe(out PipeEnd readEnd, out PipeEnd writeEnd, bool blocking);
    }

    class PipeEnd : CloseSafeHandle
    {
        private PipeEnd()
        {}

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