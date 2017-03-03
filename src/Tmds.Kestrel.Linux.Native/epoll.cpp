// Copyright 2017 Tom Deseyn <tom.deseyn@gmail.com>
// This software is made available under the MIT License
// See COPYING for details

#include "utilities.h"

#include <sys/epoll.h>

extern "C"
{
    int32_t TmdsKL_SizeOfEPollEvent();
    PosixResult TmdsKL_EPollCreate(intptr_t* fd);
    PosixResult TmdsKL_EPollWait(intptr_t epoll, struct epoll_event* events, int32_t maxEvents, int32_t timeout);
    PosixResult TmdsKL_EPollControl(intptr_t epoll, int32_t op, intptr_t fd, uint32_t events, uint64_t data);
}

static_assert(sizeof(epoll_event) == 12 || sizeof(epoll_event) == 16,
    "epoll_event must match with EPollEventPacked or EPollEvent");

int32_t TmdsKL_SizeOfEPollEvent()
{
    return sizeof(epoll_event);
}

PosixResult TmdsKL_EPollCreate(intptr_t* fd)
{
    int rv = epoll_create1(EPOLL_CLOEXEC);
    if (rv != -1)
    {
        *fd = FromFileDescriptor(rv);
    }
    return ToPosixResult(rv);
}

PosixResult TmdsKL_EPollWait(intptr_t epoll, struct epoll_event* events, int32_t maxEvents, int32_t timeout)
{
    int fd = ToFileDescriptor(epoll);
    int rv = epoll_wait(fd, events, maxEvents, timeout);
    return ToPosixResult(rv);
}

PosixResult TmdsKL_EPollControl(intptr_t epoll, int32_t op, intptr_t fd, uint32_t events, uint64_t data)
{
    int epollFd = ToFileDescriptor(epoll);
    int fileFd = ToFileDescriptor(fd);
    struct epoll_event event = { .events = events, .data.u64 = data };
    int rv = epoll_ctl(epollFd, op, fileFd, &event);
    return ToPosixResult(rv);
}
