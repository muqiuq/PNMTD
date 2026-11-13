using System.Text.RegularExpressions;
using PNMTD.Models.Db;

namespace PNMTD.Mails;

/// <summary>
/// The AI never writes regular expressions (docs/ai-operations-plan.md §6.3): it returns a subject template with
/// <c>{*}</c> for what varies, and phrases copied from the mail. This builds escaped, anchored expressions from them,
/// checks that they match the mail, and previews them against the last mails. Every match runs with a timeout.
/// </summary>
public static class RuleTemplate
{
    public static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(250);
    public const string Wildcard = "{*}";

    /// <summary>A match with the timeout; a timeout counts as no match.</summary>
    public static bool IsMatch(string input, string pattern)
    {
        try
        {
            return Regex.IsMatch(input, pattern, RegexOptions.None, Timeout);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    /// <summary><c>^address$</c>, or <c>@domain$</c>, escaped.</summary>
    public static string From(bool domain, string value) =>
        domain ? "@" + Regex.Escape(value.TrimStart('@').Trim()) + "$" : "^" + Regex.Escape(value.Trim()) + "$";

    /// <summary>The subject's template as an anchored expression: literals escaped, <c>{*}</c> a lazy run of anything.</summary>
    public static string Subject(string template)
    {
        var parts = template.Split(Wildcard);
        return "^" + string.Join(".+?", parts.Select(Regex.Escape)) + "$";
    }

    public static string Phrase(string phrase) => Regex.Escape(phrase);

    /// <summary>The message after a phrase: <c>escaped(phrase)\s*(?&lt;msg&gt;[^\r\n]{1,200})</c>.</summary>
    public static string MessageAfter(string phrase) => Regex.Escape(phrase) + @"\s*(?<msg>[^\r\n]{1,200})";

    /// <summary>The sender's address: the first of the From header's list.</summary>
    public static string SenderOf(string from) => from.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? from;

    public static string DomainOf(string address) => address.Contains('@') ? address[(address.LastIndexOf('@') + 1)..] : address;

    /// <summary>
    /// The rule's tests against the last mails: matches, how many senders other than <paramref name="sender"/> they
    /// come from, and examples. Too broad: more than 20 % of at least 20 mails, or more than 3 other senders.
    /// </summary>
    public static (int Checked, int Matches, int OtherSenders, bool TooBroad, IReadOnlyList<string> Examples) Preview(
        IReadOnlyList<MailLogEntity> mails, string fromTest, string? subjectTest, string? bodyTest, string sender)
    {
        var matched = mails.Where(m => IsMatch(m.From, fromTest)
            && (string.IsNullOrEmpty(subjectTest) || IsMatch(m.Subject, subjectTest))
            && (string.IsNullOrEmpty(bodyTest) || IsMatch(m.Content, bodyTest))).ToList();
        var others = matched.Select(m => SenderOf(m.From).ToLowerInvariant()).Distinct().Count(s => s != sender.ToLowerInvariant());
        var tooBroad = (mails.Count >= 20 && matched.Count > mails.Count * 0.2) || others > 3;
        return (mails.Count, matched.Count, others, tooBroad, matched.Select(m => $"{SenderOf(m.From)}: {m.Subject}").Distinct().Take(5).ToList());
    }
}
