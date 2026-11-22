using Utility.Core.Models;

namespace Utility.Core.Services;

/// <summary>
/// Evaluates Authenticode trust, publisher identity, architecture, OS support, and BIOS exclusion
/// before allowing Utility to interactively launch a user-selected vendor driver installer.
/// </summary>
public static class DriverApplicabilityEvaluator
{
    private static readonly string[] AllowedPublishers =
    [
        "Lenovo",
        "NVIDIA Corporation",
        "Intel Corporation",
        "Realtek Semiconductor",
        "Advanced Micro Devices",
        "Microsoft Corporation"
    ];

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
            failed.Add($"Unsupported file extension '{ext}'; only verified official .exe installers can be launched.");
            return new DriverPackageDecision(
                false, true, false,
                "UNSUPPORTED_EXTENSION",
                "Only verified official .exe driver packages are eligible for interactive launch.",
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
        passed.Add($"Valid Authenticode signature ({pkg.SignerSubject ?? pkg.CompanyName}).");

        var publisherText = $"{pkg.SignerSubject} {pkg.CompanyName}";
        if (!AllowedPublishers.Any(p => publisherText.Contains(p, StringComparison.OrdinalIgnoreCase)))
        {
            failed.Add($"Signer/publisher '{pkg.SignerSubject ?? pkg.CompanyName ?? "(unknown)"}' is not in the official vendor allowlist.");
            return new DriverPackageDecision(
                false, true, false,
                "UNRECOGNIZED_PUBLISHER",
                "Publisher does not match an expected official hardware vendor for this machine.",
                pkg.Sha256Hex, passed, failed);
        }
        passed.Add("Publisher matches official hardware vendor allowlist.");

        if (!string.Equals(pkg.PeArchitecture, "x64", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(pkg.PeArchitecture, "x86_stub_for_x64_sfx", StringComparison.OrdinalIgnoreCase))
        {
            failed.Add($"Incompatible PE architecture '{pkg.PeArchitecture ?? "Unknown"}'; expected x64.");
            return new DriverPackageDecision(
                false, true, false,
                "ARCHITECTURE_MISMATCH",
                $"Package architecture '{pkg.PeArchitecture ?? "Unknown"}' does not match Windows x64.",
                pkg.Sha256Hex, passed, failed);
        }
        passed.Add($"Architecture check passed ({pkg.PeArchitecture}).");

        if (!pkg.SupportsCurrentOs)
        {
            failed.Add("Package metadata indicates incompatibility with Windows 11 x64.");
            return new DriverPackageDecision(
                false, true, false,
                "OS_MISMATCH",
                "Package does not declare support for the current Windows version.",
                pkg.Sha256Hex, passed, failed);
        }
        passed.Add("OS support check passed.");

        // If Lenovo model metadata explicitly names another model (e.g., 82WK) without 83JJ/Legion Y7000, reject mismatch.
        if (publisherText.Contains("Lenovo", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(machine.Model))
        {
            var mentionsOtherModel = System.Text.RegularExpressions.Regex.IsMatch(
                combinedText,
                @"\b(8[0-9][A-Z0-9]{2})\b",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (mentionsOtherModel && !combinedText.Contains(machine.Model, StringComparison.OrdinalIgnoreCase))
            {
                failed.Add($"Package metadata targets another Lenovo machine type instead of '{machine.Model}'.");
                return new DriverPackageDecision(
                    false, true, false,
                    "MODEL_MISMATCH",
                    $"Package targets a different Lenovo machine type than {machine.Model}.",
                    pkg.Sha256Hex, passed, failed);
            }
        }
        passed.Add("Target machine compatibility check passed.");

        return new DriverPackageDecision(
            IsAllowedToLaunch: true,
            RequiresInteractiveUserConfirm: true,
            IsBiosOrFirmwarePackage: false,
            VerdictCode: "VERIFIED_OFFICIAL_EXE",
            Reason: "Package passed signature, vendor, architecture, OS, and non-BIOS checks. Ready for user-confirmed interactive launch.",
            VerifiedSha256Hex: pkg.Sha256Hex,
            ChecksPassed: passed,
            ChecksFailed: failed);
    }
}
