using System.Threading.Tasks;

namespace RedHat.AspNetCore.Server.Kestrel.Transport.Linux
{
    interface ITransportActionHandler
    {
        Task BindAsync();
        Task UnbindAsync();
        Task StopAsync();
    }
}