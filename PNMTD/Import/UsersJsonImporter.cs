using System.Text.Json;
using PNMTD.Auth;
using PNMTD.Lib.Helper;
using PNMTD.Lib.Security;
using PNMTD.Models.Db;

namespace PNMTD.Import;

/// <summary>
/// The WebApp's users.json (docs/webui-migration-plan.md §4.8), read for `import-sqlite --users`:
/// <list type="bullet">
/// <item>A JSON array of <c>{ Id, Username, Name, IsAdmin, PasswordHash, Salt }</c>, as WebAppUserManager
///   wrote it.</item>
/// <item>The oldest format, one user without Id and Name, becomes an admin named like its username
///   (as WebAppUserManager migrated it).</item>
/// <item>The hashes convert without the passwords (<see cref="PasswordHasher.FromWebAppHash"/>) and
///   are upgraded at each user's next sign-in.</item>
/// <item>The default user pnmt that still has the password pnmt must change it at the first sign-in.</item>
/// </list>
/// </summary>
public static class UsersJsonImporter
{
    private sealed class WebAppUser
    {
        public Guid Id { get; set; }
        public string? Username { get; set; }
        public string? Name { get; set; }
        public bool IsAdmin { get; set; }
        public string? PasswordHash { get; set; }
        public string? Salt { get; set; }
    }

    public sealed record ImportedUser(UserEntity User, string Note);

    public static List<ImportedUser> Read(string path)
    {
        if (!File.Exists(path))
        {
            throw new ImportException($"--users: {path} does not exist.");
        }
        List<WebAppUser> users;
        try
        {
            users = JsonSerializer.Deserialize<List<WebAppUser>>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new ImportException("--users: the file is empty.");
        }
        catch (JsonException ex)
        {
            throw new ImportException($"--users: not a users.json of the WebApp ({ex.Message}).");
        }
        if (users.Count == 1 && users[0].Id == Guid.Empty && string.IsNullOrEmpty(users[0].Name))
        {
            users[0].Id = Guid.NewGuid();
            users[0].Name = users[0].Username;
            users[0].IsAdmin = true;
        }

        var now = Clock.UtcNow;
        var result = new List<ImportedUser>();
        foreach (var u in users)
        {
            if (string.IsNullOrWhiteSpace(u.Username) || string.IsNullOrEmpty(u.PasswordHash) || string.IsNullOrEmpty(u.Salt))
            {
                throw new ImportException($"--users: a user without a username, hash or salt ({u.Username ?? u.Id.ToString()}).");
            }
            string hash;
            try
            {
                hash = PasswordHasher.FromWebAppHash(u.Salt, u.PasswordHash);
            }
            catch (FormatException)
            {
                throw new ImportException($"--users: the hash of {u.Username} is not Base64.");
            }
            var username = u.Username.Trim();
            var defaultPassword = UserAccounts.Normalize(username) == "pnmt" && PasswordHasher.Verify("pnmt", hash);
            var user = new UserEntity
            {
                Id = u.Id == Guid.Empty ? Guid.NewGuid() : u.Id,
                Username = username,
                NormalizedUsername = UserAccounts.Normalize(username),
                DisplayName = string.IsNullOrWhiteSpace(u.Name) ? username : u.Name.Trim(),
                PasswordHash = hash,
                Role = u.IsAdmin ? Roles.Admin : Roles.User,
                IsActive = true,
                MustChangePassword = defaultPassword,
                SecurityStamp = UserAccounts.NewStamp(),
                CreatedAt = now,
            };
            result.Add(new ImportedUser(user, defaultPassword ? "must change the default password pnmt" : "keeps its password"));
        }
        var duplicate = result.GroupBy(r => r.User.NormalizedUsername).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw new ImportException($"--users: {duplicate.Count()} users are named {duplicate.Key} (names are no longer case-sensitive). Rename one in users.json.");
        }
        if (result.GroupBy(r => r.User.Id).Any(g => g.Count() > 1))
        {
            throw new ImportException("--users: two users share an Id.");
        }
        return result;
    }
}
