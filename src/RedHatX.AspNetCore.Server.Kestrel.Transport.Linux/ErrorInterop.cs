// Copyright 2017 Tom Deseyn <tom.deseyn@gmail.com>
// This software is made available under the MIT License
// See COPYING for details

using System;
using System.Runtime.InteropServices;

namespace RedHatX.AspNetCore.Server.Kestrel.Transport.Linux
{
    static class ErrorInterop
    {
        public static unsafe string StrError(int errno)
        {
            int maxBufferLength = 1024; // should be long enough for most any UNIX error
            byte* buffer = stackalloc byte[maxBufferLength];
            byte* message = StrErrorR(errno, buffer, maxBufferLength);

            if (message == null)
            {
                // This means the buffer was not large enough, but still contains
                // as much of the error message as possible and is guaranteed to
                // be null-terminated. We're not currently resizing/retrying because
                // maxBufferLength is large enough in practice, but we could do
                // so here in the future if necessary.
                message = buffer;
            }

            return Marshal.PtrToStringAnsi((IntPtr)message);
        }

        [DllImport(Interop.Library, EntryPoint = "RHXKL_StrErrorR")]
        private static extern unsafe byte* StrErrorR(int errno, byte* buffer, int bufferSize);

        [DllImport(Interop.Library, EntryPoint = "RHXKL_GetErrnoValues")]
        public static extern void GetErrnoValues(int[] knownErrorValues, int count);
    }
}