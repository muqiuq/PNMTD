using System.Reflection;

namespace PNMTD
{
    public class ServerInfo
    {
        public static string _Identity = "no identity set";
        
        public string Version
        {
            get
            {
                return Assembly.GetEntryAssembly().GetName().Version.ToString();
            }
        }

        public string Name
        {
            get
            {
                return Assembly.GetEntryAssembly().GetName().Name ?? "PNMTD";
            }
        }

        public string Identity
        {
            get
            {
                return _Identity;
            }
        }

    }
}
