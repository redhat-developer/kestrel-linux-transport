#include "utilities.h"

#include <time.h>
#include <unistd.h>
#include <sys/syscall.h>
#include <linux/aio_abi.h>

extern "C"
{
    PosixResult RHXKL_IoSetup(uint32_t nr, intptr_t *ctxp);
    PosixResult RHXKL_IoDestroy(intptr_t ctx);
    PosixResult RHXKL_IoSubmit(intptr_t ctx, int32_t nr, struct iocb **iocbpp);
    PosixResult RHXKL_IoGetEvents(intptr_t ctx, int32_t min_nr, int32_t max_nr, struct io_event *events, int32_t timeoutMs);
}

#pragma clang diagnostic push
#pragma clang diagnostic ignored "-Wshorten-64-to-32"

PosixResult RHXKL_IoSetup(uint32_t nr, intptr_t *ctxp)
{
    int rv = syscall(__NR_io_setup, nr, ctxp);

    if (rv == -1)
    {
        *ctxp = 0;
    }

    return ToPosixResult(rv);
}

PosixResult RHXKL_IoDestroy(intptr_t ctx)
{
    int rv = syscall(__NR_io_destroy, ctx);

    return ToPosixResult(rv);
}

PosixResult RHXKL_IoSubmit(intptr_t ctx, int32_t nr, struct iocb **iocbpp)
{
    long nr_ = nr;

    int rv = syscall(__NR_io_submit, ctx, nr_, iocbpp);

    return ToPosixResult(rv);
}

PosixResult RHXKL_IoGetEvents(intptr_t ctx, int32_t min_nr, int32_t max_nr, struct io_event *events, int32_t timeoutMs)
{
    long min_nr_ = min_nr;
    long max_nr_ = max_nr;
    struct timespec timeout;
    if (timeoutMs >= 0)
    {
        timeout.tv_sec = timeoutMs / 1000;
        timeout.tv_nsec = 1000 * (timeoutMs % 1000);
    }

    int rv;
    while (CheckInterrupted(rv = syscall(__NR_io_getevents, ctx, min_nr_, max_nr_, events, timeoutMs < 0 ? NULL : &timeout)));

    return ToPosixResult(rv);
}

#pragma clang diagnostic pop
