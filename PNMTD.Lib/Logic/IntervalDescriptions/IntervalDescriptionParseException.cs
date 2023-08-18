using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using System.Text;
using System.Threading.Tasks;

namespace PNMTD.Lib.Logic.IntervalDescriptions
{
    public class IntervalDescriptionParseException : Exception
    {
        public IntervalDescriptionParseException()
        {
        }

        public IntervalDescriptionParseException(string? message) : base(message)
        {
        }

        public IntervalDescriptionParseException(string? message, Exception? innerException) : base(message, innerException)
        {
        }

        protected IntervalDescriptionParseException(SerializationInfo info, StreamingContext context) : base(info, context)
        {
        }
    }
}
