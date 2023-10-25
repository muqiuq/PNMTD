using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PNMTD.Services.ApiOutputFormat
{
    public class ApiOutputFormatter
    {

        public static string FormatOutput(List<KeyValuePair<string, object>> data, string format)
        {
            if (format == "property")
            {
                StringBuilder sb = new StringBuilder();
                foreach (var pair in data)
                {
                    sb.AppendLine($"{pair.Key} = {pair.Value}");
                }
                return sb.ToString();
            }
            else if (format == "raw")
            {
                return string.Join(";", data.Select(d => d.Value).ToList());
            }
            // defaults to json
            return JsonSerializer.Serialize(
                data.ToDictionary(x => x.Key, x => x.Value));
        }

    }
}
