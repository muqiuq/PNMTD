using Microsoft.Extensions.Logging.Console;

namespace PNMTD
{
    public class Global
    {
        public static ILoggerFactory _LoggerFactory;
        internal static WebApplication App;

        public static bool IsDevelopment 
        { 
            get
            {
                return Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") == "Development";
            } 
        }

        public static ILogger<T> CreateLogger<T>()
        {
            if(App == null) { return null;  }
            return App.Services.GetRequiredService<ILogger<T>>();
        }

    }
}
