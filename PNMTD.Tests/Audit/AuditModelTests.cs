using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;

namespace PNMTD.Tests.Audit;

/// <summary>
/// The audit can't record a secret by accident (docs/audit-redmine-zabbix-plan.md §3.4, §3.7): every entity is audited or
/// operational, and on audited ones every property whose name looks like a secret's is either a secret or plainly not.
/// </summary>
[TestClass]
public sealed class AuditModelTests
{
    private static readonly Regex SecretLike = new("token|secret|password|key|hash|protected|private|credential", RegexOptions.IgnoreCase);

    private static IReadOnlyList<Type> EntityTypes()
    {
        using var db = Data.PnmtdDbContext.Create("Host=localhost");
        return db.Model.GetEntityTypes().Where(t => !t.IsOwned()).Select(t => t.ClrType).Distinct().ToList();
    }

    [TestMethod]
    public void EveryEntityIsAuditedOrOperational()
    {
        var unclassified = EntityTypes()
            .Where(t => t.GetCustomAttribute<AuditedAttribute>() is null && t.GetCustomAttribute<NotAuditedAttribute>() is null)
            .Select(t => t.Name).ToList();
        Assert.AreEqual("", string.Join(", ", unclassified), "entities with neither [Audited] nor [NotAudited]");
    }

    [TestMethod]
    public void SecretLookingPropertiesOfAuditedEntitiesAreMarked()
    {
        var unmarked = EntityTypes().Where(t => t.GetCustomAttribute<AuditedAttribute>() is not null)
            .SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => (Type: t, Property: p)))
            .Where(x => SecretLike.IsMatch(x.Property.Name)
                && x.Property.GetCustomAttribute<AuditSecretAttribute>() is null
                && x.Property.GetCustomAttribute<AuditPlainAttribute>() is null
                && x.Property.GetCustomAttribute<AuditIgnoreAttribute>() is null
                && (x.Property.PropertyType.IsValueType || x.Property.PropertyType == typeof(string) || x.Property.PropertyType == typeof(byte[])))
            .Select(x => $"{x.Type.Name}.{x.Property.Name}").ToList();
        Assert.AreEqual("", string.Join("\n", unmarked), "mark them [AuditSecret], or [AuditPlain(\"why\")]");
    }
}
