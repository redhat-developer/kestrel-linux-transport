using System;
using System.Net;

namespace RedHatX.AspNetCore.Server.Kestrel.Transport.Linux
{
    static class TransportConstants
    {
        public const int MaxEAgainCount = 10;

        public static readonly Exception EofSentinel = new Exception();
        public static readonly Exception EAgainSentinel = new Exception();
        public static readonly Exception StopSentinel = new Exception();
    }
}