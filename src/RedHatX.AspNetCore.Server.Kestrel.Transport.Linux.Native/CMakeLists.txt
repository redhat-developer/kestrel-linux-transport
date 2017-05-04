project(RedHatX.AspNetCore.Server.Kestrel.Transport.Linux.Native)

set(SOURCES
    pipeend.cpp
    epoll.cpp
    socket.cpp
    scheduler.cpp
    io.cpp
    error.cpp
)

add_library(RedHatX.AspNetCore.Server.Kestrel.Transport.Linux.Native
    SHARED
    ${SOURCES}
)

install_library_and_symbols (RedHatX.AspNetCore.Server.Kestrel.Transport.Linux.Native)