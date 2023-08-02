using PNMTD.Lib;
using System.CommandLine;

namespace PNMTD.Cli
{
    internal class Program
    {
        static int Main(string[] args)
        {

            var rootCommand = new RootCommand("PNMTD Tool");

            var usernameOption = new Option<string>(
               name: "--username",
               description: "Username");

            var audienceOption = new Option<string>(
               name: "--audience",
               description: "Audience");

            var issuerOption = new Option<string>(
               name: "--issuer",
               description: "Issuer");

            var keyOption = new Option<string>(
               name: "--key",
               description: "Key");

            var jwtCommand = new Command("jwt", "JWT")
            {
                usernameOption,
                audienceOption,
                issuerOption,
                keyOption
            };

            rootCommand.AddCommand(jwtCommand);

            jwtCommand.SetHandler((username, audience, issuer, key) =>
            {
                if(username == null || audience == null || issuer == null || key == null) {
                    Console.WriteLine("Missing options");
                    return;
                }
                var token = JwtTokenHelper.GenerateNewToken(username, issuer, audience, key);
                Console.WriteLine(token);
            },
            usernameOption, audienceOption, issuerOption, keyOption);

            return rootCommand.InvokeAsync(args).Result;
        }
    }
}