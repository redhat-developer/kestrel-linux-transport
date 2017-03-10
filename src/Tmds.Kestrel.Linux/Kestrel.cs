using System.IO.Pipelines;
using System.Net;

namespace Kestrel
{
    // interfaces per https://github.com/aspnet/KestrelHttpServer/issues/828

    public interface IConnectionHandler
    {
        IConnectionContext OnConnection(IConnectionInformation connectionInfo, PipeOptions inputOptions, PipeOptions outputOptions);
    }

    public interface IConnectionContext
    {
        string ConnectionId { get; }
        IPipeWriter Input { get; }
        IPipeReader Output { get; }
    }

    public interface IConnectionInformation
    {
        IPEndPoint RemoteEndPoint { get; }
        IPEndPoint LocalEndPoint { get; }
    }
}