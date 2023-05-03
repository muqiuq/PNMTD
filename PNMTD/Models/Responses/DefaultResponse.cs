namespace PNMTD.Models.Responses
{
    public class DefaultResponse
    {
        public bool Success { get; set; }

        public string? Message { get; set; }

        public object? Data { get; set; }

    }
}
