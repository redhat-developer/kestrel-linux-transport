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

# Design

...