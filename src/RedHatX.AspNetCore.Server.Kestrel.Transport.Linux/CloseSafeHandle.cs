// Copyright 2017 Tom Deseyn <tom.deseyn@gmail.com>
// This software is made available under the MIT License
// See COPYING for details

using System;
using System.Runtime.InteropServices;

namespace RedHatX.AspNetCore.Server.Kestrel.Transport.Linux
{
    internal class CloseSafeHandle : SafeHandle
    {
        public CloseSafeHandle()
            : base(new IntPtr(-1), true)
        {}

        public override bool IsInvalid
        {
            get { return handle == new IntPtr(-1); }
        }

        protected override bool ReleaseHandle()
        {
            var result = IOInterop.Close(handle);
            return result.IsSuccess;
        }

        protected unsafe PosixResult TryWrite(byte* buffer, int length)
        {
            return IOInterop.Write(this, buffer, length);
        }

        protected unsafe PosixResult TryRead(byte* buffer, int length)
        {
            return IOInterop.Read(this, buffer, length);
        }

        protected unsafe PosixResult TryWrite(ArraySegment<byte> buffer)
        {
            // TODO: validate buffer
            fixed (byte* buf = buffer.Array)
            {
                return IOInterop.Write(this, buf + buffer.Offset, buffer.Count);
            }
        }

        protected unsafe PosixResult TryRead(ArraySegment<byte> buffer)
        {
            // TODO: validate buffer
            fixed (byte* buf = buffer.Array)
            {
                return IOInterop.Read(this, buf + buffer.Offset, buffer.Count);
            }
        }
    }
}