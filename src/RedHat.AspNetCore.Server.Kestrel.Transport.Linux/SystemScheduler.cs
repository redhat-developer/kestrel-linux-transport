using System.Runtime.InteropServices;
using Tmds.Linux;
using static Tmds.Linux.LibC;

namespace RedHat.AspNetCore.Server.Kestrel.Transport.Linux
{
    class SchedulerInterop
    {

        public unsafe static PosixResult SetCurrentThreadAffinity(int cpuId)
        {
            cpu_set_t cpu_set;
            CPU_ZERO(&cpu_set);
            CPU_SET(cpuId, &cpu_set);

            int rv = sched_setaffinity(0, SizeOf.cpu_set_t, &cpu_set);

            return PosixResult.FromReturnValue(rv);
        }

        public unsafe static PosixResult ClearCurrentThreadAffinity()
        {
            cpu_set_t cpu_set;
            CPU_ZERO(&cpu_set);
            for (int cpuId = 0; cpuId < CPU_SETSIZE; cpuId++)
            {
                CPU_SET(cpuId, &cpu_set);
            }

            int rv = sched_setaffinity(0, SizeOf.cpu_set_t, &cpu_set);

            return PosixResult.FromReturnValue(rv);
        }

        public unsafe static PosixResult GetAvailableCpusForProcess()
        {
            cpu_set_t set;

            int rv = sched_getaffinity (getpid(), SizeOf.cpu_set_t, &set);
            if (rv == 0)
            {
                rv = CPU_COUNT (&set);
            }

            return PosixResult.FromReturnValue(rv);
        }
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