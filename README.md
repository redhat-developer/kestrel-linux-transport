[![Travis](https://travis-ci.org/redhat-developer/kestrel-linux-transport.svg?branch=master)](https://travis-ci.org/redhat-developer/kestrel-linux-transport)

# Introduction

The ASP.NET Core Kestrel webserver has been using libuv as a cross-platform network library.
It is possible to replace libuv with another implementation thanks to the `Transport` abstraction.

In this repo we explore creating an experimental Transport for Linux.

# CI

NuGet feed: `https://www.myget.org/F/redhat-dotnet/api/v3/index.json`
```
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <add key="rh" value="https://www.myget.org/F/redhat-dotnet/api/v3/index.json" />
  </packageSources>
</configuration>
```

Dependency: `"RedHatX.AspNetCore.Server.Kestrel.Transport.Linux": "0.1.0-*"`

# Repo structure

There are 5 projects in this repository:
- src/RedHatX.AspNetCore.Server.Kestrel.Transport.Linux: managed library implementing Transport
- src/RedHatX.AspNetCore.Server.Kestrel.Transport.Linux.Native: native library used by managed library
- samples/KestrelSample: Kestrel app for [benchmarking](Benchmark.md)
- test/RedHatX.AspNetCore.Server.Kestrel.Transport.Linux.Test: xunit test projects, has access to internals of managed library
- test/RedHatX.AspNetCore.Server.Kestrel.Transport.Linux.TestApp: empty application to use during development, has access to internals of managed library

The library can be packaged by running the `dotnet pack` on src/RedHatX.AspNetCore.Server.Kestrel.Transport.Linux.
```
$ dotnet pack src/RedHatX.AspNetCore.Server.Kestrel.Transport.Linux --configuration Release
```

To build the library and run the tests execute `dotnet test` on test/RedHatX.AspNetCore.Server.Kestrel.Transport.Linux.Test.
```
$ dotnet test test/RedHatX.AspNetCore.Server.Kestrel.Transport.Linux.Test
```

# Design

Similar to other implementations, this library makes use of the non-blocking socket and epoll. Like the corefx
Socket implementation, the eventloop is implemented in managed (C#) code. This is different from the libuv loop which
is part of the native libuv library.

This library does not provide a generic xplat network API. It uses the kernel primitives directly to implement the
Transport API. This reduces the number of heap allocated objects (e.g. `uv_buf_t`, `SocketAsyncEventArgs`), which means
there is less GC pressure. Implementations building on top of an xplat API will pool objects to achieve this.

The implementation starts a number of threads that each accept connections. This is based on [`SO_REUSEPORT`](https://lwn.net/Articles/542629/)
socket option. This option allow multiple sockets to concurrently bind and listen to the same port. The kernel performs
load-balancing between the listen sockets.

The Transport has these options:

- **SetThreadAffinity**: This option binds the Transport Threads to specific cpus. This is improves data caching. This
is even more important for NUMA systems. This option defaults to false.

- **CpuSet**: Specifies the logical processors on which to bind the TransportThreads. This option implies `SetThreadAffinity`.

- **ReceiveOnIncomingCPU**: This uses the [`SO_INCOMING_CPU`](https://www.spinics.net/lists/netdev/msg347106.html) socket option.
This makes the kernel cpu that handles the socket match with the application cpu that will handle the receive. This requires
the NIC is configured to receive on multiple cpus using RSS. This option implies `SetThreadAffinity`.

- **DeferAccept**: This uses the `TCP_DEFER_ACCEPT` socket option. Instead of being notified of a new connection when
the TCP connection is set up, the application is notified when the connection was setup and data has arrived. This options
defaults to true.

- **DeferSend**: This defers sends to the Transport Thread which increases chances for multiple sends to coalesce. This options
defaults to true.

- **ThreadCount**: Specifies the number of Transport Threads. This defaults to the number processors in `CpuSet` when specified
and the number of logical processors in the system otherwise.

This transport can be used with the Kestrel `UseTransportThread` option.
See `samples/KestrelSample` on how to use the transport with Kestrel.