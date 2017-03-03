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
        public extern static PosixResult Pipe(out PipeEnd readEnd, out PipeEnd writeEnd);
    }

    class PipeEnd : CloseSafeHandle
    {
        private PipeEnd()
        {}

        public new PosixResult Write(ArraySegment<byte> buffer)
        {
            return base.Write(buffer);
        }

        public new PosixResult Read(ArraySegment<byte> buffer)
        {
            return base.Read(buffer);
        }

        public static PipeEndPair CreatePair()
        {
            PipeEnd readEnd;
            PipeEnd writeEnd;
            var result = PipeInterop.Pipe(out readEnd, out writeEnd);
            result.ThrowOnError();
            return new PipeEndPair { ReadEnd = readEnd, WriteEnd = writeEnd };
        }
    }
}