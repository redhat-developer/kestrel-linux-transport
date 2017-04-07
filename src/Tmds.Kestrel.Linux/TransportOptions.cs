using System;

namespace Tmds.Kestrel.Linux
{
    public class TransportOptions
    {
        private CpuSet _cpuSet = default(CpuSet);

        internal CpuSet ParsedCpuSet { get { return _cpuSet; } }

        public int ThreadCount { get; set; } = AvailableProcessors;

        public string CpuSet
        {
            get { return _cpuSet.ToString(); }
            set
            {
                if (string.IsNullOrEmpty(value))
                {
                    ThreadCount = AvailableProcessors;
                }
                else
                {
                    CpuSet set;
                    if (!Tmds.Kestrel.Linux.CpuSet.TryParse(value, out set))
                    {
                        throw new FormatException();
                    }
                    _cpuSet = set;
                    ThreadCount = _cpuSet.Cpus.Length;
                }
             }
        }

        public bool SetThreadAffinity { get; set; } = false;

        public bool ReceiveOnIncomingCpu { get; set; } = false;

        public bool DeferAccept { get; set; } = true;

        private static int AvailableProcessors => Scheduler.GetAvailableCpusForProcess();
    }
}