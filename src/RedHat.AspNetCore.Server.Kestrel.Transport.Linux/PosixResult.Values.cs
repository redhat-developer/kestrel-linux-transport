// Copyright 2017 Tom Deseyn <tom.deseyn@gmail.com>
// This software is made available under the MIT License
// See COPYING for details

using System.Collections.Generic;

namespace RedHat.AspNetCore.Server.Kestrel.Transport.Linux
{
    internal partial struct PosixResult
    {
        public static int EAFNOSUPPORT => -Tmds.LibC.Definitions.EAFNOSUPPORT;
        public static int EAGAIN => -Tmds.LibC.Definitions.EAGAIN;
        public static int ECONNABORTED => -Tmds.LibC.Definitions.ECONNABORTED;
        public static int ECONNRESET => -Tmds.LibC.Definitions.ECONNRESET;
        public static int EINVAL => -Tmds.LibC.Definitions.EINVAL;
        public static int ENOBUFS => -Tmds.LibC.Definitions.ENOBUFS;
        public static int EPIPE => -Tmds.LibC.Definitions.EPIPE;
        public static int ECONNREFUSED => -Tmds.LibC.Definitions.ECONNREFUSED;
    }
}