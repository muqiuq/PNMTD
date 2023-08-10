using System.Runtime.Serialization;

namespace PNMT.ApiClient.Data
{
    public class PNMTDApiException : Exception
    {
        public PNMTDApiException()
        {
        }

        public PNMTDApiException(string? message) : base(message)
        {
        }

        public PNMTDApiException(string? message, Exception? innerException) : base(message, innerException)
        {
        }

        protected PNMTDApiException(SerializationInfo info, StreamingContext context) : base(info, context)
        {
        }
    }
}
