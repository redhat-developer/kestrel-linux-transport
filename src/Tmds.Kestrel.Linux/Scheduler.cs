using System.Runtime.InteropServices;
using Tmds.Posix;

namespace Tmds.Kestrel.Linux
{
    class SchedulerInterop
    {

        [DllImport(Interop.Library, EntryPoint="TmdsKL_SetCurrentThreadAffinity")]
        public extern static PosixResult SetCurrentThreadAffinity(int cpuId);

        [DllImport(Interop.Library, EntryPoint="TmdsKL_GetAvailableCpusForProcess")]
        public extern static PosixResult GetAvailableCpusForProcess();
    }

    class Scheduler
    {
        public static PosixResult TrySetCurrentThreadAffinity(int cpuId)
        {
            return SchedulerInterop.SetCurrentThreadAffinity(cpuId);
        }

        public static int GetAvailableCpusForProcess()
        {
            var result = SchedulerInterop.GetAvailableCpusForProcess();
            result.ThrowOnError();
            return result.Value;
        }
    }
}