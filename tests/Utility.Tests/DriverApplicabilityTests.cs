using Utility.Core.Models;
using Utility.Core.Services;
using Xunit;

namespace Utility.Tests;

public sealed class DriverApplicabilityTests
{
    private static readonly MachineIdentity Target83JJ = new("LENOVO", "83JJ", "Legion Y7000 IRX9", "83JJ", "PTCN18WW", "26300");

    private static DriverPackageInspection MakeBasePackage() => new(
        FilePath: @"C:\Downloads\nvidia_572_x64.exe",
        FileExtension: ".exe",
        FileSizeBytes: 500_000_000,
        Sha256Hex: "ABCDEF0123456789",
        IsSignatureValid: true,
        SignatureStatusDetail: "TRUST_E_OK",
        SignerSubject: "CN=NVIDIA Corporation",
        SignerIssuer: "CN=DigiCert",
        ProductName: "NVIDIA Graphics Driver",
        FileDescription: "NVIDIA GeForce RTX 4060 Laptop GPU Driver",
        FileVersion: "572.16",
        CompanyName: "NVIDIA Corporation",
        PeArchitecture: "x64");

    [Fact]
    public void SignedFileStillRequiresAnOfficialInstallationRoute()
    {
        var decision = DriverApplicabilityEvaluator.Evaluate(MakeBasePackage(), Target83JJ);
        Assert.False(decision.IsAllowedToLaunch);
        Assert.True(decision.RequiresInteractiveUserConfirm);
        Assert.False(decision.IsBiosOrFirmwarePackage);
        Assert.Equal("USE_OFFICIAL_INSTALLATION_ROUTE", decision.VerdictCode);
    }

    [Fact]
    public void RejectsBiosFirmwareUntrustedInfAndMismatchedPackages()
    {
        var bios = MakeBasePackage() with { FilePath = @"C:\Downloads\PTCN18WW_BIOS.exe", ProductName = "Lenovo BIOS Update" };
        var biosRes = DriverApplicabilityEvaluator.Evaluate(bios, Target83JJ);
        Assert.False(biosRes.IsAllowedToLaunch);
        Assert.True(biosRes.IsBiosOrFirmwarePackage);
        Assert.Equal("BIOS_FIRMWARE_EXCLUDED", biosRes.VerdictCode);

        var untrusted = MakeBasePackage() with { IsSignatureValid = false, SignatureStatusDetail = "CERT_E_UNTRUSTEDROOT" };
        Assert.Equal("UNTRUSTED_SIGNATURE", DriverApplicabilityEvaluator.Evaluate(untrusted, Target83JJ).VerdictCode);

        var infPkg = MakeBasePackage() with { FilePath = @"C:\Downloads\nv_disp.inf", FileExtension = ".inf" };
        Assert.Equal("DEFER_INF_CATALOG", DriverApplicabilityEvaluator.Evaluate(infPkg, Target83JJ).VerdictCode);

        var wrongModel = MakeBasePackage() with
        {
            FilePath = @"C:\Downloads\lenovo_wlan_82WK.exe",
            SignerSubject = "CN=Lenovo",
            CompanyName = "Lenovo",
            ProductName = "WLAN Driver 82WK"
        };
        Assert.False(DriverApplicabilityEvaluator.Evaluate(wrongModel, Target83JJ).IsAllowedToLaunch);
    }
}
