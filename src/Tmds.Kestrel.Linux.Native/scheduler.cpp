#include "utilities.h"

#include <sched.h>
#include <sys/types.h>
#include <unistd.h>

extern "C"
{
    PosixResult TmdsKL_SetCurrentThreadAffinity(int cpuId);
    PosixResult TmdsKL_ClearCurrentThreadAffinity();
    PosixResult TmdsKL_GetAvailableCpusForProcess();
}

PosixResult TmdsKL_SetCurrentThreadAffinity(int cpuId)
{
    cpu_set_t cpu_set;
    CPU_ZERO(&cpu_set);
    CPU_SET(cpuId, &cpu_set);

    int rv = sched_setaffinity(0, sizeof(cpu_set), &cpu_set);

    return ToPosixResult(rv);
}

PosixResult TmdsKL_ClearCurrentThreadAffinity()
{
    cpu_set_t cpu_set;
    CPU_ZERO(&cpu_set);
    for (int cpuId = 0; cpuId < CPU_SETSIZE; cpuId++)
    {
        CPU_SET(cpuId, &cpu_set);
    }

    int rv = sched_setaffinity(0, sizeof(cpu_set), &cpu_set);

    return ToPosixResult(rv);
}

PosixResult TmdsKL_GetAvailableCpusForProcess()
{
    cpu_set_t set;
    int rv = sched_getaffinity (getpid (), sizeof (set), &set);
    if (rv == 0)
    {
        rv = CPU_COUNT (&set);
    }
    return ToPosixResult(rv);
}
