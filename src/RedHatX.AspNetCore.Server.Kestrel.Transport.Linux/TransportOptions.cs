using System;

namespace RedHatX.AspNetCore.Server.Kestrel.Transport.Linux
{
    public class LinuxTransportOptions
    {
        public const int NoZeroCopy = int.MaxValue;

        private int    _threadCount;
        private bool   _threadAffinity;
        private bool   _zeroCopy;
        private bool   _aioSend;
        private bool   _deferAccept;
        private bool   _deferSend;
        private CpuSet _cpuSet;
        private bool   _receiveOnIncomingCpu;

        public LinuxTransportOptions()
        {
            AioSend = true;
            AioReceive = true;

            // Use a default ThreadCount that wont cause the number of threads
            // to be a bottleneck.
            // Users that want to optimize, should do their own benchmarks.
            ThreadCount = Math.Min(Environment.ProcessorCount, 16);
        }

        internal bool ReceiveOnIncomingCpu
        {
            get => _receiveOnIncomingCpu;
            set
            {
                if (value == true)
                {
                    _threadAffinity = true;
                }
                _receiveOnIncomingCpu = value;
            }
        }

        internal bool DeferAccept
        {
            get => _deferAccept;
            set
            {
                if (value == true)
                {
                    _aioSend = false;
                }
                _deferAccept = value;
            }
        }

        public bool DeferSend
        {
            get => _deferSend;
            set
            {
                if (value == false)
                {
                    _aioSend = false;
                }
                _deferSend = value;
            }
        }

        internal int ZeroCopyThreshold { get; set; } = 10 * 1024; // 10KB

        internal bool ZeroCopy
        {
            get => _zeroCopy;
            set
            {
                if (value == true)
                {
                    _aioSend = false;
                }
                _zeroCopy = value;
            }
        }

        public bool AioReceive { get; set; } = false;

        public bool AioSend
        {
            get => _aioSend;
            set
            {
                if (value == true)
                {
                    _zeroCopy = false;
                    _deferAccept = false;
                    _deferSend = true;
                }
                _aioSend = value;
            }
        }

        internal CpuSet CpuSet
        {
            get => _cpuSet;
            set
            {
                if (!value.IsEmpty)
                {
                    _threadCount = value.Cpus.Length;
                }
                _cpuSet = value;
            }
        }

        public int ThreadCount
        {
            get => _threadCount;
            set
            {
                if (_threadCount != value)
                {
                    _cpuSet = default(CpuSet);
                }
                _threadCount = value;
            }
        }

        internal bool SetThreadAffinity
        {
            get => _threadAffinity;
            set
            {
                if (value == false)
                {
                    _receiveOnIncomingCpu = false;
                }
                _threadAffinity = value;
            }
        }
    }
}