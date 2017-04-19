using System;
using System.Runtime.InteropServices;
using System.Threading;
using Tmds.Posix;

namespace RedHatX.AspNetCore.Server.Kestrel.Transport.Linux
{
    static class EPollInterop
    {
        [DllImportAttribute(Interop.Library, EntryPoint = "RHXKL_EPollCreate")]
        public static extern PosixResult EPollCreate(out EPoll epoll);

        [DllImportAttribute(Interop.Library, EntryPoint = "RHXKL_EPollWait")]
        public static unsafe extern PosixResult EPollWait(EPoll epoll, void* events, int maxEvents, int timeout);

        [DllImportAttribute(Interop.Library, EntryPoint = "RHXKL_EPollControl")]
        public static extern PosixResult EPollControl(EPoll epoll, EPollOperation operation, SafeHandle fd, EPollEvents events, long data);

        [DllImportAttribute(Interop.Library, EntryPoint = "RHXKL_SizeOfEPollEvent")]
        public static extern int SizeOfEPollEvent();
    }

    class EPoll : CloseSafeHandle
    {
        private static bool s_packedEvents = false;
        public static bool PackedEvents => s_packedEvents;
        public const int TimeoutInfinite = -1;
        private bool _released = false;

        static EPoll()
        {
            var epollEventSize =  EPollInterop.SizeOfEPollEvent();
            if (epollEventSize == Marshal.SizeOf<EPollEventPacked>())
            {
                s_packedEvents = true;
            }
            else if (epollEventSize == Marshal.SizeOf<EPollEvent>())
            {
                s_packedEvents = false;
            }
            else
            {
                throw new NotSupportedException();
            }
        }

        private EPoll()
        {}

        public static EPoll Create()
        {
            EPoll epoll;
            var result = EPollInterop.EPollCreate(out epoll);
            result.ThrowOnError();
            return epoll;
        }

        public unsafe int Wait(void* events, int maxEvents, int timeout)
        {
            var result = TryWait(events, maxEvents, timeout);
            result.ThrowOnError();
            return result.Value;
        }

        public unsafe PosixResult TryWait(void* events, int maxEvents, int timeout)
        {
            return EPollInterop.EPollWait(this, events, maxEvents, timeout);
        }

        public void Control(EPollOperation operation, SafeHandle fd, EPollEvents events, EPollData data)
        {
            TryControl(operation, fd, events, data)
                .ThrowOnError();
        }

        public PosixResult TryControl(EPollOperation operation, SafeHandle fd, EPollEvents events, EPollData data)
        {
            return EPollInterop.EPollControl(this, operation, fd, events, data.Long);
        }

        protected override bool ReleaseHandle()
        {
            _released = true;
            return base.ReleaseHandle();
        }

        // This method will only return when the EPoll has been closed.
        // Calls to Control will then throw ObjectDisposedException.
        public void BlockingDispose()
        {
            if (IsInvalid)
            {
                return;
            }

            Dispose();

            // block until the refcount drops to zero
            SpinWait sw = new SpinWait();
            while (!_released)
            {
                sw.SpinOnce();
            }
        }
    }
}