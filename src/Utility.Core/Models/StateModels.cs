namespace Utility.Core.Models;

/// <summary>Outcome of a serialized hardware write operation.</summary>
public enum OperationStatus
{
    InProgress = 0,
    Succeeded = 1,
    NoOpAlreadyMatches = 2,
    CancelledByUser = 3,
    RolledBack = 4,
    PartiallyFailed = 5,
    FailedBeforeWrite = 6,
    Acknowledged = 7
}

/// <summary>Persisted settings stored under %LOCALAPPDATA%\Utility\settings.json.</summary>
public sealed record AppSettingsState
{
    public int SchemaVersion { get; init; } = 1;
    public string ActiveSelectedProfileId { get; init; } = ProfileSeedFactory.DailyId;
    public IReadOnlyList<PerformanceProfile> Profiles { get; init; } = ProfileSeedFactory.CreateEditableSeeds();
    public IReadOnlyList<FieldHardwareContract> FieldContracts { get; init; } = ProfileSeedFactory.CreateDefaultFieldContracts();
    public GraphicsRoutingMode? PendingGraphicsMode { get; init; }
    public string? PendingGraphicsBootSession { get; init; }
    public GraphicsRoutingMode? LastVerifiedGraphicsMode { get; init; }
    public bool PauseGpuPollingOnBattery { get; init; } = true;
    public bool ConfirmGraphicsRecoveryPath { get; init; }
    public string UiLanguage { get; init; } = "zh-CN";
    public DateTimeOffset LastSavedUtc { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>Journal entry written atomically before any hardware mutation begins.</summary>
public sealed record OperationJournalEntry
{
    public required string OperationId { get; init; }
    public required string OperationType { get; init; }
    public required string TargetDescription { get; init; }
    public required OperationStatus Status { get; init; }
    public DateTimeOffset StartedUtc { get; init; }
    public DateTimeOffset? CompletedUtc { get; init; }
    public ProfilePowerSettings? BaselinePower { get; init; }
    public ProfilePowerSettings? RequestedPower { get; init; }
    public ThermalMode? BaselineThermalMode { get; init; }
    public ThermalMode? RequestedThermalMode { get; init; }
    public IReadOnlyList<string> AppliedSteps { get; init; } = [];
    public IReadOnlyList<string> FailedOrUnverifiedFields { get; init; } = [];
    public string? Summary { get; init; }
}

/// <summary>Top-level journal file supporting interrupted-operation detection on startup.</summary>
public sealed record OperationJournalState
{
    public int SchemaVersion { get; init; } = 1;
    public OperationJournalEntry? ActiveInProgress { get; init; }
    public IReadOnlyList<OperationJournalEntry> History { get; init; } = [];
}
