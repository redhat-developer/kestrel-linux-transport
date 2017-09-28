using System.Threading.Tasks;

namespace RedHatX.AspNetCore.Server.Kestrel.Transport.Linux
{
    sealed class AcceptThread : ITransportActionHandler
    {
        public AcceptThread(Socket socket)
        {}

        public Socket CreateThreadSocket()
            => null;

        public Task BindAsync()
            => Task.CompletedTask;

        public Task UnbindAsync()
            => Task.CompletedTask;

        public Task StopAsync()
            => Task.CompletedTask;
    }
}