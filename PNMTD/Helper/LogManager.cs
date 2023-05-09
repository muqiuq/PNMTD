namespace PNMTD.Helper
{
    public static class LogManager
    {
        public static ILogger<T> CreateLogger<T>()
        {
            var l =  Global.CreateLogger<T>();
            if (l != null) return l;

            var tempFactory = LoggerFactory.Create(builder =>
            {
                builder.AddConsole();
            });
            return tempFactory.CreateLogger<T>();
        }

    }
}
