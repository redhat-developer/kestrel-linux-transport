namespace RedHatX.AspNetCore.Server.Kestrel.Transport.Linux
{
    enum EPollOperation : int
    {
        Add    = 1, // EPOLL_CTL_ADD
        Delete = 2, // EPOLL_CTL_DEL
        Modify = 3, // EPOLL_CTL_MOD
    }
}