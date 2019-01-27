// Copyright 2017 Tom Deseyn <tom.deseyn@gmail.com>
// This software is made available under the MIT License
// See COPYING for details

using System;
using System.Runtime.InteropServices;
using static Tmds.Linux.LibC;

namespace RedHat.AspNetCore.Server.Kestrel.Transport.Linux
{
    static class ErrorInterop
    {
        public static unsafe string StrError(int errno)
        {
            int maxBufferLength = 1024; // should be long enough for most any UNIX error
            byte* buffer = stackalloc byte[maxBufferLength];
            int rv = Tmds.Linux.LibC.strerror_r(errno, buffer, maxBufferLength);
            return rv == 0 ? Marshal.PtrToStringAnsi((IntPtr)buffer) : $"errno={errno}";
        }
    }
}