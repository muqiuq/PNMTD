using System.Globalization;
using Microsoft.EntityFrameworkCore;
using PNMTD.Api;
using PNMTD.Auth;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Models.Db;

namespace PNMTD.Cli;

/// <summary>
/// `pnmtd users …` (docs/webui-migration-plan.md §4.10), behind deploy/add-user.sh, list-users.sh and
/// reset-password.sh. Changes are logged with the origin "cli". A running server notices ended
/// sessions within a minute (its session cache).
/// </summary>
internal static class UsersCommand
{
    public static int Run(string[] args, Func<PnmtdDbContext> database, ILogger logger)
    {
        using var db = database();
        DatabaseMigrator.Migrate(db, logger);
        var accounts = new UserAccounts(db);
        try
        {
            return args switch
            {
                ["list"] => List(db),
                ["add", var name, .. var rest] => Add(accounts, name, new CommandLine.Options(rest)),
                ["reset-password", var name, .. var rest] => ResetPassword(accounts, name, new CommandLine.Options(rest)),
                ["unlock", var name] => Unlock(accounts, name),
                ["set-role", var name, var role] => SetRole(accounts, name, role),
                _ => throw new CommandLineException($"Unknown users command: {string.Join(' ', args)}"),
            };
        }
        catch (ApiException ex)
        {
            Console.Error.WriteLine(ex.Message);
            foreach (var (field, messages) in ex.Errors ?? new Dictionary<string, string[]>())
            {
                Console.Error.WriteLine($"  {field}: {string.Join(" ", messages)}");
            }
            return 1;
        }
    }

    private static int List(PnmtdDbContext db)
    {
        var users = db.Users.AsNoTracking().OrderBy(u => u.NormalizedUsername).ToList();
        var now = Clock.UtcNow;
        var rows = users.Select(u => new[]
        {
            u.Username,
            u.Role,
            u.IsActive ? "active" : "inactive",
            u.IsTotpEnabled ? "2FA" : "-",
            u.LockoutEnd is { } end && end > now ? $"locked until {end:yyyy-MM-dd HH:mm} UTC" : u.MustChangePassword ? "must change password" : "-",
            u.LastLoginAt is { } last ? $"{last:yyyy-MM-dd HH:mm} UTC" : "never",
            u.DisplayName,
        }).ToList();
        var header = new[] { "USERNAME", "ROLE", "STATE", "2FA", "NOTE", "LAST SIGN-IN", "NAME" };
        var widths = header.Select((h, i) => rows.Select(r => r[i].Length).Append(h.Length).Max()).ToArray();
        foreach (var row in rows.Prepend(header))
        {
            Console.WriteLine(string.Join("  ", row.Select((cell, i) => i == row.Length - 1 ? cell : cell.PadRight(widths[i]))).TrimEnd());
        }
        Console.WriteLine($"{users.Count} user(s).");
        return 0;
    }

    private static int Add(UserAccounts accounts, string name, CommandLine.Options options)
    {
        var role = options.Optional("--role") ?? Roles.User;
        var displayName = options.Optional("--display-name");
        var fromStdin = options.Flag("--password-stdin");
        options.EnsureAllUsed();
        var (user, temporary) = accounts.CreateAsync(name, displayName, role, fromStdin ? ReadPassword() : null, AuditContext.Cli)
            .GetAwaiter().GetResult();
        Console.WriteLine($"Created {user.Username} ({user.Role}).");
        PrintTemporary(user, temporary);
        return 0;
    }

    private static int ResetPassword(UserAccounts accounts, string name, CommandLine.Options options)
    {
        var fromStdin = options.Flag("--password-stdin");
        options.EnsureAllUsed();
        var user = Find(accounts, name);
        var temporary = accounts.ResetPasswordAsync(user.Id, fromStdin ? ReadPassword() : null, AuditContext.Cli).GetAwaiter().GetResult();
        Console.WriteLine($"Reset the password of {user.Username}: their sessions end, a lockout is lifted.");
        PrintTemporary(user, temporary);
        return 0;
    }

    private static int Unlock(UserAccounts accounts, string name)
    {
        var user = Find(accounts, name);
        accounts.UnlockAsync(user.Id, AuditContext.Cli).GetAwaiter().GetResult();
        Console.WriteLine($"Unlocked {user.Username}.");
        return 0;
    }

    private static int SetRole(UserAccounts accounts, string name, string role)
    {
        var user = Find(accounts, name);
        var updated = accounts.UpdateAsync(user.Id, user.DisplayName, role, user.IsActive, AuditContext.Cli).GetAwaiter().GetResult();
        Console.WriteLine($"{updated.Username} is {updated.Role} now; their sessions end.");
        return 0;
    }

    private static UserEntity Find(UserAccounts accounts, string name) =>
        accounts.FindAsync(name).GetAwaiter().GetResult() ?? throw new ApiException(404, ApiErrorCodes.NotFound, $"No user {name}.");

    private static string ReadPassword() =>
        Console.In.ReadLine() is { Length: > 0 } line ? line : throw new CommandLineException("--password-stdin: no password on standard input.");

    private static void PrintTemporary(UserEntity user, string? temporary)
    {
        if (temporary is null)
        {
            return;
        }
        var until = user.TemporaryPasswordExpiresAt!.Value.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        Console.WriteLine($"Temporary password, shown only now (valid until {until} UTC, to be changed at the first sign-in):");
        Console.WriteLine();
        Console.WriteLine($"    {temporary}");
        Console.WriteLine();
    }
}
