namespace RedHatX.AspNetCore.Server.Kestrel.Transport.Linux
{
    public class LinuxTransportOptions
    {
        private int? _threadCount;
        private bool? _threadAffinity;

        public const int NoZeroCopy = int.MaxValue;

        internal bool ReceiveOnIncomingCpu { get; set; } = false;

        public bool DeferAccept { get; set; } = true;

        public bool DeferSend { get; set; } = true;

        public int ZeroCopyThreshold { get; set; } = 10 * 1024; // 10KB

        public bool ZeroCopy { get; set; } = false;

        internal CpuSet CpuSet { get; set; }

        public int ThreadCount
        {
            get => _threadCount ?? (CpuSet.IsEmpty ? SystemScheduler.GetAvailableCpusForProcess() : CpuSet.Cpus.Length);
            set => _threadCount = value;
        }

        internal bool SetThreadAffinity
        {
            get => _threadAffinity ?? (ReceiveOnIncomingCpu || !CpuSet.IsEmpty);
            set => _threadAffinity = value;
        }
    }
}