using System.Net;
using MailKit.Net.Pop3;
using Microsoft.Extensions.Configuration;
using NLua;
using PNMTD.Helper;
using System.Text;
using System.Text.RegularExpressions;

namespace PNMTD.DevConsole
{
    internal class Program
    {
        static void Main(string[] args)
        {
            var Configuration = ConfigurationHelper.InitConfiguration();

            if (IPAddress.TryParse("::ffff:178.197.218.9", out var ip))
            {
                Console.WriteLine(ip);
            }

            string pattern1 = @"::ffff:\d+\.\d+\.\d+\.\d+\:\d+";
            var rg1 = new Regex(pattern1);
            var m1 = rg1.Match("::ffff:178.197.218.9:0");
            Console.WriteLine(m1);
        }

        public static void LuaTest()
        {
            using (Lua lua = new Lua())
            {
                lua.State.Encoding = Encoding.UTF8;
                var state = lua;
                state.DoString(@"
	                function ScriptFunc (val1, val2)
		                if val1 == val2 then
			                return false
		                else
			                return true
		                end
	                end
	                ");
                var scriptFunc = state["ScriptFunc"] as LuaFunction;

                var res = scriptFunc.Call(5, 5).First();
                if (res.GetType() == typeof(bool))
                {
                    Console.WriteLine("OK");
                }
                else
                {
                    Console.WriteLine("Invalid return type");
                }

                Console.WriteLine(res);

            }
        }

        public static void MailTest(IConfiguration Configuration)
        {
            var email = Configuration["mailbox:email"];
            var host = Configuration["mailbox:host"];
            var password = Configuration["mailbox:password"];

            using (var client = new Pop3Client())
            {
                client.Connect(host, 995, true);

                client.Authenticate(email, password);

                Console.WriteLine("Found: {0}", client.Count);

                for (int i = 0; i < client.Count; i++)
                {
                    var message = client.GetMessage(i);
                    Console.WriteLine("Subject: {0}", message.Subject);
                    if (message.Date < DateTime.Now.AddDays(-30))
                    {
                        Console.WriteLine("Message is older then 30 days");
                    }
                    else
                    {
                        Console.WriteLine("Message is NOT older then 30 days");
                    }
                    client.DeleteMessage(i);
                }

                client.Disconnect(true);
            }
        }

        public static void RegexTest()
        {
            string regexStr = @"OK";

            Regex rx = new Regex(regexStr, RegexOptions.Compiled | RegexOptions.IgnoreCase);

            string text = "OK";

            // Find matches.
            MatchCollection matches = rx.Matches(text);

            // Report the number of matches found.
            Console.WriteLine("{0} matches found in:\n   {1}",
                              matches.Count,
                              text);

            foreach (Match match in matches)
            {
                GroupCollection groups = match.Groups;
                Console.WriteLine("'{0}' repeated at positions {1} and {2}",
                                  groups["word"].Value,
                                  groups[0].Index,
                                  groups[1].Index);
            }
        }
    }
}