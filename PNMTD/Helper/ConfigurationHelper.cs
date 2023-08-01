using System.Reflection;

namespace PNMTD.Helper
{
    public static class ConfigurationHelper
    {

        public static IConfiguration InitConfiguration()
        {
            var config = new ConfigurationBuilder()
               .AddJsonFile("appsettings.Development.json")
               .AddUserSecrets(Assembly.GetExecutingAssembly(), true)
               .AddEnvironmentVariables()
               .Build();
            return config;
        }

    }
}
