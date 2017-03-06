// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#pragma once

#include <stdint.h> // int32_t, int64_t, etc.
#include <assert.h>
#include <type_traits>
#include <errno.h>

/**
* Cast a positive value typed as a signed integer to the
* appropriately sized unsigned integer type.
*
* We use this when we've already ensured that the value is positive,
* but we don't want to cast to a specific unsigned type as that could
* inadvertently defeat the compiler's narrowing conversion warnings
* (which we treat as error).
*/
template <typename T>
inline typename std::make_unsigned<T>::type UnsignedCast(T value)
{
    assert(value >= 0);
    return static_cast<typename std::make_unsigned<T>::type>(value);
}

/**
* Converts an intptr_t to a file descriptor.
* intptr_t is the type used to marshal file descriptors so we can use SafeHandles effectively.
*/
inline static int ToFileDescriptor(intptr_t fd)
{
    assert(0 <= fd && fd < sysconf(_SC_OPEN_MAX));

    return static_cast<int>(fd);
}

/**
* Converts a file descriptor to an intptr_t.
* intptr_t is the type used to marshal file descriptors so we can use SafeHandles effectively.
*/
inline static intptr_t FromFileDescriptor(int fd)
{
    assert(0 <= fd && fd < sysconf(_SC_OPEN_MAX));

    return static_cast<intptr_t>(fd);
}

/**
* Checks if the IO operation was interupted and needs to be retried.
* Returns true if the operation was interupted; otherwise, false.
*/
template <typename TInt>
static inline bool CheckInterrupted(TInt result)
{
    return result < 0 && errno == EINTR;
}

enum PosixResult : int32_t {};
static inline PosixResult ToPosixResult(int ret)
{
    return ret < 0 ? static_cast<PosixResult>(-errno) : static_cast<PosixResult>(ret);
}

static inline PosixResult PosixResultForErrno(int nativeErrno)
{
    return static_cast<PosixResult>(-nativeErrno);
}

const static PosixResult PosixResultEFAULT = static_cast<PosixResult>(-EFAULT);
const static PosixResult PosixResultSUCCESS = static_cast<PosixResult>(0);
