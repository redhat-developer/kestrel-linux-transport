project(Tmds.Kestrel.Linux.Native)

set(SOURCES
    pipeend.cpp
    epoll.cpp
    socket.cpp
    scheduler.cpp
)

add_library(Tmds.Kestrel.Linux.Native
    SHARED
    ${SOURCES}
)

install_library_and_symbols (Tmds.Kestrel.Linux.Native)