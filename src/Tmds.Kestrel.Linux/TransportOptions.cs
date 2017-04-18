namespace Tmds.Kestrel.Linux
{
    public class TransportOptions
    {
        private int? _threadCount;

        public int ThreadCount
        {
            get
            {
                return _threadCount ?? (CpuSet.Cpus.Length != 0 ? CpuSet.Cpus.Length : Scheduler.GetAvailableCpusForProcess());
            }
            set
            {
                _threadCount = value;
            }
        }

        public CpuSet CpuSet { get; set; }

        public bool SetThreadAffinity { get; set; } = false;

        public bool ReceiveOnIncomingCpu { get; set; } = false;

        public bool DeferAccept { get; set; } = true;
    }
}