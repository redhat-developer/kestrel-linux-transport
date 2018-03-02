using System;
using System.Runtime.InteropServices;

namespace RedHatX.AspNetCore.Server.Kestrel.Transport.Linux
{
    enum AioOpCode : ushort
    {
        PRead = 0,
        PWrite = 1,
        FSync = 2,
        FdSync = 3,
        PReadX = 4,
        Poll = 5,
        Noop = 6,
        PReadv = 7,
        PWritev = 8
    }

    struct AioEvent
    {
        public long          Data;
        private ulong       _iocb;
        private long        _res;
        private long        _res2;

        public PosixResult   Result => new PosixResult((int)_res);
        public unsafe AioCb* AioCb => (AioCb*)_iocb;
    }

    struct AioCb
    {
        public long          Data;
        private long        _keyRwFlags;
        public AioOpCode     OpCode;
        private short       _reqPrio;
        public int           Fd;
        private ulong       _buf;
        private ulong       _nBytes;
        private long        _offset;
        private ulong       _reserved2;
        private uint        _flags;
        private uint        _resFd;

        public unsafe void*  Buffer { set { _buf = (ulong)value; } }
        public int           Length { set { _nBytes = (ulong)value; } }
    }

    static class AioInterop
    {
        [DllImport(Interop.Library, EntryPoint = "RHXKL_IoSetup")]

        public static extern PosixResult IoSetup(int nr, out IntPtr ctxp);

        [DllImport(Interop.Library, EntryPoint = "RHXKL_IoDestroy")]
        public static extern PosixResult IoDestroy(IntPtr ctxp);

        [DllImport(Interop.Library, EntryPoint = "RHXKL_IoSubmit")]
        public unsafe static extern PosixResult IoSubmit(IntPtr ctxp, int nr, AioCb** iocbpp);

        [DllImport(Interop.Library, EntryPoint = "RHXKL_IoGetEvents")]
        public unsafe static extern PosixResult IoGetEvents(IntPtr ctxp, int minNr, int maxNr, AioEvent* events, int timeoutMs);
    }
}