using System;
using System.IO.Pipelines;
using System.Text;
using System.Text.Formatting;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Server.Kestrel.Transport;
using Tmds.Kestrel.Linux;

namespace ConsoleApplication
{
    class HttpServer : IConnectionHandler
    {
        class ConnectionContext : IConnectionContext
        {
            public ConnectionContext(string connectionId, IPipeWriter input, IPipeReader output)
            {
                ConnectionId = connectionId;
                Input = input;
                Output = output;
            }
            public string ConnectionId { get; }
            public IPipeWriter Input { get; }
            public IPipeReader Output { get; }

            // TODO: Remove these (Use Pipes instead?)
            Task IConnectionContext.StopAsync() { throw new NotSupportedException(); }
            void IConnectionContext.Abort(Exception ex) { throw new NotSupportedException(); }
            void IConnectionContext.Timeout() { throw new NotSupportedException(); }
        }

        public HttpServer()
        {}
        
        public IConnectionContext OnConnection(IConnectionInformation connectionInformation)
        {
            var factory = connectionInformation.PipeFactory;
            var input = factory.Create(Transport.InputPipeOptions);
            var output = factory.Create(Transport.OutputPipeOptions);

            HandleConnection(input.Reader, output.Writer);

            return new ConnectionContext(string.Empty, input.Writer, output.Reader);
        }

        private async void HandleConnection(IPipeReader reader, IPipeWriter writer)
        {
            try
            {
                bool complete = false;
                while (!complete)
                {
                    var result = await reader.ReadAsync();
                    ReadableBuffer input = result.Buffer;
                    complete = result.IsCompleted;
                    if (input.IsEmpty && result.IsCompleted)
                    {
                        // No more data
                        reader.Advance(input.End, input.End);
                        break;
                    }

                    ReadOnlySpan<byte> bytes = input.First.Span;

                    // Parse RequestLine
                    int requestLineParsed;
                    HttpRequestLine requestLine;
                    if (!HttpRequestParser.TryParseRequestLine(bytes, out requestLine, out requestLineParsed))
                    {
                        complete = input.Length > 1000;
                        reader.Advance(input.Start, input.End);
                        continue;
                    }
                    bytes = bytes.Slice(requestLineParsed);

                    // Parse Headers
                    int headerParsed;
                    HttpHeadersSingleSegment headers;
                    if (!HttpRequestParser.TryParseHeaders(bytes, out headers, out headerParsed))
                    {
                        complete = input.Length > 1000;
                        reader.Advance(input.Start, input.End);
                        continue;
                    }

                    // We don't support a Body

                    // Succesfully parsed RequestLine and Header
                    reader.Advance(input.Move(input.Start, requestLineParsed + headerParsed));

                    // Respond
                    var output = writer.Alloc();
                    output.Append("HTTP/1.1 200 OK", TextEncoder.Utf8);
                    output.Append("\r\nContent-Length: 13", TextEncoder.Utf8);
                    output.Append("\r\nContent-Type: text/plain", TextEncoder.Utf8);
                    output.Append("\r\nDate: ", TextEncoder.Utf8); output.Append(DateTime.UtcNow, TextEncoder.Utf8, 'R');
                    output.Append("\r\nServer: System.IO.Pipelines", TextEncoder.Utf8);
                    output.Append("\r\n\r\n", TextEncoder.Utf8);
                    // write body
                    output.Append("Hello, World!", TextEncoder.Utf8);
                    await output.FlushAsync();
                }
            }
            catch
            {}
            finally
            {
                reader.Complete();
                writer.Complete();
            }
        }
    }
}