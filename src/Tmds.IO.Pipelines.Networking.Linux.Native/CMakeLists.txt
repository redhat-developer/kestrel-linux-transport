project(Tmds.IO.Pipelines.Networking.Linux.Native)

set(SOURCES
    lib.cpp
)

add_library(Tmds.IO.Pipelines.Networking.Linux.Native
    SHARED
    ${SOURCES}
)

install_library_and_symbols (Tmds.IO.Pipelines.Networking.Linux.Native)