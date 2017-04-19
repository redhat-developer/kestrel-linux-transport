using System;

namespace RedHatX.AspNetCore.Server.Kestrel.Transport.Linux
{
    [Flags]
    enum EPollEvents : int
    {
        None     = 0,
        Readable = 0x01,    // EPOLLIN
        Writable = 0x04,    // EPOLLOUT
        Error    = 0x08,    // EPOLLERR
        HangUp   = 0x10,    // EPOLLHUP
        OneShot  = 1 << 30, // EPOLLONESHOT
    }
}