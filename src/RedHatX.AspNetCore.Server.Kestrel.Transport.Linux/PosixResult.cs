// Copyright 2017 Tom Deseyn <tom.deseyn@gmail.com>
// This software is made available under the MIT License
// See COPYING for details

namespace RedHatX.AspNetCore.Server.Kestrel.Transport.Linux
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

        internal string ErrorDescription()
        {
            if (_value >= 0)
            {
                return string.Empty;
            }
            else
            {
                lock (s_descriptions)
                {
                    string description;
                    if (s_descriptions.TryGetValue(_value, out description))
                    {
                        return description;
                    }
                    description = ErrorInterop.StrError(-_value);
                    s_descriptions.Add(_value, description);
                    return description;
                }
            }
        }

        internal string ErrorName()
        {
            if (_value < 0)
            {
                string name;
                if (s_names.TryGetValue(_value, out name))
                {
                    return name;
                }
                return $"E{-_value}";
            }
            else
            {
                return string.Empty;
            }
        }

        public void ThrowOnError()
        {
            if (!IsSuccess)
            {
                throw new PosixException(_value);
            }
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
                return ErrorName();
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