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

            // Benchmarking Techempower Json on 24-core machine with hyper threading (ProcessorCount = 48)
            // shows best performance at ThreadCount 12.
            // TODO: what happens if hyperthreading is disabled? Perhaps this should be half the cores?
            // TODO: benchmark more scenarios to validate this is a good default.
            ThreadCount = Math.Max((Environment.ProcessorCount + 2) / 4, 1);
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