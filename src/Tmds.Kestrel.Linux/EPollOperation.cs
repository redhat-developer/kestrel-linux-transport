// Copyright 2017 Tom Deseyn <tom.deseyn@gmail.com>
// This software is made available under the MIT License
// See LICENSE for details

namespace Tmds.Kestrel.Linux
{
    enum EPollOperation : int
    {
        Add    = 1, // EPOLL_CTL_ADD
        Delete = 2, // EPOLL_CTL_DEL
        Modify = 3, // EPOLL_CTL_MOD
    }
}