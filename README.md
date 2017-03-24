[![Travis](https://api.travis-ci.org/tmds/Tmds.Kestrel.Linux.svg?branch=master)](https://travis-ci.org/tmds/Tmds.Kestrel.Linux)

# Introduction

The ASP.NET Core Kestrel webserver has been using libuv as a cross-platform network library.
In future it will be possible to replace the `Transport` with other implementations: https://github.com/aspnet/KestrelHttpServer/issues/828.

In this repo we explore creating a Transport for Linux.

# CI

NuGet feed: `https://www.myget.org/F/tmds/api/v3/index.json`
```
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <add key="Tmds.Kestrel.Linux" value="https://www.myget.org/F/tmds/api/v3/index.json" />
  </packageSources>
</configuration>
```

Dependency: `"Tmds.Kestrel.Linux": "0.1.0-*"`

# Repo structure

There are 4 projects in this repository:
- src/Tmds.Kestrel.Linux: managed library implementing Transport
- src/Tmds.Kestrel.Linux.Native: native library used by managed library
- test/Tmds.Kestrel.Linux.Test: xunit test projects, has access to internals of managed library
- test/Tmds.Kestrel.Linux.TestApp: empty application to use during development, has access to internals of managed library

Before anything can be done, dependencies need to be restore:
```
$ dotnet restore
```

The library can be packaged by running the `dotnet pack` on src/Tmds.Kestrel.Linux.
```
$ dotnet pack src/Tmds.Kestrel.Linux --configuration Release
```

To build the library and run the tests execute `dotnet test` on test/Tmds.Kestrel.Linux.Test.
```
$ dotnet test test/Tmds.Kestrel.Linux.Test
```

It is also possible to build the native and managed parts of the library without executing tests.
```
$ src/build-native.sh
$ dotnet build src/Tmds.Kestrel.Linux
```

# Design

## General

Since this library doesn't aim to be xplat or provide a generic networking API, we can include only what is necessary and
optimize for the specific use-case.

**For example**: the libuv and corfx async socket APIs have a receive and send function. Since these functinos are async, their
arguments (e.g. `uv_buf_t`, `SocketAsyncEventArgs`) must be allocated on the heap. To reduce pressure on the GC, these types
can be pooled. In this library, the async operations on the limited to waiting for a socket to be come readable/writable. These operations
don't have arguments so they don't require allocation and pooling.

## Threading

### Kestrel libuv

The kestrel libuv implementation has a one listener thread that accepts incoming connection and then passes these off to
other threads for processing. Connections are distributed round-robin between the threads.

See [ListenerPrimary.cs](https://github.com/aspnet/KestrelHttpServer/blob/7d3bcd2bf868dbd65741da1569ce974993a8e720/src/Microsoft.AspNetCore.Server.Kestrel/Internal/Http/ListenerPrimary.cs#L97-L121)

### .NET Core Framework

`System.Net.Sockets.Socket` has one thread that monitors asynchronous socket events.

See [SocketAsyncEngine.Unix.cs](https://github.com/dotnet/corefx/blob/4611d411d892bd4c4fa9e4dfc2e4cdbb89fea799/src/System.Net.Sockets/src/System/Net/Sockets/SocketAsyncEngine.Unix.cs#L58).

### Tmds.Kestrel.Linux

The implementation starts a number of threads which each accept connections. This is based on [`SO_REUSEPORT`](https://lwn.net/Articles/542629/)
socket option. This option allow multiple sockets to concurrently bind and listen to the same port. The kernel performs
load-balancing between the listen sockets.

## Socket options

### SO_REUSEPORT

This option allow multiple sockets to concurrently bind and listen to the same port. The kernel performs
load-balancing between the listen sockets.

### TCP_DEFER_ACCEPT

This option can be used for protocols where the client sends first (like HTTP). Instead of being notified of a new connection when the TCP connection is set-up,
the application is notified when the connection was setup and data has arrived.
