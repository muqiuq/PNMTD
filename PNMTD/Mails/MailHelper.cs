using MimeKit;
using System.Text;
using System.Text.RegularExpressions;

namespace PNMTD.Mails
{
    public static class MailHelper
    {
        public static string ExtractMailAdresses(MimeKit.InternetAddressList addr)
        {
            StringBuilder sb = new StringBuilder();
            foreach (var mb in addr.Mailboxes)
            {
                if (sb.Length != 0)
                {
                    sb.Append(", ");
                }
                sb.Append(mb.Address);
            }
            return sb.ToString().Trim();
        }

        static string StripHTML(string inputString)
        {
            return Regex.Replace
              (inputString, "<.*?>", string.Empty);
        }

        internal static string ExtractBodyText(MimeMessage message)
        {
            var textPlain = message.BodyParts.Where(bp => bp.ContentType.MimeType == "text/plain").FirstOrDefault();
            if (textPlain != null && textPlain is MimeKit.TextPart)
            {
                return ((MimeKit.TextPart)textPlain).Text;
            }
            var textHtml = message.BodyParts.Where(bp => bp.ContentType.MimeType == "text/html").FirstOrDefault();
            if(textHtml != null)
            {
                return StripHTML(textHtml.ToString());
            }
            return "";

        }
    }
}
