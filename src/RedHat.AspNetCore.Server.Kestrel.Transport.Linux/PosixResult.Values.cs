// Copyright 2017 Tom Deseyn <tom.deseyn@gmail.com>
// This software is made available under the MIT License
// See COPYING for details

using System.Collections.Generic;

namespace RedHat.AspNetCore.Server.Kestrel.Transport.Linux
{
    internal partial struct PosixResult
    {
        public static int EAFNOSUPPORT => -Tmds.Linux.LibC.EAFNOSUPPORT;
        public static int EAGAIN => -Tmds.Linux.LibC.EAGAIN;
        public static int ECONNABORTED => -Tmds.Linux.LibC.ECONNABORTED;
        public static int ECONNRESET => -Tmds.Linux.LibC.ECONNRESET;
        public static int EINVAL => -Tmds.Linux.LibC.EINVAL;
        public static int ENOBUFS => -Tmds.Linux.LibC.ENOBUFS;
        public static int EPIPE => -Tmds.Linux.LibC.EPIPE;
        public static int ECONNREFUSED => -Tmds.Linux.LibC.ECONNREFUSED;
        public static int EADDRINUSE => -Tmds.Linux.LibC.EADDRINUSE;
        public static int EADDRNOTAVAIL = -Tmds.Linux.LibC.EADDRNOTAVAIL;
    }
}