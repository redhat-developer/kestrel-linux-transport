using System;
using RedHat.AspNetCore.Server.Kestrel.Transport.Linux;
using Xunit;
using Tmds.LibC;
using static Tmds.LibC.Definitions;

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

            epoll_event[] events;
            PosixResult result;

            events = PollEvents(epoll, maxEvents: 10, timeout: 0);
            Assert.Equal(0, events.Length);

            // Register pipePair1 for readable
            result = epoll.TryControl(EPOLL_CTL_ADD, pipePair1.ReadEnd, EPOLLIN, 0x01020304);
            Assert.True(result.IsSuccess);

            // Not readable
            events = PollEvents(epoll, maxEvents: 10, timeout: 0);
            Assert.Equal(0, events.Length);

            // Make readable
            pipePair1.WriteEnd.TryWrite(s_data);
            events = PollEvents(epoll, maxEvents: 10, timeout: 0);
            Assert.Equal(1, events.Length);
            Assert.Equal(EPOLLIN, events[0].events);
            Assert.Equal(0x01020304, events[0].data.fd);

            epoll.Dispose();
            Dispose(pipePair1);
        }

        [Fact]
        public void Writable()
        {
            var epoll = EPoll.Create();
            var pipePair1 = PipeEnd.CreatePair(blocking: true);

            epoll_event[] events;
            PosixResult result;

            events = PollEvents(epoll, maxEvents: 10, timeout: 0);
            Assert.Equal(0, events.Length);

            // Register pipePair1 for writable
            result = epoll.TryControl(EPOLL_CTL_ADD, pipePair1.WriteEnd, EPOLLOUT, 0x01020304);
            Assert.True(result.IsSuccess);

            // Writable
            events = PollEvents(epoll, maxEvents: 10, timeout: 0);
            Assert.Equal(1, events.Length);
            Assert.Equal(EPOLLOUT, events[0].events);
            Assert.Equal(0x01020304, events[0].data.fd);

            epoll.Dispose();
            Dispose(pipePair1);
        }

        [Fact]
        public void MultipleEvents()
        {
            var epoll = EPoll.Create();
            var pipePair1 = PipeEnd.CreatePair(blocking: true);
            var pipePair2 = PipeEnd.CreatePair(blocking: true);

            epoll_event[] events;
            PosixResult result;

            events = PollEvents(epoll, maxEvents: 10, timeout: 0);
            Assert.Equal(0, events.Length);

            // Register pipePair1 for writable
            result = epoll.TryControl(EPOLL_CTL_ADD, pipePair1.WriteEnd, EPOLLOUT, 0x01020304);
            Assert.True(result.IsSuccess);

            // Add pipePair2
            result = epoll.TryControl(EPOLL_CTL_ADD, pipePair2.WriteEnd, EPOLLOUT, 0x04030201);
            Assert.True(result.IsSuccess);

            // Poll
            events = PollEvents(epoll, maxEvents: 10, timeout: 0);
            Assert.Equal(2, events.Length);
            Assert.Equal(EPOLLOUT, events[0].events);
            Assert.Equal(EPOLLOUT, events[1].events);
            var datas = new long[] { 0x01020304, 0x04030201};
            Assert.Contains(events[0].data.fd, datas);
            Assert.Contains(events[1].data.fd, datas);
            Assert.NotEqual(events[0].data.fd, events[1].data.fd);

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

            epoll_event[] events;
            PosixResult result;

            events = PollEvents(epoll, maxEvents: 10, timeout: 0);
            Assert.Equal(0, events.Length);

            // Register pipePair1 for writable
            result = epoll.TryControl(EPOLL_CTL_ADD, pipePair1.WriteEnd, EPOLLOUT, 0);
            Assert.True(result.IsSuccess);

            // Add pipePair2
            result = epoll.TryControl(EPOLL_CTL_ADD, pipePair2.WriteEnd, EPOLLOUT, 0);
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

            epoll_event[] events;
            PosixResult result;

            // Register pipePair1 for readable with OneShot
            result = epoll.TryControl(EPOLL_CTL_ADD, pipePair1.WriteEnd, EPOLLOUT | EPOLLONESHOT, 0);
            Assert.True(result.IsSuccess);

            // Poll indicates writable
            events = PollEvents(epoll, maxEvents: 10, timeout: 0);
            Assert.Equal(1, events.Length);

            // OneShot, no longer writable
            events = PollEvents(epoll, maxEvents: 10, timeout: 0);
            Assert.Equal(0, events.Length);

            // Rearm without OneShot
            result = epoll.TryControl(EPOLL_CTL_MOD, pipePair1.WriteEnd, EPOLLOUT, 0);

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

            epoll_event[] events;
            PosixResult result;

            // Register pipePair1 for writable
            result = epoll.TryControl(EPOLL_CTL_ADD, pipePair1.WriteEnd, EPOLLOUT, 0);
            Assert.True(result.IsSuccess);

            // Poll indicates writable
            events = PollEvents(epoll, maxEvents: 10, timeout: 0);
            Assert.Equal(1, events.Length);

            // Unregister
            result = epoll.TryControl(EPOLL_CTL_DEL, pipePair1.WriteEnd, 0, 0);

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
            const int milliSecondTimeout = 500;
            const int milliSecondMargin = 10;
            PollEvents(epoll, maxEvents: 10, timeout: milliSecondTimeout);
            var endTime = Environment.TickCount;
            Assert.True(endTime - startTime > milliSecondTimeout - milliSecondMargin);

            epoll.Dispose();
        }

        [Fact]
        public void HangUp()
        {
            var epoll = EPoll.Create();
            var pipePair1 = PipeEnd.CreatePair(blocking: true);

            epoll_event[] events;
            PosixResult result;

            events = PollEvents(epoll, maxEvents: 10, timeout: 0);
            Assert.Equal(0, events.Length);

            // Register pipePair1 for readable
            result = epoll.TryControl(EPOLL_CTL_ADD, pipePair1.ReadEnd, EPOLLIN, 0);
            Assert.True(result.IsSuccess);

            // Close the write end
            pipePair1.WriteEnd.Dispose();

            // Poll returns HangUp
            events = PollEvents(epoll, maxEvents: 10, timeout: 0);
            Assert.Equal(1, events.Length);
            Assert.Equal(EPOLLHUP, events[0].events);

            epoll.Dispose();
            pipePair1.ReadEnd.Dispose();
        }

        [Fact]
        public void Error()
        {
            var epoll = EPoll.Create();
            var pipePair1 = PipeEnd.CreatePair(blocking: true);

            epoll_event[] events;
            PosixResult result;

            events = PollEvents(epoll, maxEvents: 10, timeout: 0);
            Assert.Equal(0, events.Length);

            // Close the read end
            pipePair1.ReadEnd.Dispose();

            // Register pipePair1 for readable
            result = epoll.TryControl(EPOLL_CTL_ADD, pipePair1.WriteEnd, EPOLLOUT, 0);
            Assert.True(result.IsSuccess);

            // Poll returns Writable, Error
            events = PollEvents(epoll, maxEvents: 10, timeout: 0);
            Assert.Equal(1, events.Length);
            Assert.Equal(EPOLLOUT | EPOLLERR, events[0].events);

            epoll.Dispose();
            pipePair1.WriteEnd.Dispose();
        }

        internal static unsafe epoll_event[] PollEvents(EPoll epoll, int maxEvents, int timeout)
        {
            epoll_event* events = stackalloc epoll_event[maxEvents];
            int nrEvents = epoll.Wait(events, maxEvents, timeout);
            var retval = new epoll_event[nrEvents];
            for (int i = 0; i < nrEvents; i++)
            {
                retval[i].events = events[i].events;
                retval[i].data.fd   = events[i].data.fd;
            }
            return retval;
        }
    }
}