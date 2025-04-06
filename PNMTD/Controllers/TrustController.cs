using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore.Metadata.Internal;
using PNMTD.Lib.Authentification;
using PNMTD.Models.Helper;
using PNMTD.Models.Poco;
using System.Runtime.InteropServices;

namespace PNMTD.Controllers
{
    [Route("trust")]
    [ApiController]
    public class TrustController : Controller
    {
        private readonly JwtTokenConfig jwtTokenConfig;

        public TrustController(JwtTokenConfig jwtTokenConfig)
        {
            this.jwtTokenConfig = jwtTokenConfig;
        }
        
        [AllowAnonymous]
        [HttpGet("create-web-app-token")]
        public object CreateWebAppToken()
        {
            var basePath = "";
            if (!Global.IsDevelopment && RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                basePath = GlobalConfiguration.LinuxBasePath;
            }
            var webAppTokenPath = Path.Combine( basePath, "create-web-app-token.txt");
            if (System.IO.File.Exists(webAppTokenPath))
            {
                return Unauthorized();
            }
            var token = JwtTokenHelper.GenerateNewToken("pnmt.webapp", jwtTokenConfig.Issuer, jwtTokenConfig.Audience, jwtTokenConfig.Key, 240);
            System.IO.File.WriteAllText(webAppTokenPath, token);
            return token;
        }
    }
}
