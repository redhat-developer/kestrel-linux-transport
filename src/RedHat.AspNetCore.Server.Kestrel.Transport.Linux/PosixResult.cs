// Copyright 2017 Tom Deseyn <tom.deseyn@gmail.com>
// This software is made available under the MIT License
// See COPYING for details

using System;
using System.IO;
using System.Collections.Generic;

namespace RedHat.AspNetCore.Server.Kestrel.Transport.Linux
{
    internal partial struct PosixResult
    {
        private int _value;

        public int Value => _value;

        public PosixResult(int value)
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

        public static PosixResult FromReturnValue(int rv)
        {
            return rv < 0 ? new PosixResult(-Tmds.LibC.Definitions.errno) : new PosixResult(rv);
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
                    string description;
                    if (s_errnoDescriptions.TryGetValue(_value, out description))
                    {
                        return description;
                    }
                    description = ErrorInterop.StrError(-_value);
                    s_errnoDescriptions.Add(_value, description);
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
            return new IOException(ErrorDescription(), _value);
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