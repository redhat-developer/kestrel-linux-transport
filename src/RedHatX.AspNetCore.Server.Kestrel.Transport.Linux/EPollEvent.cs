using System.Runtime.InteropServices;

namespace RedHatX.AspNetCore.Server.Kestrel.Transport.Linux
{
    [StructLayout(LayoutKind.Explicit, Size = 8)]
    struct EPollData
    {
        [FieldOffset(0)]
        public long   Long;
        [FieldOffset(0)]
        public int    Int1;
        [FieldOffset(4)]
        public int    Int2;
    }

    // EPoll.PackedEvents == false (64-bit systems, except x64)
    [StructLayout(LayoutKind.Sequential, Size = 16, Pack = 8)]
    struct EPollEvent
    {
        public EPollEvents Events;
        public EPollData Data;
    }

    // EPoll.PackedEvents == true (32-bit systems and x64)
    [StructLayout(LayoutKind.Sequential, Size = 12, Pack = 4)]
    struct EPollEventPacked
    {
        public EPollEvents Events;
        public EPollData Data;
    }
}