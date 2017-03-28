using System;

namespace Tmds.Kestrel.Linux
{
    public enum ReadStrategy
    {
        // Attempt to read a fixed amount of data
        // This may leave data pending
        Fixed,
        // Read data based on how much is available
        // Requires querying amount of available data
        Available
    }

    public class TransportOptions
    {
        public int ThreadCount { get; set; } = ProcessorThreadCount;

        public bool DeferAccept { get; set; } = true;

        public bool CoalesceWrites { get; set; } = true;

        public ReadStrategy ReadStrategy { get; set; } = ReadStrategy.Available;

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