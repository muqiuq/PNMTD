namespace PNMTD.Helper
{
    public static class HttpRequestExtensions
    {
        public static async Task<string> GetRequestBody(this HttpRequest httpRequest)
        {
            var bodyStream = new StreamReader(httpRequest.Body);
            var bodyText = await bodyStream.ReadToEndAsync();
            return bodyText;
        }
    }
}
