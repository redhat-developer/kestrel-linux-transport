using System.Threading.Tasks;

namespace RedHatX.AspNetCore.Server.Kestrel.Transport.Linux
{
    interface ITransportActionHandler
    {
        Task BindAsync();
        Task UnbindAsync();
        Task StopAsync();
    }
}