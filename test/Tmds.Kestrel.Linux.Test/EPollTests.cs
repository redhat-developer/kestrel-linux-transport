using System;
using Tmds.Kestrel.Linux;
using Tmds.Posix;
using Xunit;

namespace Tests
{
    public class EPollTests
    {
        private static ArraySegment<byte> s_data = new ArraySegment<byte>(new byte[] { 0x23 } );

        private static void Dispose(PipeEndPair pair)
        {
            pair.WriteEnd.Dispose();
            pair.ReadEnd.Dispose();
        }

        [Fact]
        public void CreateAndDispose() 
        {
            var epoll = EPoll.Create();
            Assert.True(!epoll.IsInvalid);
            epoll.Dispose();
        }

        [Fact]
        public void CreateAndBlockingClose() 
        {
            var epoll = EPoll.Create();
            Assert.True(!epoll.IsInvalid);
            epoll.BlockingDispose();
        }

        [Fact]
        public void Readable()
        {
            var epoll = EPoll.Create();
            var pipePair1 = PipeEnd.CreatePair(blocking: true);

            EPollEvent[] events;
            PosixResult result;

            events = PollEvents(epoll, maxEvents: 10, timeout: 0);
            Assert.Equal(0, events.Length);

            // Register pipePair1 for readable
            result = epoll.TryControl(EPollOperation.Add, pipePair1.ReadEnd, EPollEvents.Readable, new EPollData() { Long = 0x0102030405060708L });
            Assert.True(result.IsSuccess);

            // Not readable
            events = PollEvents(epoll, maxEvents: 10, timeout: 0);
            Assert.Equal(0, events.Length);

            // Make readable
            pipePair1.WriteEnd.TryWrite(s_data);
            events = PollEvents(epoll, maxEvents: 10, timeout: 0);
            Assert.Equal(1, events.Length);
            Assert.Equal(EPollEvents.Readable, events[0].Events);
            Assert.Equal(0x0102030405060708L, events[0].Data.Long);

            epoll.Dispose();
            Dispose(pipePair1);
        }

        [Fact]
        public void Writable()
        {
            var epoll = EPoll.Create();
            var pipePair1 = PipeEnd.CreatePair(blocking: true);

            EPollEvent[] events;
            PosixResult result;

            events = PollEvents(epoll, maxEvents: 10, timeout: 0);
            Assert.Equal(0, events.Length);

            // Register pipePair1 for writable
            result = epoll.TryControl(EPollOperation.Add, pipePair1.WriteEnd, EPollEvents.Writable, new EPollData() { Long = 0x0102030405060708L });
            Assert.True(result.IsSuccess);

            // Writable
            events = PollEvents(epoll, maxEvents: 10, timeout: 0);
            Assert.Equal(1, events.Length);
            Assert.Equal(EPollEvents.Writable, events[0].Events);
            Assert.Equal(0x0102030405060708L, events[0].Data.Long);

            epoll.Dispose();
            Dispose(pipePair1);
        }

        [Fact]
        public void MultipleEvents()
        {
            var epoll = EPoll.Create();
            var pipePair1 = PipeEnd.CreatePair(blocking: true);
            var pipePair2 = PipeEnd.CreatePair(blocking: true);

            EPollEvent[] events;
            PosixResult result;

            events = PollEvents(epoll, maxEvents: 10, timeout: 0);
            Assert.Equal(0, events.Length);

            // Register pipePair1 for writable
            result = epoll.TryControl(EPollOperation.Add, pipePair1.WriteEnd, EPollEvents.Writable, new EPollData() { Long = 0x0102030405060708L });
            Assert.True(result.IsSuccess);

            // Add pipePair2
            result = epoll.TryControl(EPollOperation.Add, pipePair2.WriteEnd, EPollEvents.Writable, new EPollData() { Long = 0x0807060504030201L });
            Assert.True(result.IsSuccess);

            // Poll
            events = PollEvents(epoll, maxEvents: 10, timeout: 0);
            Assert.Equal(2, events.Length);
            Assert.Equal(EPollEvents.Writable, events[0].Events);
            Assert.Equal(EPollEvents.Writable, events[1].Events);
            var datas = new long[] { 0x0102030405060708L, 0x0807060504030201L};
            Assert.Contains(events[0].Data.Long, datas);
            Assert.Contains(events[1].Data.Long, datas);
            Assert.NotEqual(events[0].Data.Long, events[1].Data.Long);

            epoll.Dispose();
            Dispose(pipePair1);
            Dispose(pipePair2);
        }

        [Fact]
        public void MaxEvents()
        {
            var epoll = EPoll.Create();
            var pipePair1 = PipeEnd.CreatePair(blocking: true);
            var pipePair2 = PipeEnd.CreatePair(blocking: true);

            EPollEvent[] events;
            PosixResult result;

            events = PollEvents(epoll, maxEvents: 10, timeout: 0);
            Assert.Equal(0, events.Length);

            // Register pipePair1 for writable
            result = epoll.TryControl(EPollOperation.Add, pipePair1.WriteEnd, EPollEvents.Writable, new EPollData());
            Assert.True(result.IsSuccess);

            // Add pipePair2
            result = epoll.TryControl(EPollOperation.Add, pipePair2.WriteEnd, EPollEvents.Writable, new EPollData());
            Assert.True(result.IsSuccess);

            // Poll with maxEvents 1
            events = PollEvents(epoll, maxEvents: 1, timeout: 0);
            Assert.Equal(1, events.Length);

            epoll.Dispose();
            Dispose(pipePair1);
            Dispose(pipePair2);
        }

        [Fact]
        public void OneShotAndModify()
        {
            var epoll = EPoll.Create();
            var pipePair1 = PipeEnd.CreatePair(blocking: true);

            EPollEvent[] events;
            PosixResult result;

            // Register pipePair1 for readable with OneShot
            result = epoll.TryControl(EPollOperation.Add, pipePair1.WriteEnd, EPollEvents.Writable | EPollEvents.OneShot, new EPollData());
            Assert.True(result.IsSuccess);

            // Poll indicates writable
            events = PollEvents(epoll, maxEvents: 10, timeout: 0);
            Assert.Equal(1, events.Length);

            // OneShot, no longer writable
            events = PollEvents(epoll, maxEvents: 10, timeout: 0);
            Assert.Equal(0, events.Length);

            // Rearm without OneShot
            result = epoll.TryControl(EPollOperation.Modify, pipePair1.WriteEnd, EPollEvents.Writable, new EPollData());

            // Poll indicates writable
            events = PollEvents(epoll, maxEvents: 10, timeout: 0);
            Assert.Equal(1, events.Length);

            // Event triggered (i.e. no OneShot), still writable
            events = PollEvents(epoll, maxEvents: 10, timeout: 0);
            Assert.Equal(1, events.Length);

            epoll.Dispose();
            Dispose(pipePair1);
        }

        [Fact]
        public void Delete()
        {
            var epoll = EPoll.Create();
            var pipePair1 = PipeEnd.CreatePair(blocking: true);

            EPollEvent[] events;
            PosixResult result;

            // Register pipePair1 for writable
            result = epoll.TryControl(EPollOperation.Add, pipePair1.WriteEnd, EPollEvents.Writable, new EPollData());
            Assert.True(result.IsSuccess);

            // Poll indicates writable
            events = PollEvents(epoll, maxEvents: 10, timeout: 0);
            Assert.Equal(1, events.Length);

            // Unregister
            result = epoll.TryControl(EPollOperation.Delete, pipePair1.WriteEnd, EPollEvents.None, new EPollData());

            // Flush pending
            events = PollEvents(epoll, maxEvents: 10, timeout: 0);

            // Poll no longer returns writable
            events = PollEvents(epoll, maxEvents: 10, timeout: 0);
            Assert.Equal(0, events.Length);

            epoll.Dispose();
            Dispose(pipePair1);
        }

        [Fact]
        public void Timeout()
        {
            var epoll = EPoll.Create();

            var startTime = Environment.TickCount;
            int milliSecondTimeout = 500;
            // TODO: do tests run faster if we defer the blocking operation to the ThreadPool?
            PollEvents(epoll, maxEvents: 10, timeout: milliSecondTimeout);
            var endTime = Environment.TickCount;
            bool timeoutOK = endTime - startTime >= milliSecondTimeout;
            if (!timeoutOK)
            {
                System.Console.WriteLine($"{endTime - startTime} >= {milliSecondTimeout}");
            }
            Assert.True(timeoutOK);

            epoll.Dispose();
        }

        [Fact]
        public void HangUp()
        {
            var epoll = EPoll.Create();
            var pipePair1 = PipeEnd.CreatePair(blocking: true);

            EPollEvent[] events;
            PosixResult result;

            events = PollEvents(epoll, maxEvents: 10, timeout: 0);
            Assert.Equal(0, events.Length);

            // Register pipePair1 for readable
            result = epoll.TryControl(EPollOperation.Add, pipePair1.ReadEnd, EPollEvents.Readable, new EPollData());
            Assert.True(result.IsSuccess);

            // Close the write end
            pipePair1.WriteEnd.Dispose();

            // Poll returns HangUp
            events = PollEvents(epoll, maxEvents: 10, timeout: 0);
            Assert.Equal(1, events.Length);
            Assert.Equal(EPollEvents.HangUp, events[0].Events);

            epoll.Dispose();
            pipePair1.ReadEnd.Dispose();
        }

        [Fact]
        public void Error()
        {
            var epoll = EPoll.Create();
            var pipePair1 = PipeEnd.CreatePair(blocking: true);

            EPollEvent[] events;
            PosixResult result;

            events = PollEvents(epoll, maxEvents: 10, timeout: 0);
            Assert.Equal(0, events.Length);

            // Close the read end
            pipePair1.ReadEnd.Dispose();

            // Register pipePair1 for readable
            result = epoll.TryControl(EPollOperation.Add, pipePair1.WriteEnd, EPollEvents.Writable, new EPollData());
            Assert.True(result.IsSuccess);

            // Poll returns Writable, Error
            events = PollEvents(epoll, maxEvents: 10, timeout: 0);
            Assert.Equal(1, events.Length);
            Assert.Equal(EPollEvents.Writable | EPollEvents.Error, events[0].Events);

            epoll.Dispose();
            pipePair1.WriteEnd.Dispose();
        }

        internal static unsafe EPollEvent[] PollEvents(EPoll epoll, int maxEvents, int timeout)
        {
            bool isPackedEvents = EPoll.PackedEvents;
            EPollEvent* events = stackalloc EPollEvent[isPackedEvents ? 0 : maxEvents];
            EPollEventPacked* packedEvents = stackalloc EPollEventPacked[isPackedEvents ? maxEvents : 0];
            int nrEvents = epoll.Wait(events != null ? (void*)events : (void*)packedEvents, maxEvents, timeout);
            var retval = new EPollEvent[nrEvents];
            for (int i = 0; i < nrEvents; i++)
            {
                retval[i].Events = isPackedEvents ? packedEvents[i].Events : events[i].Events;
                retval[i].Data   = isPackedEvents ? packedEvents[i].Data   : events[i].Data;
            }
            return retval;
        }
    }
}