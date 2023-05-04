namespace PNMTD
{
    public class Global
    {

        public static bool IsDevelopment 
        { 
            get
            {
                return Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") == "Development";
            } 
        }

    }
}
