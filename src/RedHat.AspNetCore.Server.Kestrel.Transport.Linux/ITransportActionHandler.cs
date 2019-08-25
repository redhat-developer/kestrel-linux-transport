using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;

namespace RedHat.AspNetCore.Server.Kestrel.Transport.Linux
{
    interface ITransportActionHandler
    {
        Task BindAsync();
        Task UnbindAsync();
        Task StopAsync();
        ValueTask<ConnectionContext> AcceptAsync(CancellationToken cancellationToken = default);
    }
}