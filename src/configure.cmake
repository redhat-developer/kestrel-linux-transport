include(CheckCXXSourceCompiles)
include(CheckCXXSourceRuns)
include(CheckFunctionExists)
include(CheckIncludeFiles)
include(CheckPrototypeDefinition)
include(CheckStructHasMember)
include(CheckSymbolExists)
include(CheckTypeSize)

# We compile with -Werror, so we need to make sure these code fragments compile without warnings.
set(CMAKE_REQUIRED_FLAGS -Werror)

check_cxx_source_compiles(
    "
    #include <string.h>
    int main()
    {
        char buffer[1];
        char* c = strerror_r(0, buffer, 0);
    }
    "
    HAVE_GNU_STRERROR_R)

configure_file(
    ${CMAKE_CURRENT_SOURCE_DIR}/Common.Native/config.h.in
    ${CMAKE_CURRENT_BINARY_DIR}/Common.Native/config.h)