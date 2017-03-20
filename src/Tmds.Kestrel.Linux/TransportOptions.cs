using System;

namespace Tmds.Kestrel.Linux
{
    public class TransportOptions
    {
        public int ThreadCount { get; set; } = ProcessorThreadCount;

        public bool DeferAccept { get; set; } = true;

        private static int ProcessorThreadCount
        {
            get
            {
                // cfr Netty
                return Environment.ProcessorCount << 1;
            }
        }
    }
}