// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;

namespace System.Buffers
{
   internal class MemoryPoolThrowHelper
    {
        public static void ThrowArgumentOutOfRangeException_BufferRequestTooLarge(int maxSize)
        {
            throw GetArgumentOutOfRangeException_BufferRequestTooLarge(maxSize);
        }

        public static void ThrowObjectDisposedException(ExceptionArgument argument)
        {
            throw GetObjectDisposedException(argument);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static ArgumentOutOfRangeException GetArgumentOutOfRangeException_BufferRequestTooLarge(int maxSize)
        {
            return new ArgumentOutOfRangeException(GetArgumentName(ExceptionArgument.size), $"Cannot allocate more than {maxSize} bytes in a single buffer");
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static ObjectDisposedException GetObjectDisposedException(ExceptionArgument argument)
        {
            return new ObjectDisposedException(GetArgumentName(argument));
        }

        private static string GetArgumentName(ExceptionArgument argument)
        {
            Debug.Assert(Enum.IsDefined(typeof(ExceptionArgument), argument), "The enum value is not defined, please check the ExceptionArgument Enum.");

            return argument.ToString();
        }

        internal enum ExceptionArgument
        {
            size,
            offset,
            length,
            MemoryPoolBlock,
            MemoryPool
        }
    }
}
