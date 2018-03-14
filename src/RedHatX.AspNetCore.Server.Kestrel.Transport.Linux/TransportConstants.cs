using System;
using System.Net;

namespace RedHatX.AspNetCore.Server.Kestrel.Transport.Linux
{
    static class TransportConstants
    {
        public const int MaxEAgainCount = 10;
        public static PosixResult TooManyEAgain = new PosixResult(int.MinValue);

        public static readonly Exception StopSentinel = new Exception();
    }
}