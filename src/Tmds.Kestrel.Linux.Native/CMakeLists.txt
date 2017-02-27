project(Tmds.Kestrel.Linux.Native)

set(SOURCES
    lib.cpp
)

add_library(Tmds.Kestrel.Linux.Native
    SHARED
    ${SOURCES}
)

install_library_and_symbols (Tmds.Kestrel.Linux.Native)