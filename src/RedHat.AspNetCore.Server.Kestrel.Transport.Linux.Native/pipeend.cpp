#include "utilities.h"

#include <unistd.h>
#include <fcntl.h>

extern "C"
{
    PosixResult RHXKL_Pipe(intptr_t* readEnd, intptr_t* writeEnd, int32_t blocking);
}

PosixResult RHXKL_Pipe(intptr_t* readEnd, intptr_t* writeEnd, int32_t blocking)
{
    if (readEnd == nullptr || writeEnd == nullptr)
    {
        return PosixResultEFAULT;
    }

    *readEnd = -1;
    *writeEnd = -1;
    int fds[2];
    int flags = O_CLOEXEC;
    if (blocking == 0)
    {
        flags |= O_NONBLOCK;
    }
    int res = pipe2(fds, flags);
    if (res == 0)
    {
        *readEnd = FromFileDescriptor(fds[0]);
        *writeEnd = FromFileDescriptor(fds[1]);
    }
    return ToPosixResult(res);
}
