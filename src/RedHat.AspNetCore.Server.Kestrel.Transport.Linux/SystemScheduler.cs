using System.Runtime.InteropServices;

namespace RedHat.AspNetCore.Server.Kestrel.Transport.Linux
{
    class SchedulerInterop
    {

        [DllImport(Interop.Library, EntryPoint="RHXKL_SetCurrentThreadAffinity")]
        public extern static PosixResult SetCurrentThreadAffinity(int cpuId);
        [DllImport(Interop.Library, EntryPoint="RHXKL_ClearCurrentThreadAffinity")]
        public extern static PosixResult ClearCurrentThreadAffinity();

        [DllImport(Interop.Library, EntryPoint="RHXKL_GetAvailableCpusForProcess")]
        public extern static PosixResult GetAvailableCpusForProcess();
    }

    class SystemScheduler
    {
        public static PosixResult TrySetCurrentThreadAffinity(int cpuId)
        {
            return SchedulerInterop.SetCurrentThreadAffinity(cpuId);
        }

        public static void SetCurrentThreadAffinity(int cpuId)
        {
            TrySetCurrentThreadAffinity(cpuId)
                .ThrowOnError();
        }

        public static PosixResult TryClearCurrentThreadAffinity()
        {
            return SchedulerInterop.ClearCurrentThreadAffinity();
        }

        public static void ClearCurrentThreadAffinity()
        {
            TryClearCurrentThreadAffinity()
                .ThrowOnError();
        }

        public static int GetAvailableCpusForProcess()
        {
            var result = SchedulerInterop.GetAvailableCpusForProcess();
            result.ThrowOnError();
            return result.Value;
        }
    }
}