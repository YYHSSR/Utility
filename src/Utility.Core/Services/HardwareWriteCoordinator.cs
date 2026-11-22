using Utility.Core.Models;

namespace Utility.Core.Services;

public sealed record HardwareWriteReport(
    string OperationId,
    OperationStatus Status,
    string Summary,
    LiveThermalSnapshot? ReadBackSnapshot,
    IReadOnlyList<string> AppliedSteps,
    IReadOnlyList<string> FailedOrUnverifiedFields);

/// <summary>
/// Serializes hardware writes, persists pre-write operation journals, detects no-ops,
/// verifies read-back, and rolls back changed fields on mid-operation failures.
/// </summary>
public sealed class HardwareWriteCoordinator
{
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ILenovoHardwareControlProvider _hardware;
    private readonly JsonStateStore _stateStore;
    private readonly ProfileValidationService _validator;

    public HardwareWriteCoordinator(
        ILenovoHardwareControlProvider hardware,
        JsonStateStore stateStore,
        ProfileValidationService validator)
    {
        _hardware = hardware;
        _stateStore = stateStore;
        _validator = validator;
    }

    public async Task<OperationJournalEntry?> GetInterruptedOperationAsync(CancellationToken ct = default)
    {
        var journal = await _stateStore.LoadJournalAsync(ct).ConfigureAwait(false);
        return journal.ActiveInProgress;
    }

    public async Task DismissInterruptedOperationAsync(string operationId, string resolutionNote, CancellationToken ct = default)
    {
        var journal = await _stateStore.LoadJournalAsync(ct).ConfigureAwait(false);
        if (journal.ActiveInProgress is null || journal.ActiveInProgress.OperationId != operationId)
        {
            return;
        }

        var completed = journal.ActiveInProgress with
        {
            Status = OperationStatus.RolledBack,
            CompletedUtc = DateTimeOffset.UtcNow,
            Summary = resolutionNote
        };

        var updatedHistory = journal.History.Prepend(completed).Take(50).ToList();
        await _stateStore.SaveJournalAsync(new OperationJournalState
        {
            SchemaVersion = 1,
            ActiveInProgress = null,
            History = updatedHistory
        }, ct).ConfigureAwait(false);
    }

    public async Task<HardwareWriteReport> ApplyPowerFieldsAsync(
        PerformanceProfile profile,
        IReadOnlyList<FieldHardwareContract> contracts,
        CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var baseline = await _hardware.ReadLiveThermalSnapshotAsync(allowElevation: true, ct).ConfigureAwait(false);
            if (!baseline.IsVerifiedLiveRead)
            {
                return new HardwareWriteReport(
                    Guid.NewGuid().ToString("N"),
                    OperationStatus.FailedBeforeWrite,
                    $"Unable to read baseline live state before write: {baseline.StatusNote}",
                    baseline,
                    [],
                    ["BaselineRead"]);
            }

            var validation = _validator.ValidatePowerFields(profile.Power, contracts, baseline.IsOnAcPower, requireVerifiedContracts: true);
            if (!validation.IsValid)
            {
                return new HardwareWriteReport(
                    Guid.NewGuid().ToString("N"),
                    OperationStatus.FailedBeforeWrite,
                    string.Join(" ", validation.Errors),
                    baseline,
                    [],
                    validation.MissingFields);
            }

            if (ProfileValidationService.DoesLiveMatchProfile(baseline, profile, contracts))
            {
                return new HardwareWriteReport(
                    Guid.NewGuid().ToString("N"),
                    OperationStatus.NoOpAlreadyMatches,
                    "Live hardware state already matches the requested profile fields; no write performed.",
                    baseline,
                    [],
                    []);
            }

            var opId = Guid.NewGuid().ToString("N");
            var appliedSteps = new List<string>();
            var entry = new OperationJournalEntry
            {
                OperationId = opId,
                OperationType = "ApplyPowerFields",
                TargetDescription = profile.Name,
                Status = OperationStatus.InProgress,
                StartedUtc = DateTimeOffset.UtcNow,
                BaselinePower = baseline.LivePowerValues,
                RequestedPower = profile.Power,
                BaselineThermalMode = baseline.ThermalMode,
                RequestedThermalMode = ThermalMode.Custom
            };

            await BeginJournalEntryAsync(entry, ct).ConfigureAwait(false);

            var thermalChanged = false;
            if (baseline.ThermalMode != ThermalMode.Custom)
            {
                var modeRes = await _hardware.SetThermalModeAsync(ThermalMode.Custom, ct).ConfigureAwait(false);
                if (modeRes.WasCancelledByUser)
                {
                    await FinalizeJournalEntryAsync(entry, OperationStatus.CancelledByUser, appliedSteps, [], "User cancelled elevation.", ct).ConfigureAwait(false);
                    return new HardwareWriteReport(opId, OperationStatus.CancelledByUser, modeRes.Message, baseline, appliedSteps, []);
                }

                if (!modeRes.Success)
                {
                    await FinalizeJournalEntryAsync(entry, OperationStatus.FailedBeforeWrite, appliedSteps, ["ThermalMode"], modeRes.Message, ct).ConfigureAwait(false);
                    return new HardwareWriteReport(opId, OperationStatus.FailedBeforeWrite, modeRes.Message, baseline, appliedSteps, ["ThermalMode"]);
                }

                thermalChanged = true;
                appliedSteps.Add("ThermalMode=Custom");
            }

            var writeRes = await _hardware.ApplyPowerFieldsAsync(validation.ConvertedBackendValues, contracts, ct).ConfigureAwait(false);
            if (writeRes.WasCancelledByUser)
            {
                if (thermalChanged && baseline.ThermalMode != ThermalMode.Unknown)
                {
                    await _hardware.SetThermalModeAsync(baseline.ThermalMode, ct).ConfigureAwait(false);
                }

                var afterCancel = await _hardware.ReadLiveThermalSnapshotAsync(allowElevation: false, ct).ConfigureAwait(false);
                await FinalizeJournalEntryAsync(entry, OperationStatus.CancelledByUser, appliedSteps, [], writeRes.Message, ct).ConfigureAwait(false);
                return new HardwareWriteReport(opId, OperationStatus.CancelledByUser, writeRes.Message, afterCancel, appliedSteps, []);
            }

            if (!writeRes.Success)
            {
                var recoveryNotes = await AttemptRollbackAsync(baseline, thermalChanged, contracts, ct).ConfigureAwait(false);
                var afterRecovery = await _hardware.ReadLiveThermalSnapshotAsync(allowElevation: true, ct).ConfigureAwait(false);
                var failedFields = writeRes.FailedFields ?? ["PowerFields"];
                var finalStatus = recoveryNotes.AllRestored ? OperationStatus.RolledBack : OperationStatus.PartiallyFailed;
                var summary = $"{writeRes.Message} Recovery: {recoveryNotes.Message}";
                await FinalizeJournalEntryAsync(entry, finalStatus, appliedSteps, failedFields, summary, ct).ConfigureAwait(false);
                return new HardwareWriteReport(opId, finalStatus, summary, afterRecovery, appliedSteps, failedFields);
            }

            appliedSteps.AddRange(validation.ConvertedBackendValues.Keys);

            // Best-effort Windows power mode update (kept separate from firmware mode).
            var winModeNote = string.Empty;
            if (profile.WindowsPowerMode != WindowsPowerMode.Unknown && baseline.WindowsPowerMode != profile.WindowsPowerMode)
            {
                var winRes = await _hardware.SetWindowsPowerModeAsync(profile.WindowsPowerMode, ct).ConfigureAwait(false);
                if (winRes.Success)
                {
                    appliedSteps.Add($"WindowsPowerMode={profile.WindowsPowerMode}");
                }
                else
                {
                    winModeNote = $" Windows power mode note: {winRes.Message}";
                }
            }

            var readBack = await _hardware.ReadLiveThermalSnapshotAsync(allowElevation: true, ct).ConfigureAwait(false);
            var mismatched = FindMismatchedFields(profile.Power, readBack.LivePowerValues, contracts);
            if (mismatched.Count > 0)
            {
                var summary = $"Applied power fields, but read-back verification mismatched for: {string.Join(", ", mismatched)}.{winModeNote}";
                await FinalizeJournalEntryAsync(entry, OperationStatus.PartiallyFailed, appliedSteps, mismatched, summary, ct).ConfigureAwait(false);
                return new HardwareWriteReport(opId, OperationStatus.PartiallyFailed, summary, readBack, appliedSteps, mismatched);
            }

            var okSummary = $"Applied and verified {validation.ConvertedBackendValues.Count} power fields (existing fan policy preserved).{winModeNote}";
            await FinalizeJournalEntryAsync(entry, OperationStatus.Succeeded, appliedSteps, [], okSummary, ct).ConfigureAwait(false);
            return new HardwareWriteReport(opId, OperationStatus.Succeeded, okSummary, readBack, appliedSteps, []);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<HardwareWriteReport> SetStockThermalModeAsync(ThermalMode targetMode, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var baseline = await _hardware.ReadLiveThermalSnapshotAsync(allowElevation: true, ct).ConfigureAwait(false);
            if (baseline.IsVerifiedLiveRead && baseline.ThermalMode == targetMode)
            {
                return new HardwareWriteReport(
                    Guid.NewGuid().ToString("N"),
                    OperationStatus.NoOpAlreadyMatches,
                    $"Thermal mode is already {targetMode}.",
                    baseline,
                    [],
                    []);
            }

            var opId = Guid.NewGuid().ToString("N");
            var entry = new OperationJournalEntry
            {
                OperationId = opId,
                OperationType = "SetThermalMode",
                TargetDescription = targetMode.ToString(),
                Status = OperationStatus.InProgress,
                StartedUtc = DateTimeOffset.UtcNow,
                BaselineThermalMode = baseline.ThermalMode,
                RequestedThermalMode = targetMode
            };
            await BeginJournalEntryAsync(entry, ct).ConfigureAwait(false);

            var res = await _hardware.SetThermalModeAsync(targetMode, ct).ConfigureAwait(false);
            if (res.WasCancelledByUser)
            {
                await FinalizeJournalEntryAsync(entry, OperationStatus.CancelledByUser, [], [], res.Message, ct).ConfigureAwait(false);
                return new HardwareWriteReport(opId, OperationStatus.CancelledByUser, res.Message, baseline, [], []);
            }

            if (!res.Success)
            {
                await FinalizeJournalEntryAsync(entry, OperationStatus.FailedBeforeWrite, [], ["ThermalMode"], res.Message, ct).ConfigureAwait(false);
                return new HardwareWriteReport(opId, OperationStatus.FailedBeforeWrite, res.Message, baseline, [], ["ThermalMode"]);
            }

            var readBack = await _hardware.ReadLiveThermalSnapshotAsync(allowElevation: true, ct).ConfigureAwait(false);
            var status = readBack.IsVerifiedLiveRead && readBack.ThermalMode == targetMode
                ? OperationStatus.Succeeded
                : OperationStatus.PartiallyFailed;
            var failed = status == OperationStatus.Succeeded ? Array.Empty<string>() : new[] { "ThermalMode" };
            var msg = status == OperationStatus.Succeeded
                ? $"Switched and verified thermal mode: {targetMode}."
                : $"Requested {targetMode}, but live read-back returned {readBack.ThermalMode}.";

            await FinalizeJournalEntryAsync(entry, status, [$"ThermalMode={targetMode}"], failed, msg, ct).ConfigureAwait(false);
            return new HardwareWriteReport(opId, status, msg, readBack, [$"ThermalMode={targetMode}"], failed);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task<(bool AllRestored, string Message)> AttemptRollbackAsync(
        LiveThermalSnapshot baseline,
        bool thermalChanged,
        IReadOnlyList<FieldHardwareContract> contracts,
        CancellationToken ct)
    {
        var messages = new List<string>();
        var allOk = true;

        if (baseline.LivePowerValues is not null)
        {
            var baseValidation = _validator.ValidatePowerFields(baseline.LivePowerValues, contracts, baseline.IsOnAcPower, requireVerifiedContracts: true);
            if (baseValidation.ConvertedBackendValues.Count > 0)
            {
                var restoreRes = await _hardware.ApplyPowerFieldsAsync(baseValidation.ConvertedBackendValues, contracts, ct).ConfigureAwait(false);
                if (!restoreRes.Success)
                {
                    allOk = false;
                    messages.Add($"Power field rollback failed ({restoreRes.Message}).");
                }
                else
                {
                    messages.Add("Power fields restored to baseline.");
                }
            }
        }

        if (thermalChanged && baseline.ThermalMode != ThermalMode.Unknown)
        {
            var modeRestore = await _hardware.SetThermalModeAsync(baseline.ThermalMode, ct).ConfigureAwait(false);
            if (!modeRestore.Success)
            {
                allOk = false;
                messages.Add($"Thermal mode rollback failed ({modeRestore.Message}).");
            }
            else
            {
                messages.Add($"Thermal mode restored to {baseline.ThermalMode}.");
            }
        }

        return (allOk, messages.Count == 0 ? "No rollback actions required." : string.Join(" ", messages));
    }

    private static List<string> FindMismatchedFields(
        ProfilePowerSettings expected,
        ProfilePowerSettings? actual,
        IReadOnlyList<FieldHardwareContract> contracts)
    {
        var mismatches = new List<string>();
        if (actual is null)
        {
            mismatches.Add("LivePowerValues");
            return mismatches;
        }

        var expMap = expected.AsFieldMap();
        var actMap = actual.AsFieldMap();

        foreach (var contract in contracts)
        {
            if (!expMap.TryGetValue(contract.FieldKey, out var expVal) || expVal is null)
            {
                continue;
            }

            if (!actMap.TryGetValue(contract.FieldKey, out var actVal) || actVal is null)
            {
                mismatches.Add(contract.FieldKey);
                continue;
            }

            var tolerance = Math.Max(0, contract.Step - 1);
            if (Math.Abs(actVal.Value - expVal.Value) > tolerance)
            {
                mismatches.Add(contract.FieldKey);
            }
        }

        return mismatches;
    }

    private async Task BeginJournalEntryAsync(OperationJournalEntry entry, CancellationToken ct)
    {
        var journal = await _stateStore.LoadJournalAsync(ct).ConfigureAwait(false);
        await _stateStore.SaveJournalAsync(journal with { ActiveInProgress = entry }, ct).ConfigureAwait(false);
    }

    private async Task FinalizeJournalEntryAsync(
        OperationJournalEntry entry,
        OperationStatus finalStatus,
        IReadOnlyList<string> appliedSteps,
        IReadOnlyList<string> failedFields,
        string summary,
        CancellationToken ct)
    {
        var completed = entry with
        {
            Status = finalStatus,
            CompletedUtc = DateTimeOffset.UtcNow,
            AppliedSteps = appliedSteps,
            FailedOrUnverifiedFields = failedFields,
            Summary = summary
        };

        var journal = await _stateStore.LoadJournalAsync(ct).ConfigureAwait(false);
        var history = journal.History.Prepend(completed).Take(50).ToList();
        await _stateStore.SaveJournalAsync(new OperationJournalState
        {
            SchemaVersion = 1,
            ActiveInProgress = null,
            History = history
        }, ct).ConfigureAwait(false);
    }
}
