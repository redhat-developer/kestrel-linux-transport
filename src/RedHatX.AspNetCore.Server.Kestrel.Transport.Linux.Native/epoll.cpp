#include "utilities.h"

#include <sys/epoll.h>

extern "C"
{
    int32_t RHXKL_SizeOfEPollEvent();
    PosixResult RHXKL_EPollCreate(intptr_t* fd);
    PosixResult RHXKL_EPollWait(intptr_t epoll, struct epoll_event* events, int32_t maxEvents, int32_t timeout);
    PosixResult RHXKL_EPollControl(intptr_t epoll, int32_t op, intptr_t fd, uint32_t events, uint64_t data);
}

static_assert(sizeof(epoll_event) == 12 || sizeof(epoll_event) == 16,
    "epoll_event must match with EPollEventPacked or EPollEvent");

int32_t RHXKL_SizeOfEPollEvent()
{
    return sizeof(epoll_event);
}

PosixResult RHXKL_EPollCreate(intptr_t* fd)
{
    if (fd == nullptr)
    {
        return PosixResultEFAULT;
    }

    int rv = epoll_create1(EPOLL_CLOEXEC);
    *fd = FromFileDescriptor(rv);

    return ToPosixResult(rv);
}

PosixResult RHXKL_EPollWait(intptr_t epoll, struct epoll_event* events, int32_t maxEvents, int32_t timeout)
{
    int fd = ToFileDescriptor(epoll);
    int rv;
    while (CheckInterrupted(rv = epoll_wait(fd, events, maxEvents, timeout)));
    return ToPosixResult(rv);
}

PosixResult RHXKL_EPollControl(intptr_t epoll, int32_t op, intptr_t fd, uint32_t events, uint64_t data)
{
    int epollFd = ToFileDescriptor(epoll);
    int fileFd = ToFileDescriptor(fd);
    struct epoll_event event = { .events = events, .data.u64 = data };
    int rv = epoll_ctl(epollFd, op, fileFd, &event);
    return ToPosixResult(rv);
}
