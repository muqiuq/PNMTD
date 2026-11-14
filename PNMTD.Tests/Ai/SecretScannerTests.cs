using PNMTD.Ai;

namespace PNMTD.Tests.Ai;

/// <summary>The credentials scan (docs/locations-knowledge-plan.md §7.3, §12): every pattern, and what must pass.</summary>
[TestClass]
public sealed class SecretScannerTests
{
    [TestMethod]
    [DataRow("-----BEGIN OPENSSH PRIVATE KEY-----", SecretKinds.PrivateKey)]
    [DataRow("-----BEGIN RSA PRIVATE KEY-----", SecretKinds.PrivateKey)]
    [DataRow("PuTTY-User-Key-File-3: ssh-ed25519", SecretKinds.PrivateKey)]
    [DataRow("password: Sommer2024!", SecretKinds.Password)]
    [DataRow("Old password = hunter22", SecretKinds.Password)]
    [DataRow("MYSQL_ROOT_PASSWORD=s3cr3t-value", SecretKinds.Password)]
    [DataRow("  \"password\": \"abc123\",", SecretKinds.Password)]
    [DataRow("db_password: 'correct horse battery staple'", SecretKinds.Password)]
    [DataRow("**Passwort:** Winter2025", SecretKinds.Password)]
    [DataRow("Kennwort = Geh31m!", SecretKinds.Password)]
    [DataRow("passphrase: hunter2 # the old one", SecretKinds.Password)]
    [DataRow("| Wi-Fi password | Sommer2024! |", SecretKinds.Password)]
    [DataRow("Backups go to https://backup:Xy7-pass@nas.example.com/share", SecretKinds.Password)]
    [DataRow("token ghp_aBcDeFgHiJkLmNoPqRsTuVwXyZ0123456789", SecretKinds.Token)]
    [DataRow("github_pat_11ABCDEFG0123456789_abcdefghijklmnopqrstuvwxyz", SecretKinds.Token)]
    [DataRow("export GITLAB=glpat-AbCdEfGhIjKlMnOpQrSt", SecretKinds.Token)]
    [DataRow("SLACK_BOT=xoxb-1234567890-abcdefghij", SecretKinds.Token)]
    [DataRow("aws_access_key_id = AKIAIOSFODNN7EXAMPLE", SecretKinds.Token)]
    [DataRow("OPENROUTER=sk-or-v1-0123456789abcdef0123456789abcdef", SecretKinds.Token)]
    [DataRow("stripe sk_live_4eC39HqLyjWDarjtT1zdp7dc", SecretKinds.Token)]
    [DataRow("eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dozjgNryP4J3jVmNHl0w5N_XgL0n3I9PlFUP0THsR8U", SecretKinds.Jwt)]
    [DataRow("curl -H 'Authorization: Bearer abcdef0123456789'", SecretKinds.Authorization)]
    [DataRow("X-Api-Key: 9f8e7d6c5b4a39281706", SecretKinds.Authorization)]
    [DataRow("API key: Zx81Qw9LmN4pR7sT2vB6yK3d", SecretKinds.Secret)]
    [DataRow("client secret = \"k3J9sL2mQ8xV5nB7cR4tZ1wA\"", SecretKinds.Secret)]
    [DataRow("Schlüssel: Qm93Lp28Xz47Rt65Vy13Nb", SecretKinds.Secret)]
    public void A_credential_is_found_with_its_line_and_kind(string line, string kind)
    {
        var findings = SecretScanner.Scan($"# Notes\n\n{line}\n\nThe end.");
        Assert.AreEqual(1, findings.Count, line);
        Assert.AreEqual(new SecretFinding(3, kind), findings[0], line);
    }

    [TestMethod]
    public void A_page_about_the_password_policy_passes()
    {
        Assert.AreEqual(0, SecretScanner.Scan("""
            ## Password policy

            Passwords must have at least 12 characters, and a password is changed every 90 days.
            Password: see the password manager, entry "ZRH DC".
            **Passwort:** steht im Passwort-Manager.
            Minimum password length = 12
            Password policy: 12+ characters, no reuse.
            passwordless: true
            | Field | Value |
            |---|---|
            | Password | (in the vault) |
            | Admin password | ******** |
            Never write a token, a key or a secret into a page. PNMT is not a password manager.
            The API key goes into the `Authorization:` header.
            """).Count);
    }

    [TestMethod]
    public void Placeholders_variables_and_password_files_pass()
    {
        Assert.AreEqual(0, SecretScanner.Scan("""
            pg_dump --password-file /run/secrets/db --username pnmtd
            restic --password-file=/etc/restic/pass backup /srv
            password_file: /run/secrets/db
            password: <your password>
            password: ${DB_PASSWORD}
            PGPASSWORD=$PGPASS psql
            "password": "{{ vault_db_password }}"
            password = ""
            password: [redacted]
            password: none
            Kennwort: ***
            DATABASE_URL=postgres://pnmtd:${PASSWORD}@db/pnmtd
            Authorization: Bearer <token>
            """).Count);
    }

    [TestMethod]
    public void Checksums_public_keys_fingerprints_and_ids_pass()
    {
        Assert.AreEqual(0, SecretScanner.Scan("""
            ## SHA-256 checksums
            e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855  pnmt-agent-linux-x64.tar.gz
            9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08  pnmt-agent-windows-x64.zip
            Public key: ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIOMqqnkVzrm0SdG6UOoqKLsabgH5C9okWi0dh2l9GKJl philipp@laptop
            The key's fingerprint: SHA256:Kq1fXa9kY2Zt3Hn4Bp7mWc6Vd8Lr0Es5Gu1Ij2Ok3Pq
            key 3f2a9c1e-1111-4222-8333-444455556666 is the host's id
            -----BEGIN PUBLIC KEY-----
            MIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8AMIIBCgKCAQEAu1SU1LfVLPHCozMxH2Mo
            -----END PUBLIC KEY-----
            Token rotation runs every Sunday at 03:00; see KB-12 for the procedure-with-a-long-name.
            The release tag is v1.2.3-rc.1 and the image is ghcr.io/example/pnmt:2026.11.14.
            """).Count);
    }

    [TestMethod]
    public void A_private_key_is_one_finding_and_lines_are_counted_from_one()
    {
        var findings = SecretScanner.Scan("intro\r\n-----BEGIN OPENSSH PRIVATE KEY-----\r\nb3BlbnNzaC1rZXktdjEAAAAABG5vbmUAAAAEbm9uZQ\r\n-----END OPENSSH PRIVATE KEY-----\r\npassword: hunter22");
        CollectionAssert.AreEqual(new[] { new SecretFinding(2, SecretKinds.PrivateKey), new SecretFinding(5, SecretKinds.Password) }, findings.ToArray());
        Assert.AreEqual(0, SecretScanner.Scan(null).Count);
        Assert.AreEqual(0, SecretScanner.Scan(new string('a', 100_000)).Count);
    }
}
