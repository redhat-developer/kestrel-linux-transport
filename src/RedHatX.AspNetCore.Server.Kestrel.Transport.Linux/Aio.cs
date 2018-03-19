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

        public unsafe void*  Buffer { set { _buf = (ulong)value; } get { return (void*)_buf; } }
        public int           Length { set { _nBytes = (ulong)value; } }
    }

    struct AioRing
    {
        public int Id;
        public int Nr;
        public volatile int Head;
        public volatile int Tail;
        public uint Magic;
        public uint CompatFeatures;
        public uint IncompatFeatures;
        public int HeaderLength;
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

        public unsafe static PosixResult IoGetEvents(IntPtr ctxp, int nr, AioEvent* events)
        {
            if (nr <= 0)
            {
                return new PosixResult(PosixResult.EINVAL);
            }
            AioRing* pRing = (AioRing*)ctxp;
            if (pRing->Magic == 0xa10a10a1 && pRing->IncompatFeatures == 0)
            {
                int head = pRing->Head;
                int tail = pRing->Tail;
                int available = tail - head;
                if (available < 0)
                {
                    available += pRing->Nr;
                }
                if (available >= nr)
                {
                    AioEvent* ringEvents = (AioEvent*)((byte*)pRing + pRing->HeaderLength);
                    AioEvent* start = ringEvents + head;
                    AioEvent* end = start + nr;
                    if (head + nr > pRing->Nr)
                    {
                        end -= pRing->Nr;
                    }
                    if (end > start)
                    {
                        Copy(start, end, events);
                    }
                    else
                    {
                        AioEvent* eventsEnd = Copy(start, ringEvents + pRing->Nr, events);
                        Copy(ringEvents, end, eventsEnd);
                    }
                    head += nr;
                    if (head >= pRing->Nr)
                    {
                        head -= pRing->Nr;
                    }
                    pRing->Head = head;
                    return new PosixResult(nr);
                }
            }
            return IoGetEvents(ctxp, nr, nr, events, -1);
        }

        private static unsafe AioEvent* Copy(AioEvent* start, AioEvent* end, AioEvent* dst)
        {
            while (start < end)
            {
                *dst++ = *start++;
            }
            return dst;
        }
    }
}