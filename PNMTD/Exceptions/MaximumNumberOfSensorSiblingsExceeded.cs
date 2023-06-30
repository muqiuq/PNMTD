using System.Runtime.Serialization;

namespace PNMTD.Exceptions
{
    public class MaximumNumberOfSensorSiblingsExceeded : Exception
    {
        public MaximumNumberOfSensorSiblingsExceeded()
        {
        }

        public MaximumNumberOfSensorSiblingsExceeded(string? message) : base(message)
        {
        }

        public MaximumNumberOfSensorSiblingsExceeded(string? message, Exception? innerException) : base(message, innerException)
        {
        }

        protected MaximumNumberOfSensorSiblingsExceeded(SerializationInfo info, StreamingContext context) : base(info, context)
        {
        }
    }
}
