using Utility.Core.Models;

namespace Utility.Core.Services;

/// <summary>
/// Describes inspection results without treating a signature as proof of model applicability.
/// </summary>
public static class DriverApplicabilityEvaluator
{
    private static readonly string[] BiosKeywords =
    [
        "BIOS",
        "UEFI",
        "FIRMWARE",
        "PTCN",
        "WINFLASH",
        "H2OFFT"
    ];

    public static DriverPackageDecision Evaluate(DriverPackageInspection pkg, MachineIdentity machine)
    {
        var passed = new List<string>();
        var failed = new List<string>();

        var ext = pkg.FileExtension.ToLowerInvariant();
        if (ext is ".inf" or ".cat")
        {
            failed.Add($"Extension '{ext}' is an INF/catalog package; defer to Windows Update or vendor installer.");
            return new DriverPackageDecision(
                false, true, false,
                "DEFER_INF_CATALOG",
                "INF and catalog packages are not directly launched by Utility; install via Device Manager or vendor setup.",
                pkg.Sha256Hex, passed, failed);
        }

        if (ext != ".exe")
        {
            failed.Add($"Unsupported file extension '{ext}'; this inspection expects an .exe file.");
            return new DriverPackageDecision(
                false, true, false,
                "UNSUPPORTED_EXTENSION",
                "Use Windows Update or the vendor's official installation instructions.",
                pkg.Sha256Hex, passed, failed);
        }
        passed.Add("Package extension is .exe.");

        var combinedText = $"{Path.GetFileNameWithoutExtension(pkg.FilePath)} {pkg.ProductName} {pkg.FileDescription}";
        if (BiosKeywords.Any(k => combinedText.Contains(k, StringComparison.OrdinalIgnoreCase)))
        {
            failed.Add("Detected BIOS/UEFI/firmware flashing keyword; Utility forbids launching firmware updates.");
            return new DriverPackageDecision(
                false, true, true,
                "BIOS_FIRMWARE_EXCLUDED",
                "BIOS and firmware update packages cannot be launched from Utility.",
                pkg.Sha256Hex, passed, failed);
        }
        passed.Add("Not classified as BIOS or firmware update.");

        if (!pkg.IsSignatureValid)
        {
            failed.Add($"Authenticode trust verification failed: {pkg.SignatureStatusDetail}");
            return new DriverPackageDecision(
                false, true, false,
                "UNTRUSTED_SIGNATURE",
                $"Digital signature verification failed ({pkg.SignatureStatusDetail}).",
                pkg.Sha256Hex, passed, failed);
        }
        passed.Add($"Valid Authenticode signature ({pkg.SignerSubject ?? "signer unavailable"}).");

        failed.Add($"This file has no verified official package manifest for {machine.Model}, its devices and Windows version.");
        return new DriverPackageDecision(
            false, true, false,
            "USE_OFFICIAL_INSTALLATION_ROUTE",
            "Signature is valid. Install only through Windows Update or the matching vendor package and instructions.",
            pkg.Sha256Hex, passed, failed);
    }
}
