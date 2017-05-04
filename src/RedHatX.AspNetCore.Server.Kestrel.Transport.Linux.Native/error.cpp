// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#include "utilities.h"

#include "config.h"

#include <stdlib.h>
#include <errno.h>
#include <assert.h>
#include <string.h>

extern "C" const char* RHXKL_StrErrorR(int32_t platformErrno, char* buffer, int32_t bufferSize)
{
    assert(buffer != nullptr);
    assert(bufferSize > 0);

    if (bufferSize < 0)
        return nullptr;

// Note that we must use strerror_r because plain strerror is not
// thread-safe.
//
// However, there are two versions of strerror_r:
//    - GNU:   char* strerror_r(int, char*, size_t);
//    - POSIX: int   strerror_r(int, char*, size_t);
//
// The former may or may not use the supplied buffer, and returns
// the error message string. The latter stores the error message
// string into the supplied buffer and returns an error code.

#if HAVE_GNU_STRERROR_R
    const char* message = strerror_r(platformErrno, buffer, UnsignedCast(bufferSize));
    assert(message != nullptr);
    return message;
#else
    int error = strerror_r(platformErrno, buffer, UnsignedCast(bufferSize));
    if (error == ERANGE)
    {
        // Buffer is too small to hold the entire message, but has
        // still been filled to the extent possible and null-terminated.
        return nullptr;
    }

    // The only other valid error codes are 0 for success or EINVAL for
    // an unkown error, but in the latter case a reasonable string (e.g
    // "Unknown error: 0x123") is returned.
    assert(error == 0 || error == EINVAL);
    return buffer;
#endif
}

extern "C" void RHXKL_GetErrnoValues(int32_t* values, int32_t length)
{
    if (length != 82)
    {
        abort();
    }

    values[0] = E2BIG;
    values[1] = EACCES;
    values[2] = EADDRINUSE;
    values[3] = EADDRNOTAVAIL;
    values[4] = EAFNOSUPPORT;
    values[5] = EAGAIN;
    values[6] = EALREADY;
    values[7] = EBADF;
    values[8] = EBADMSG;
    values[9] = EBUSY;
    values[10] = ECANCELED;
    values[11] = ECHILD;
    values[12] = ECONNABORTED;
    values[13] = ECONNREFUSED;
    values[14] = ECONNRESET;
    values[15] = EDEADLK;
    values[16] = EDESTADDRREQ;
    values[17] = EDOM;
    values[18] = EDQUOT;
    values[19] = EEXIST;
    values[20] = EFAULT;
    values[21] = EFBIG;
    values[22] = EHOSTUNREACH;
    values[23] = EIDRM;
    values[24] = EILSEQ;
    values[25] = EINPROGRESS;
    values[26] = EINTR;
    values[27] = EINVAL;
    values[28] = EIO;
    values[29] = EISCONN;
    values[30] = EISDIR;
    values[31] = ELOOP;
    values[32] = EMFILE;
    values[33] = EMLINK;
    values[34] = EMSGSIZE;
    values[35] = EMULTIHOP;
    values[36] = ENAMETOOLONG;
    values[37] = ENETDOWN;
    values[38] = ENETRESET;
    values[39] = ENETUNREACH;
    values[40] = ENFILE;
    values[41] = ENOBUFS;
    values[42] = ENODEV;
    values[43] = ENOENT;
    values[44] = ENOEXEC;
    values[45] = ENOLCK;
    values[46] = ENOLINK;
    values[47] = ENOMEM;
    values[48] = ENOMSG;
    values[49] = ENOPROTOOPT;
    values[50] = ENOSPC;
    values[51] = ENOSYS;
    values[52] = ENOTCONN;
    values[53] = ENOTDIR;
    values[54] = ENOTEMPTY;
    values[55] = ENOTRECOVERABLE;
    values[56] = ENOTSOCK;
    values[57] = ENOTSUP;
    values[58] = ENOTTY;
    values[59] = ENXIO;
    values[60] = EOVERFLOW;
    values[61] = EOWNERDEAD;
    values[62] = EPERM;
    values[63] = EPIPE;
    values[64] = EPROTO;
    values[65] = EPROTONOSUPPORT;
    values[66] = EPROTOTYPE;
    values[67] = ERANGE;
    values[68] = EROFS;
    values[69] = ESPIPE;
    values[70] = ESRCH;
    values[71] = ESTALE;
    values[72] = ETIMEDOUT;
    values[73] = ETXTBSY;
    values[74] = EXDEV;
    values[75] = ESOCKTNOSUPPORT;
    values[76] = EPFNOSUPPORT;
    values[77] = ESHUTDOWN;
    values[78] = EHOSTDOWN;
    values[79] = ENODATA;
    values[80] = EOPNOTSUPP;
    values[81] = EWOULDBLOCK;
}
