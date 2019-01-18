// Copyright 2017 Tom Deseyn <tom.deseyn@gmail.com>
// This software is made available under the MIT License
// See COPYING for details

using System;
using System.Runtime.InteropServices;
using static Tmds.LibC.Definitions;

namespace RedHat.AspNetCore.Server.Kestrel.Transport.Linux
{
    static class IOInterop
    {
        public static PosixResult Close(int handle)
        {
            int rv = close(handle);

            return PosixResult.FromReturnValue(rv);
        }

        public static unsafe PosixResult Write(SafeHandle handle, byte* buf, int count)
        {
            bool addedRef = false;
            try
            {
                handle.DangerousAddRef(ref addedRef);
                if (!addedRef)
                {
                    throw new ObjectDisposedException(nameof(SafeHandle));
                }

                int fd = handle.DangerousGetHandle().ToInt32();
                int rv;
                do
                {
                    rv = (int)write(fd, buf, count);
                } while (rv < 0 && errno == EINTR);

                return PosixResult.FromReturnValue(rv);
            }
            finally
            {
                if (addedRef)
                {
                    handle.DangerousRelease();
                }
            }
        }


        public static unsafe PosixResult Read(SafeHandle handle, byte* buf, int count)
        {
            bool addedRef = false;
            try
            {
                handle.DangerousAddRef(ref addedRef);
                if (!addedRef)
                {
                    throw new ObjectDisposedException(nameof(SafeHandle));
                }

                int fd = handle.DangerousGetHandle().ToInt32();
                int rv;
                do
                {
                    rv = (int)read(fd, buf, count);
                } while (rv < 0 && errno == EINTR);

                return PosixResult.FromReturnValue(rv);
            }
            finally
            {
                if (addedRef)
                {
                    handle.DangerousRelease();
                }
            }
        }
    }
}