// Copyright 2017 Tom Deseyn <tom.deseyn@gmail.com>
// This software is made available under the MIT License
// See COPYING for details

#include "utilities.h"

#include <unistd.h>

extern "C"
{
    PosixResult RHXKL_Close(intptr_t handle);
    PosixResult RHXKL_Write(intptr_t handle, void* buf, int32_t count);
    PosixResult RHXKL_Read(intptr_t handle, void* buf, int32_t count);
}

PosixResult RHXKL_Close(intptr_t handle)
{
    int fd = ToFileDescriptor(handle);
    // don't CheckInterrupted, linux and and many other implementations
    // close the file descriptor even when returning EINTR
    int rv = close(fd);
    return ToPosixResult(rv);
}

PosixResult RHXKL_Write(intptr_t handle, void* buf, int32_t count)
{
    int fd = ToFileDescriptor(handle);
    int rv;
    while (CheckInterrupted(rv = static_cast<int>(write(fd, buf, UnsignedCast(count)))));
    return ToPosixResult(rv);
}

PosixResult RHXKL_Read(intptr_t handle, void* buf, int32_t count)
{
    int fd = ToFileDescriptor(handle);
    int rv;
    while (CheckInterrupted(rv = static_cast<int>(read(fd, buf, UnsignedCast(count)))));
    return ToPosixResult(rv);
}
