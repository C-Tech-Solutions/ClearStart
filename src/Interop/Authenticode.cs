using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Win32.SafeHandles;

namespace ClearStart;

/// <summary>What Windows makes of a program file's signature.</summary>
/// <param name="Valid">Signed, unchanged since, and by a certificate Windows trusts for signing code.</param>
/// <param name="Subject">The signer's full name, as its certificate gives it; null when there is no valid signature.</param>
/// <param name="Name">The signer's common name, "C-Tech Solutions LLC" for example.</param>
/// <param name="Problem">Why it is not valid, in a few words; null when it is.</param>
internal sealed record SignatureInfo(bool Valid, string? Subject, string? Name, string? Problem);

/// <summary>
/// Checks Authenticode signatures with WinVerifyTrust, the same check Windows makes before it runs a
/// downloaded program, and reads who signed the file from what that check verified.
/// </summary>
internal static class Authenticode
{
    /// <param name="path">The file to check.</param>
    /// <param name="file">An open handle to the same file. Holding it open without sharing write access keeps
    /// the file from changing between this check and whatever is done with it next.</param>
    /// <param name="online">Also ask whether any certificate in the chain has been revoked, which goes online. Without
    /// it nothing is fetched, and the answer comes from what this PC already has.</param>
    public static SignatureInfo Check(string path, SafeFileHandle? file = null, bool online = false)
    {
        var fileInfo = new WINTRUST_FILE_INFO
        {
            cbStruct = Marshal.SizeOf<WINTRUST_FILE_INFO>(),
            pcwszFilePath = path,
            hFile = file?.DangerousGetHandle() ?? IntPtr.Zero,
            pgKnownSubject = IntPtr.Zero,
        };
        IntPtr filePointer = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
        Marshal.StructureToPtr(fileInfo, filePointer, false);
        var data = new WINTRUST_DATA
        {
            cbStruct = Marshal.SizeOf<WINTRUST_DATA>(),
            dwUIChoice = WTD_UI_NONE,
            fdwRevocationChecks = online ? WTD_REVOKE_WHOLECHAIN : WTD_REVOKE_NONE,
            dwUnionChoice = WTD_CHOICE_FILE,
            pFile = filePointer,
            dwStateAction = WTD_STATEACTION_VERIFY,
            dwProvFlags = online
                ? WTD_REVOCATION_CHECK_CHAIN_EXCLUDE_ROOT
                : WTD_REVOCATION_CHECK_NONE | WTD_CACHE_ONLY_URL_RETRIEVAL,
        };

        bool addedRef = false;
        file?.DangerousAddRef(ref addedRef);
        var action = WINTRUST_ACTION_GENERIC_VERIFY_V2;
        try
        {
            int result = WinVerifyTrust(new IntPtr(-1), ref action, ref data);
            if (result != 0) return new SignatureInfo(false, null, null, Describe(result));

            using var signer = SignerOf(data.hWVTStateData);
            if (signer == null) return new SignatureInfo(false, null, null, "the signer could not be read");
            return new SignatureInfo(true, signer.SubjectName.Name, signer.GetNameInfo(X509NameType.SimpleName, false), null);
        }
        finally
        {
            data.dwStateAction = WTD_STATEACTION_CLOSE;
            WinVerifyTrust(new IntPtr(-1), ref action, ref data);
            Marshal.DestroyStructure<WINTRUST_FILE_INFO>(filePointer);
            Marshal.FreeHGlobal(filePointer);
            if (addedRef) file!.DangerousRelease();
        }
    }

    /// <summary>The certificate that made the file's signature, from the verified state WinVerifyTrust left behind.</summary>
    private static X509Certificate2? SignerOf(IntPtr state)
    {
        IntPtr provider = WTHelperProvDataFromStateData(state);
        if (provider == IntPtr.Zero) return null;
        IntPtr signer = WTHelperGetProvSignerFromChain(provider, 0, false, 0);
        if (signer == IntPtr.Zero) return null;
        IntPtr cert = WTHelperGetProvCertFromChain(signer, 0);
        if (cert == IntPtr.Zero) return null;
        // CRYPT_PROVIDER_CERT starts with its size (a DWORD) and then the certificate, aligned to a pointer.
        IntPtr context = Marshal.ReadIntPtr(cert, IntPtr.Size);
        return context == IntPtr.Zero ? null : new X509Certificate2(context);
    }

    private static string Describe(int result) => unchecked((uint)result) switch
    {
        0x800B0100 => "it is not signed",
        0x800B0003 => "it is not a signed program file",
        0x80096010 => "it has been changed since it was signed",
        0x800B0109 => "it is signed by a certificate Windows does not trust",
        0x800B010C => "the certificate it is signed with has been revoked",
        0x800B010E => "Windows could not check whether its certificate has been revoked",
        0x80092013 => "the server that says whether its certificate is still good could not be reached",
        0x800B010A => "its certificate does not lead back to one Windows trusts",
        0x800B0101 => "its certificate has expired",
        0x800B0111 => "its signer is blocked on this PC",
        0x800B0110 => "its certificate is not meant for signing programs",
        _ => $"Windows did not accept its signature (0x{result:X8})",
    };

    // ------------------------------------------------------------------ wintrust.dll

    private static readonly Guid WINTRUST_ACTION_GENERIC_VERIFY_V2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    private const int WTD_UI_NONE = 2;
    private const int WTD_REVOKE_NONE = 0;
    private const int WTD_REVOKE_WHOLECHAIN = 1;
    private const int WTD_CHOICE_FILE = 1;
    private const int WTD_STATEACTION_VERIFY = 1;
    private const int WTD_STATEACTION_CLOSE = 2;
    private const int WTD_REVOCATION_CHECK_NONE = 0x10;
    private const int WTD_REVOCATION_CHECK_CHAIN_EXCLUDE_ROOT = 0x80;
    private const int WTD_CACHE_ONLY_URL_RETRIEVAL = 0x1000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WINTRUST_FILE_INFO
    {
        public int cbStruct;
        [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_DATA
    {
        public int cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public int dwUIChoice;
        public int fdwRevocationChecks;
        public int dwUnionChoice;
        public IntPtr pFile;
        public int dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public int dwProvFlags;
        public int dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid action, ref WINTRUST_DATA data);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern IntPtr WTHelperProvDataFromStateData(IntPtr state);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern IntPtr WTHelperGetProvSignerFromChain(IntPtr provider, int signer,
        [MarshalAs(UnmanagedType.Bool)] bool counterSigner, int counterSignerIndex);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern IntPtr WTHelperGetProvCertFromChain(IntPtr signer, int cert);
}
