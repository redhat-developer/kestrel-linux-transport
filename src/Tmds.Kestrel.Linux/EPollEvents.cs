// Copyright 2017 Tom Deseyn <tom.deseyn@gmail.com>
// This software is made available under the MIT License
// See LICENSE for details

using System;

namespace Tmds.Kestrel.Linux
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