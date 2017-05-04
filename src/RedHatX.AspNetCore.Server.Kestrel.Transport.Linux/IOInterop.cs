// Copyright 2017 Tom Deseyn <tom.deseyn@gmail.com>
// This software is made available under the MIT License
// See COPYING for details

using System;
using System.Runtime.InteropServices;

namespace RedHatX.AspNetCore.Server.Kestrel.Transport.Linux
{
    static class IOInterop
    {
        [DllImport(Interop.Library, EntryPoint = "RHXKL_Close")]
        public static extern PosixResult Close(IntPtr handle);

        [DllImport(Interop.Library, EntryPoint = "RHXKL_Write")]
        public static unsafe extern PosixResult Write(SafeHandle handle, byte* buf, int count);

        [DllImport(Interop.Library, EntryPoint = "RHXKL_Read")]
        public static unsafe extern PosixResult Read(SafeHandle handle, byte* buf, int count);
    }
}