using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Utility.Core.Models;

namespace Utility.Platform.Native;

/// <summary>Authenticode signature verification (WinVerifyTrust), SHA-256 hashing, and PE inspection.</summary>
public static class WinTrustInterop
{
    private static readonly Guid WintrustActionGenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
    private const uint WtdUiNone = 2;
    private const uint WtdRevokeWholeChain = 1;
    private const uint WtdChoiceFile = 1;
    private const uint WtdStateActionVerify = 1;
    private const uint WtdStateActionClose = 2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo
    {
        public uint cbStruct;
        public IntPtr pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustData
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = false, CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid pgActionId, ref WinTrustData pWvtData);

    public static DriverPackageInspection InspectLocalFile(string filePath)
    {
        var fullPath = Path.GetFullPath(filePath);
        var fi = new FileInfo(fullPath);
        var ext = fi.Extension;
        var sha256 = ComputeSha256Hex(fullPath);
        var (sigOk, sigStatus) = VerifyAuthenticode(fullPath);
        var (subject, issuer) = ReadSignerInfo(fullPath);
        var fvi = FileVersionInfo.GetVersionInfo(fullPath);
        var peArch = ReadPeArchitecture(fullPath);

        return new DriverPackageInspection(
            FilePath: fullPath,
            FileExtension: ext,
            FileSizeBytes: fi.Length,
            Sha256Hex: sha256,
            IsSignatureValid: sigOk,
            SignatureStatusDetail: sigStatus,
            SignerSubject: subject,
            SignerIssuer: issuer,
            ProductName: fvi.ProductName,
            FileDescription: fvi.FileDescription,
            FileVersion: fvi.FileVersion,
            CompanyName: fvi.CompanyName,
            PeArchitecture: peArch);
    }

    public static string ComputeSha256Hex(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        var hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash);
    }

    public static (bool IsValid, string Detail) VerifyAuthenticode(string filePath)
    {
        var pFilePath = Marshal.StringToCoTaskMemUni(filePath);
        var fileInfo = new WinTrustFileInfo
        {
            cbStruct = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
            pcwszFilePath = pFilePath,
            hFile = IntPtr.Zero,
            pgKnownSubject = IntPtr.Zero
        };

        var pFileInfo = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
        try
        {
            Marshal.StructureToPtr(fileInfo, pFileInfo, false);
            var data = new WinTrustData
            {
                cbStruct = (uint)Marshal.SizeOf<WinTrustData>(),
                dwUIChoice = WtdUiNone,
                fdwRevocationChecks = WtdRevokeWholeChain,
                dwUnionChoice = WtdChoiceFile,
                pFile = pFileInfo,
                dwStateAction = WtdStateActionVerify
            };

            var status = WinVerifyTrust(IntPtr.Zero, WintrustActionGenericVerifyV2, ref data);
            data.dwStateAction = WtdStateActionClose;
            WinVerifyTrust(IntPtr.Zero, WintrustActionGenericVerifyV2, ref data);

            return status == 0
                ? (true, "TRUST_E_OK (0x00000000)")
                : (false, $"WinVerifyTrust returned 0x{status:X8}");
        }
        finally
        {
            Marshal.FreeHGlobal(pFileInfo);
            Marshal.FreeCoTaskMem(pFilePath);
        }
    }

    private static (string? Subject, string? Issuer) ReadSignerInfo(string filePath)
    {
        try
        {
            // Inspect the signer embedded in a PE; LoadCertificateFromFile expects a certificate file.
#pragma warning disable SYSLIB0057
            using var cert = X509Certificate.CreateFromSignedFile(filePath);
#pragma warning restore SYSLIB0057
            return (cert.Subject, cert.Issuer);
        }
        catch
        {
            return (null, null);
        }
    }

    private static string? ReadPeArchitecture(string filePath)
    {
        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var br = new BinaryReader(fs);
            if (fs.Length < 0x40 || br.ReadUInt16() != 0x5A4D)
            {
                return null;
            }

            fs.Seek(0x3C, SeekOrigin.Begin);
            var peOffset = br.ReadInt32();
            if (peOffset <= 0 || peOffset + 6 >= fs.Length)
            {
                return null;
            }

            fs.Seek(peOffset, SeekOrigin.Begin);
            if (br.ReadUInt32() != 0x00004550)
            {
                return null;
            }

            var machine = br.ReadUInt16();
            return machine switch
            {
                0x8664 => "x64",
                0x014C => "x86",
                0xAA64 => "arm64",
                _ => $"0x{machine:X4}"
            };
        }
        catch
        {
            return null;
        }
    }
}
