using System;
using System.Runtime.Serialization;

namespace RedHat.AspNetCore.Server.Kestrel.Transport.Linux
{
    public class AddressNotAvailableException : Exception
    {
        public int HResult { get; }
        public AddressNotAvailableException()
        {
        }

        protected AddressNotAvailableException(SerializationInfo info, StreamingContext context) 
            : base(info, context)
        {
        }

        public AddressNotAvailableException(string message) 
            : base(message)
        {
        }
        
        public AddressNotAvailableException(string message, int hResult) 
            : base(message)
        {
            HResult = hResult;
        }

        public AddressNotAvailableException(string message, Exception innerException) 
            : base(message, innerException)
        {
        }
    }
}