// Copyright 2017 Tom Deseyn <tom.deseyn@gmail.com>
// This software is made available under the MIT License
// See COPYING for details

using System;

namespace RedHatX.AspNetCore.Server.Kestrel.Transport.Linux
{
    public class PosixException : System.Exception
    {
        private string _message;
        public PosixException(int error) :
            base(string.Empty)
        {
            if (error >= 0)
            {
                throw new ArgumentException("Should be a negative number equal to '-errno'", nameof(error));
            }
            Error = error;
        }

        public override string Message
        {
            get
            {
                if (_message == null)
                {
                    PosixResult result = new PosixResult(Error);
                    _message = $"{result.ErrorName()} - {result.ErrorDescription()}";
                }
                return _message;
            }
        }

        public int Error { get; private set; }
    }
}