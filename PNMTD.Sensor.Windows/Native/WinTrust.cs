using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace PNMTD.Sensor.Windows.Native;

/// <summary>
/// Authenticode (docs/windows-sensor-plan.md §4.5): whether a file is signed, embedded or by a catalog (most of Windows' own
/// files), checked with <c>WinVerifyTrust</c> without network access, and by whom.
/// </summary>
internal static class WinTrust
{
    public sealed record Signature(bool Valid, bool Catalog, string? Signer);

    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    private const uint UiNone = 2;
    private const uint RevocationNone = 0;
    private const uint ChoiceFile = 1;
    private const uint ChoiceCatalog = 2;
    private const uint StateVerify = 1;
    private const uint StateClose = 2;
    private const uint CacheOnlyUrlRetrieval = 0x1000;
    private const uint RevocationCheckNone = 0x10;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo
    {
        public uint Size;
        public string FilePath;
        public IntPtr File;
        public IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustCatalogInfo
    {
        public uint Size;
        public uint CatalogVersion;
        public string CatalogFilePath;
        public string MemberTag;
        public string MemberFilePath;
        public IntPtr MemberFile;
        public IntPtr CalculatedFileHash;
        public uint CalculatedFileHashSize;
        public IntPtr CatalogContext;
        public IntPtr CatAdmin;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        public uint Size;
        public IntPtr PolicyCallbackData;
        public IntPtr SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public IntPtr Info;
        public uint StateAction;
        public IntPtr StateData;
        public IntPtr UrlReference;
        public uint ProviderFlags;
        public uint UiContext;
        public IntPtr SignatureSettings;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CatalogInfo
    {
        public uint Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string CatalogFile;
    }

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(IntPtr window, [In] ref Guid action, [In] ref WinTrustData data);

    [DllImport("wintrust.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptCATAdminAcquireContext2(out IntPtr catAdmin, IntPtr subsystem, string hashAlgorithm, IntPtr strongHashPolicy, uint flags);

    [DllImport("wintrust.dll", SetLastError = true)]
    private static extern bool CryptCATAdminCalcHashFromFileHandle2(IntPtr catAdmin, IntPtr file, ref uint hashSize, byte[]? hash, uint flags);

    [DllImport("wintrust.dll", SetLastError = true)]
    private static extern IntPtr CryptCATAdminEnumCatalogFromHash(IntPtr catAdmin, byte[] hash, uint hashSize, uint flags, ref IntPtr previous);

    [DllImport("wintrust.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptCATCatalogInfoFromContext(IntPtr catInfo, ref CatalogInfo info, uint flags);

    [DllImport("wintrust.dll", SetLastError = true)]
    private static extern bool CryptCATAdminReleaseCatalogContext(IntPtr catAdmin, IntPtr catInfo, uint flags);

    [DllImport("wintrust.dll", SetLastError = true)]
    private static extern bool CryptCATAdminReleaseContext(IntPtr catAdmin, uint flags);

    public static Signature Check(string path)
    {
        if (!File.Exists(path))
        {
            return new Signature(false, false, null);
        }
        if (VerifyEmbedded(path))
        {
            return new Signature(true, false, Signer(path));
        }
        return VerifyCatalog(path) ? new Signature(true, true, "Microsoft Windows (catalog)") : new Signature(false, false, Signer(path));
    }

    private static string? Signer(string path)
    {
        try
        {
#pragma warning disable SYSLIB0057 // CreateFromSignedFile reads the Authenticode signer; there is no replacement for it.
            var certificate = X509Certificate.CreateFromSignedFile(path);
#pragma warning restore SYSLIB0057
            var subject = certificate.Subject;
            var cn = subject.Split(',').Select(p => p.Trim()).FirstOrDefault(p => p.StartsWith("CN=", StringComparison.Ordinal));
            return cn?[3..].Trim('"') ?? subject;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool VerifyEmbedded(string path)
    {
        var file = new WinTrustFileInfo { Size = (uint)Marshal.SizeOf<WinTrustFileInfo>(), FilePath = path };
        var filePointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
        try
        {
            Marshal.StructureToPtr(file, filePointer, false);
            var data = Data(ChoiceFile, filePointer);
            var action = GenericVerifyV2;
            var result = WinVerifyTrust(new IntPtr(-1), ref action, ref data);
            data.StateAction = StateClose;
            WinVerifyTrust(new IntPtr(-1), ref action, ref data);
            return result == 0;
        }
        finally
        {
            Marshal.DestroyStructure<WinTrustFileInfo>(filePointer);
            Marshal.FreeHGlobal(filePointer);
        }
    }

    private static bool VerifyCatalog(string path)
    {
        if (!CryptCATAdminAcquireContext2(out var admin, IntPtr.Zero, "SHA256", IntPtr.Zero, 0))
        {
            return false;
        }
        var catalog = IntPtr.Zero;
        try
        {
            using var stream = File.OpenRead(path);
            var handle = stream.SafeFileHandle.DangerousGetHandle();
            uint size = 0;
            CryptCATAdminCalcHashFromFileHandle2(admin, handle, ref size, null, 0);
            if (size == 0)
            {
                return false;
            }
            var hash = new byte[size];
            if (!CryptCATAdminCalcHashFromFileHandle2(admin, handle, ref size, hash, 0))
            {
                return false;
            }
            var previous = IntPtr.Zero;
            catalog = CryptCATAdminEnumCatalogFromHash(admin, hash, size, 0, ref previous);
            if (catalog == IntPtr.Zero)
            {
                return false;
            }
            var info = new CatalogInfo { Size = (uint)Marshal.SizeOf<CatalogInfo>() };
            if (!CryptCATCatalogInfoFromContext(catalog, ref info, 0))
            {
                return false;
            }
            var member = new WinTrustCatalogInfo
            {
                Size = (uint)Marshal.SizeOf<WinTrustCatalogInfo>(),
                CatalogFilePath = info.CatalogFile,
                MemberTag = Convert.ToHexString(hash),
                MemberFilePath = path,
                CatAdmin = admin,
            };
            var memberPointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustCatalogInfo>());
            try
            {
                Marshal.StructureToPtr(member, memberPointer, false);
                var data = Data(ChoiceCatalog, memberPointer);
                var action = GenericVerifyV2;
                var result = WinVerifyTrust(new IntPtr(-1), ref action, ref data);
                data.StateAction = StateClose;
                WinVerifyTrust(new IntPtr(-1), ref action, ref data);
                return result == 0;
            }
            finally
            {
                Marshal.DestroyStructure<WinTrustCatalogInfo>(memberPointer);
                Marshal.FreeHGlobal(memberPointer);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            if (catalog != IntPtr.Zero)
            {
                CryptCATAdminReleaseCatalogContext(admin, catalog, 0);
            }
            CryptCATAdminReleaseContext(admin, 0);
        }
    }

    private static WinTrustData Data(uint choice, IntPtr info) => new()
    {
        Size = (uint)Marshal.SizeOf<WinTrustData>(),
        UiChoice = UiNone,
        RevocationChecks = RevocationNone,
        UnionChoice = choice,
        Info = info,
        StateAction = StateVerify,
        ProviderFlags = CacheOnlyUrlRetrieval | RevocationCheckNone,
    };
}

/// <summary>The effective audit policy (§4.7): <c>AuditQuerySystemPolicy</c> for subcategories by GUID.</summary>
internal static class AuditPolicy
{
    [StructLayout(LayoutKind.Sequential)]
    private struct AuditPolicyInformation
    {
        public Guid SubCategory;
        public uint AuditingInformation;
        public Guid Category;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AuditQuerySystemPolicy([In] Guid[] subCategories, uint count, out IntPtr policies);

    [DllImport("advapi32.dll")]
    private static extern void AuditFree(IntPtr buffer);

    /// <summary>Success and failure auditing per subcategory.</summary>
    public static Dictionary<Guid, (bool Success, bool Failure)> Query(IReadOnlyList<Guid> subcategories)
    {
        if (!AuditQuerySystemPolicy(subcategories.ToArray(), (uint)subcategories.Count, out var buffer))
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "AuditQuerySystemPolicy failed");
        }
        try
        {
            var size = Marshal.SizeOf<AuditPolicyInformation>();
            var result = new Dictionary<Guid, (bool, bool)>();
            for (var i = 0; i < subcategories.Count; i++)
            {
                var info = Marshal.PtrToStructure<AuditPolicyInformation>(buffer + i * size);
                result[info.SubCategory] = ((info.AuditingInformation & 1) != 0, (info.AuditingInformation & 2) != 0);
            }
            return result;
        }
        finally
        {
            AuditFree(buffer);
        }
    }
}
