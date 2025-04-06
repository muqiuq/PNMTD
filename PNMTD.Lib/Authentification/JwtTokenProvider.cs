namespace PNMTD.Lib.Authentification
{
    public class JwtTokenProvider
    {
        public string JwtToken { get; private set; }

        public JwtTokenProvider(string jwtToken)
        {
            JwtToken = jwtToken;
        }
    }
}
