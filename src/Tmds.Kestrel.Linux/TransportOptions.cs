namespace Tmds.Kestrel.Linux
{
    public class TransportOptions
    {
        public int ThreadCount { get; set; } = AvailableProcessors;

        public bool SetThreadAffinity { get; set; } = false;

        public bool ReceiveOnIncomingCpu { get; set; } = false;

        public bool DeferAccept { get; set; } = true;

        private static int AvailableProcessors => Scheduler.GetAvailableCpusForProcess();
    }
}