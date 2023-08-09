using Microsoft.AspNetCore.Mvc;

namespace PNMTD.Helper
{
    public static class RequestHelper
    {

        public static string GetRemoteIpAddressOrDefault(this ControllerBase controller)
        {
            var remoteIpAdress = controller.HttpContext.Connection.RemoteIpAddress;
            var remotePort = controller.HttpContext.Connection.RemotePort;
            if (remoteIpAdress == null)
            {
                return "0.0.0.0";
            }
            return $"{remoteIpAdress}:{remotePort}";
        }

    }
}
