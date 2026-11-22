using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Utility.Core.Models;

namespace Utility.Core.Services;

public sealed record SettingsLoadResult(
    AppSettingsState Settings,
    bool RecoveredFromLastGood,
    string StatusNote);

/// <summary>
/// Persists versioned settings, atomic last-good backups, pre-write operation journals,
/// and bounded redacted local diagnostics under %LOCALAPPDATA%\Utility\.
/// </summary>
public sealed partial class JsonStateStore
{
    private const int MaxLogLines = 500;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _baseDirectory;
    private readonly SemaphoreSlim _ioLock = new(1, 1);

    public JsonStateStore(string? baseDirectory = null)
    {
        _baseDirectory = string.IsNullOrWhiteSpace(baseDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Utility")
            : baseDirectory;
    }

    public string BaseDirectory => _baseDirectory;
    public string SettingsPath => Path.Combine(_baseDirectory, "settings.json");
    public string LastGoodSettingsPath => Path.Combine(_baseDirectory, "settings.lastgood.json");
    public string JournalPath => Path.Combine(_baseDirectory, "operation-journal.json");
    public string DiagnosticsLogPath => Path.Combine(_baseDirectory, "diagnostics.log");

    public async Task<SettingsLoadResult> LoadSettingsAsync(CancellationToken ct = default)
    {
        await _ioLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(_baseDirectory);

            if (TryDeserializeFile<AppSettingsState>(SettingsPath, out var primary) && primary is not null && primary.SchemaVersion >= 1)
            {
                return new SettingsLoadResult(EnsureSeededProfiles(primary), false, "Loaded primary settings.");
            }

            if (TryDeserializeFile<AppSettingsState>(LastGoodSettingsPath, out var backup) && backup is not null && backup.SchemaVersion >= 1)
            {
                var recovered = EnsureSeededProfiles(backup);
                await WriteAtomicJsonAsync(SettingsPath, recovered, ct).ConfigureAwait(false);
                return new SettingsLoadResult(recovered, true, "Primary settings file was missing or corrupted; recovered from last-good backup.");
            }

            var defaults = new AppSettingsState();
            await WriteAtomicJsonAsync(SettingsPath, defaults, ct).ConfigureAwait(false);
            await WriteAtomicJsonAsync(LastGoodSettingsPath, defaults, ct).ConfigureAwait(false);
            return new SettingsLoadResult(defaults, false, "Initialized default settings with owner baselines.");
        }
        finally
        {
            _ioLock.Release();
        }
    }

    public async Task SaveSettingsAsync(AppSettingsState settings, CancellationToken ct = default)
    {
        await _ioLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(_baseDirectory);
            var stamped = settings with { SchemaVersion = 1, LastSavedUtc = DateTimeOffset.UtcNow };
            await WriteAtomicJsonAsync(SettingsPath, stamped, ct).ConfigureAwait(false);
            await WriteAtomicJsonAsync(LastGoodSettingsPath, stamped, ct).ConfigureAwait(false);
        }
        finally
        {
            _ioLock.Release();
        }
    }

    public async Task<OperationJournalState> LoadJournalAsync(CancellationToken ct = default)
    {
        await _ioLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(_baseDirectory);
            if (TryDeserializeFile<OperationJournalState>(JournalPath, out var journal) && journal is not null)
            {
                return journal;
            }

            return new OperationJournalState();
        }
        finally
        {
            _ioLock.Release();
        }
    }

    public async Task SaveJournalAsync(OperationJournalState journal, CancellationToken ct = default)
    {
        await _ioLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(_baseDirectory);
            await WriteAtomicJsonAsync(JournalPath, journal with { SchemaVersion = 1 }, ct).ConfigureAwait(false);
        }
        finally
        {
            _ioLock.Release();
        }
    }

    public async Task AppendDiagnosticAsync(string category, string message, CancellationToken ct = default)
    {
        await _ioLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(_baseDirectory);
            var redacted = RedactSensitiveText(message);
            var line = $"{DateTimeOffset.UtcNow:O} [{category}] {redacted}";

            var lines = File.Exists(DiagnosticsLogPath)
                ? (await File.ReadAllLinesAsync(DiagnosticsLogPath, ct).ConfigureAwait(false)).ToList()
                : [];
            lines.Add(line);
            if (lines.Count > MaxLogLines)
            {
                lines = lines.Skip(lines.Count - MaxLogLines).ToList();
            }

            var temp = DiagnosticsLogPath + ".tmp";
            await File.WriteAllLinesAsync(temp, lines, ct).ConfigureAwait(false);
            File.Move(temp, DiagnosticsLogPath, overwrite: true);
        }
        finally
        {
            _ioLock.Release();
        }
    }

    public static string RedactSensitiveText(string input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return input;
        }

        var output = SensitiveKeyValueRegex().Replace(input, "$1=[REDACTED]");
        output = BearerTokenRegex().Replace(output, "Bearer [REDACTED]");
        return output;
    }

    private static AppSettingsState EnsureSeededProfiles(AppSettingsState state)
    {
        var profiles = state.Profiles.ToList();
        if (!profiles.Any(p => p.Id == ProfileSeedFactory.DailyId))
        {
            profiles.Insert(0, ProfileSeedFactory.CreatePristineReference(ProfileSeedFactory.DailyId));
        }

        if (!profiles.Any(p => p.Id == ProfileSeedFactory.GamingId))
        {
            profiles.Add(ProfileSeedFactory.CreatePristineReference(ProfileSeedFactory.GamingId));
        }

        var contracts = state.FieldContracts.Count > 0
            ? state.FieldContracts
            : ProfileSeedFactory.CreateDefaultFieldContracts();

        return state with { Profiles = profiles, FieldContracts = contracts };
    }

    private static bool TryDeserializeFile<T>(string path, out T? result)
    {
        result = default;
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            var json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json))
            {
                return false;
            }

            result = JsonSerializer.Deserialize<T>(json, JsonOptions);
            return result is not null;
        }
        catch
        {
            return false;
        }
    }

    private static async Task WriteAtomicJsonAsync<T>(string targetPath, T payload, CancellationToken ct)
    {
        var tempPath = $"{targetPath}.{Guid.NewGuid():N}.tmp";
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        await using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true))
        await using (var writer = new StreamWriter(stream))
        {
            await writer.WriteAsync(json.AsMemory(), ct).ConfigureAwait(false);
            await writer.FlushAsync(ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }

        File.Move(tempPath, targetPath, overwrite: true);
    }

    [GeneratedRegex(@"(?i)\b(SerialNumber|MachineSerial|Password|Token|Secret|Credential|ApiKey)\s*[:=]\s*[^\s,;]+")]
    private static partial Regex SensitiveKeyValueRegex();

    [GeneratedRegex(@"(?i)Bearer\s+[A-Za-z0-9\-._~+/]+=*")]
    private static partial Regex BearerTokenRegex();
}
