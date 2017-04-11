namespace Tmds.Kestrel.Linux
{
    public class TransportOptions
    {
        private CpuSet _cpuSet = default(CpuSet);

        internal CpuSet ParsedCpuSet { get { return _cpuSet; } }

        public int ThreadCount { get; set; } = AvailableProcessors;

        public CpuSet CpuSet
        {
            get { return _cpuSet; }
            set
            {
                _cpuSet = value;
                if (_cpuSet.Cpus.Length != 0)
                {
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