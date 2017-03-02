// Copyright 2017 Tom Deseyn <tom.deseyn@gmail.com>
// This software is made available under the MIT License
// See COPYING for details

#include "utilities.h"

#include <unistd.h>
#include <fcntl.h>

extern "C"
{
    PosixResult TmdsKL_Pipe(intptr_t* readEnd, intptr_t* writeEnd);
}

PosixResult TmdsKL_Pipe(intptr_t* readEnd, intptr_t* writeEnd)
{
    *readEnd = -1;
    *writeEnd = -1;
    int fds[2];
    int res = pipe2(fds, O_NONBLOCK | O_CLOEXEC);
    if (res == 0)
    {
        *readEnd = FromFileDescriptor(fds[0]);
        *writeEnd = FromFileDescriptor(fds[1]);
    }
    return ToPosixResult(res);
}
