namespace Tmds.Kestrel.Linux
{
    public class TransportOptions
    {
        public int ThreadCount { get; set; } = AvailableProcessors;

        public bool SetThreadAffinity { get; set; } = NotConstrained;

        public bool ReceiveOnIncomingCpu { get; set; } = NotConstrained;

        public bool DeferAccept { get; set; } = true;

        public bool CoalesceWrites { get; set; } = true;

        private static int AvailableProcessors => Scheduler.GetAvailableCpusForProcess();

        private static bool NotConstrained => Scheduler.GetAvailableCpusForProcess() == CpuInfo.GetAvailableCpus();
    }
}