using System;
using System.Linq;

namespace Tmds.Kestrel.Linux
{
    public class TransportOptions
    {
        public int ThreadCount { get; set; } = CoreCount;

        public bool SetThreadAffinity { get; set; } = NotConstrained;

        public bool DeferAccept { get; set; } = true;

        public bool CoalesceWrites { get; set; } = true;

        private static int CoreCount
        {
            get
            {
                int coreCount = 0;
                foreach(var socket in CpuInfo.GetSockets())
                {
                    coreCount += CpuInfo.GetCores(socket).Count();
                }
                return Math.Min(coreCount, Scheduler.GetAvailableCpusForProcess());
            }
        }

        private static bool NotConstrained
        {
            get
            {
                return Scheduler.GetAvailableCpusForProcess() == CpuInfo.GetAvailableCpus();
            }
        }
    }
}