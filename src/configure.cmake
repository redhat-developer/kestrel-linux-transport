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

configure_file(
    ${CMAKE_CURRENT_SOURCE_DIR}/Common.Native/config.h.in
    ${CMAKE_CURRENT_BINARY_DIR}/Common.Native/config.h)