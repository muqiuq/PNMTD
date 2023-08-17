using System.Reflection;

namespace PNMTD.Helper
{
    public static class ConfigurationHelper
    {

        public static IConfiguration InitConfiguration(bool useMain = false)
        {
            var assemblyForUserSecrets = Assembly.GetCallingAssembly();
            if(useMain )
            {
                assemblyForUserSecrets = Assembly.GetExecutingAssembly();
            }
            var config = new ConfigurationBuilder()
               .AddJsonFile("appsettings.Development.json")
               .AddUserSecrets(assemblyForUserSecrets, true)
               .AddEnvironmentVariables()
               .Build();
            return config;
        }

    }
}
