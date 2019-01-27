// Copyright 2017 Tom Deseyn <tom.deseyn@gmail.com>
// This software is made available under the MIT License
// See COPYING for details

using System;
using System.IO;
using System.Collections.Generic;
using Tmds.Linux;

namespace RedHat.AspNetCore.Server.Kestrel.Transport.Linux
{
    internal partial struct PosixResult
    {
        private ssize_t _value;

        public ssize_t Value => _value;
        public int IntValue => (int)_value;

        public PosixResult(ssize_t value)
        {
            _value = value;
        }

        public bool IsSuccess
        {
            get
            {
                return _value >= 0;
            }
        }

        public static PosixResult FromReturnValue(ssize_t rv)
        {
            return rv < 0 ? new PosixResult(-Tmds.Linux.LibC.errno) : new PosixResult(rv);
        }

        internal string ErrorDescription()
        {
            if (_value >= 0)
            {
                return string.Empty;
            }
            else
            {
                lock (s_errnoDescriptions)
                {
                    int errno = (int)-_value;
                    string description;
                    if (s_errnoDescriptions.TryGetValue(errno, out description))
                    {
                        return description;
                    }
                    description = ErrorInterop.StrError(errno);
                    s_errnoDescriptions.Add(errno, description);
                    return description;
                }
            }
        }

        private static Dictionary<int, string> s_errnoDescriptions = new Dictionary<int, string>();

        public Exception AsException()
        {
            if (IsSuccess)
            {
                throw new InvalidOperationException($"{nameof(PosixResult)} is not an error.");
            }
            return new IOException(ErrorDescription(), (int)-_value);
        }

        public void ThrowOnError()
        {
            if (!IsSuccess)
            {
                ThrowException();
            }
        }

        private void ThrowException()
        {
            throw AsException();
        }

        public static implicit operator bool(PosixResult result)
        {
            return result.IsSuccess;
        }

        public override bool Equals (object obj)
        {
            var other = obj as PosixResult?;
            if (other == null)
            {
                return false;
            }
            return _value == other.Value._value;
        }

        public override int GetHashCode()
        {
            return _value.GetHashCode();
        }

        public override string ToString()
        {
            if (IsSuccess)
            {
                return _value.ToString();
            }
            else
            {
                return ErrorDescription();
            }
        }

        public static bool operator==(PosixResult lhs, int nativeValue)
        {
            return lhs._value == nativeValue;
        }

        public static bool operator!=(PosixResult lhs, int nativeValue)
        {
            return lhs._value != nativeValue;
        }

        public static bool operator==(PosixResult lhs, PosixResult rhs)
        {
            return lhs._value == rhs._value;
        }

        public static bool operator!=(PosixResult lhs, PosixResult rhs)
        {
            return lhs._value != rhs._value;
        }
    }
}