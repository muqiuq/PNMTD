namespace PNMTD.Lib.Authentification
{
    public class JwtTokenProvider
    {
        public string JwtToken { get; }

        public JwtTokenProvider(bool isDevelopment)
        {
            if (isDevelopment)
            {
                JwtToken = JwtTokenHelper.GenerateNewToken("dev", "PNMTD", "PNMTD", "Development123456789");
            }
        }
    }
}
