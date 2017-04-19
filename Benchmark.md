# Benchmarking

The engineering benchmark setup of the Microsoft asp.net team is described at: https://github.com/aspnet/benchmarks.

The TechEmpower Web Framework Benchmark compare various frameworks including Asp.Net Core https://www.techempower.com/benchmarks/.

# Benchmark Setup

## Machines

Benchmarking should be done using two separate machines connected via a dedicated high-speed network.

## Load Generation

Load is generated using the wrk tool (https://github.com/wg/wrk).

```
Usage: wrk <options> <url>                            
  Options:                                            
    -c, --connections <N>  Connections to keep open   
    -d, --duration    <T>  Duration of test           
    -t, --threads     <N>  Number of threads to use   
                                                      
    -s, --script      <S>  Load Lua script file       
    -H, --header      <H>  Add header to request      
        --latency          Print latency statistics   
        --timeout     <T>  Socket/request timeout     
    -v, --version          Print version details      
                                                      
  Numeric arguments may include a SI unit (1k, 1M, 1G)
  Time arguments may include a time unit (2s, 2m, 2h)

```

We focus on the TechEmpower Plaintext benchmark (Type 6 at https://www.techempower.com/benchmarks/#section=code).

> This test is an exercise of the request-routing fundamentals only, designed to demonstrate the capacity of high-performance platforms in particular. Requests will be sent using HTTP pipelining. The response payload is still small, meaning good performance is still necessary in order to saturate the gigabit Ethernet of the test environment.

```
wrk -H 'Host: server' -H 'User-Agent: Mozilla/5.0 (X11; Linux x86_64) Gecko/20130501 Firefox/30.0 AppleWebKit/600.00 Chrome/30.0.0000.0 Trident/10.0 Safari/600.00' -H 'Cookie: uid=12345678901234567890; __utma=1.1234567890.1234567890.1234567890.1234567890.12; wd=2560x1600' -H 'Accept: text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8' -H 'Accept-Language: en-US,en;q=0.5' -H 'Connection: keep-alive' --latency -d 15 -c 256 --timeout 8 -t 32 http://127.0.0.1:5000/plaintext -s ~/pipeline.lua -- 16
```

Where pipeline.lua (source: https://raw.githubusercontent.com/aspnet/benchmarks/dev/scripts/pipeline.lua):
```
local pipelineDepth = 1
local counter = 0
local maxRequests = -1
local method = "GET"

function init(args)

   if args[1] ~= nil then
      pipelineDepth = tonumber(args[1])
   end

   if args[2] ~= nil then
      method = args[2]
   end

   local r = {}
   for i = 1, pipelineDepth, 1 do
      r[i] = wrk.format(method)
   end

   print("Pipeline depth: " .. pipelineDepth)

   if args[3] ~= nil then
      maxRequests = tonumber(args[3])
      print("Max requests: " .. maxRequests)
   end

   req = table.concat(r)
end

function request()
   return req
end

function response()
   if counter == maxRequests then
     wrk.thread:stop()
   end
   counter = counter + 1
end
```

This causes the wrk tool to send 16 requests pipelined (6624 bytes).

## Webserver

As a webserver we use the application at `samples/KestrelSample`. The plaintext test is handled by the PlaintextMiddleware (same as TechEmpower).

```
Options: [libuv] [-c<cpuset>] [-t<threadcount>] [ta] [ic] [noda] [nott]
  General:
	libuv    Use libuv Transport instead of Linux Transport
	-t<tc>   Number of transport threads
	nott     Defer requests to thread pool
  Linux transport specific:
	ta       Set thread affinity
	ic       Receive on incoming cpu (implies ta)
	-c<cpus> Cpus for transport threads (implies ta, count = default for -t)
	noda     No deferred accept
```