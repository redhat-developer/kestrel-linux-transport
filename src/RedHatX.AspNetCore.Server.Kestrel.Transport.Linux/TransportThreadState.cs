namespace RedHatX.AspNetCore.Server.Kestrel.Transport.Linux
{
    enum TransportThreadState
    {
        Initial,
        Starting,
        Started,
        ClosingAccept,
        AcceptClosed,
        Stopping,
        Stopped
    }
}