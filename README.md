[![Travis](https://travis-ci.org/redhat-developer/kestrel-linux-transport.svg?branch=master)](https://travis-ci.org/redhat-developer/kestrel-linux-transport)

# Introduction

The ASP.NET Core Kestrel webserver has been using libuv as a cross-platform network library.
It is possible to replace libuv with another implementation thanks to the `Transport` abstraction.

In this repo we explore creating a Transport for Linux specifically.

# Using the package

Add the myget feed to your `NuGet.Config` file:
```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <add key="rh" value="https://www.myget.org/F/redhat-dotnet/api/v3/index.json" />
  </packageSources>
</configuration>
```

Include a package reference in your project `csproj` file:
```xml
  <ItemGroup>
    <PackageReference Include="RedHat.AspNetCore.Server.Kestrel.Transport.Linux" Version="3.0.0-*" />
  </ItemGroup>
```

Call `UseLinuxTransport` when creating the `WebHost` in your `Program.cs`:
```C#
public static IWebHost BuildWebHost(string[] args) =>
            Host.CreateDefaultBuilder(args)
                .ConfigureWebHostDefaults(webBuilder =>
                {
                    webBuilder
                        .UseLinuxTransport()
                        .UseStartup<Startup>();
                });
```

**note**: It's safe to call `UseLinuxTransport` on non-Linux platforms, it will no-op.

# Repo structure

There are 5 projects in this repository:
- src/RedHat.AspNetCore.Server.Kestrel.Transport.Linux: managed library implementing Transport
- samples/KestrelSample: Kestrel app for [benchmarking](Benchmark.md)
- test/RedHat.AspNetCore.Server.Kestrel.Transport.Linux.Test: xunit test projects, has access to internals of managed library
- test/RedHat.AspNetCore.Server.Kestrel.Transport.Linux.TestApp: empty application to use during development, has access to internals of managed library

The library can be packaged by running the `dotnet pack` on src/RedHat.AspNetCore.Server.Kestrel.Transport.Linux.
```
$ dotnet pack src/RedHat.AspNetCore.Server.Kestrel.Transport.Linux --configuration Release
```

To build the library and run the tests execute `dotnet test` on test/RedHat.AspNetCore.Server.Kestrel.Transport.Linux.Test.
```
$ dotnet test test/RedHat.AspNetCore.Server.Kestrel.Transport.Linux.Test
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

- **DeferSend**: This defers sends to the Transport Thread which increases chances for multiple sends to coalesce. This options
defaults to true.

- **ThreadCount**: Specifies the number of Transport Threads. This defaults to the number of logical processors in the system, maxed to 16.

- **AioSend/AioReceive**: Uses Linux AIO system calls to batch send and receive calls. AioSend implies DeferSend. These options default to true.
