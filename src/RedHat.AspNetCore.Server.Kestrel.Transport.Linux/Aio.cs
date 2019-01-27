using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Tmds.Linux;
using static Tmds.Linux.LibC;

namespace RedHat.AspNetCore.Server.Kestrel.Transport.Linux
{
    static class AioInterop
    {
        public unsafe static PosixResult IoSetup(int nr, aio_context_t* ctx)
        {
            int rv = io_setup((uint)nr, ctx);

            return PosixResult.FromReturnValue(rv);
        }

        public unsafe static PosixResult IoDestroy(aio_context_t ctx)
        {
            int rv = io_destroy(ctx);

            return PosixResult.FromReturnValue(rv);
        }

        public unsafe static PosixResult IoSubmit(aio_context_t ctx, int nr, iocb** iocbpp)
        {
            int rv = io_submit(ctx, nr, iocbpp);

            return PosixResult.FromReturnValue(rv);
        }


        public unsafe static PosixResult IoGetEvents(aio_context_t ctx, int min_nr, int nr, io_event* events, int timeoutMs)
        {
            timespec timeout = default(timespec);
            bool hasTimeout = timeoutMs >= 0;
            if (hasTimeout)
            {
                timeout.tv_sec = timeoutMs / 1000;
                timeout.tv_nsec = 1000 * (timeoutMs % 1000);
            }
            int rv;
            do
            {
                rv = io_getevents(ctx, min_nr, nr, events, hasTimeout ? &timeout : null);
            } while (rv < 0 && errno == EINTR);

            return PosixResult.FromReturnValue(rv);
        }

        public unsafe static PosixResult IoGetEvents(aio_context_t ctx, int nr, io_event* events)
        {
            aio_ring* pRing = ctx.ring;
            if (nr <= 0)
            {
                return new PosixResult(PosixResult.EINVAL);
            }
            if (pRing->magic == 0xa10a10a1 && pRing->incompat_features == 0)
            {
                int head = (int)pRing->head;
                int tail = (int)pRing->tail;
                int available = tail - head;
                if (available < 0)
                {
                    available += (int)pRing->nr;
                }
                if (available >= nr)
                {
                    io_event* ringEvents = (io_event*)((byte*)pRing + pRing->header_length);
                    io_event* start = ringEvents + head;
                    io_event* end = start + nr;
                    if (head + nr > pRing->nr)
                    {
                        end -= pRing->nr;
                    }
                    if (end > start)
                    {
                        Copy(start, end, events);
                    }
                    else
                    {
                        io_event* eventsEnd = Copy(start, ringEvents + pRing->nr, events);
                        Copy(ringEvents, end, eventsEnd);
                    }
                    head += nr;
                    if (head >= pRing->nr)
                    {
                        head -= (int)pRing->nr;
                    }
                    pRing->head = (uint)head;
                    return new PosixResult(nr);
                }
            }
            return IoGetEvents(ctx, nr, nr, events, -1);
        }

        private static unsafe io_event* Copy(io_event* start, io_event* end, io_event* dst)
        {
            uint byteCount = (uint)((byte*)end - (byte*)start);
            Unsafe.CopyBlock(dst, start, byteCount);
            return (io_event*)((byte*)dst + byteCount);
        }
    }
}