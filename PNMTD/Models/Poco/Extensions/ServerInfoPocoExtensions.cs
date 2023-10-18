using PNMTD.Lib.Models.Poco;

namespace PNMTD.Models.Poco.Extensions
{
    public static class ServerInfoPocoExtensions
    {
        public static ServerInfoPoco ToPoco(this ServerInfo versionInfo)
        {
            return new ServerInfoPoco()
            {
                Name = versionInfo.Name,
                Version = versionInfo.Version,
                Identity = versionInfo.Identity,
            };
        }

    }
}
