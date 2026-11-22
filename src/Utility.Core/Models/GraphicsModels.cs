namespace Utility.Core.Models;

/// <summary>System graphics routing modes supported by Lenovo Legion hardware.</summary>
public enum GraphicsRoutingMode
{
    Unknown = 0,
    Hybrid = 1,
    DgpuOnly = 2,
    HybridAuto = 3,
    HybridIgpuOnly = 4
}

/// <summary>Descriptor for an enumerated graphics routing mode.</summary>
public sealed record GraphicsModeOption(
    GraphicsRoutingMode Mode,
    bool RequiresReboot,
    bool MayInterruptDisplays,
    bool IsVerifiedOnTarget,
    string Description);

/// <summary>Current and pending graphics routing state.</summary>
public sealed record GraphicsStatusSnapshot(
    GraphicsRoutingMode EffectiveMode,
    GraphicsRoutingMode? PendingMode,
    bool RebootRequired,
    IReadOnlyList<GraphicsModeOption> SupportedModes,
    bool IsVerifiedLiveRead,
    string StatusNote,
    DateTimeOffset CapturedAtUtc)
{
    public string? BootSession { get; init; }
}
