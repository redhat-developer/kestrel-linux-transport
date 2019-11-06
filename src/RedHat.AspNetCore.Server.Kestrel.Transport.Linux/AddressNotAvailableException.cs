using System;
using System.Runtime.Serialization;

namespace RedHat.AspNetCore.Server.Kestrel.Transport.Linux
{
    public class AddressNotAvailableException : Exception
    {
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
        
        public AddressNotAvailableException(string message, Exception innerException) 
            : base(message, innerException)
        {
        }
    }
}