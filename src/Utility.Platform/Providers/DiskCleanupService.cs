using System.Buffers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.VisualBasic.FileIO;
using Microsoft.Win32.SafeHandles;

namespace Utility.Platform.Providers;

public enum DiskScanKind { Temporary, Large, Duplicate }

public sealed record ScannedFile(string Path, long Bytes, DateTime LastWriteUtc, string? Digest = null, int? Group = null);

public sealed record DiskScanResult(IReadOnlyList<ScannedFile> Files, long Visited, bool Limited, long? DriveFreeBytes);

public sealed record PersonalFileScan(DiskScanResult Large, DiskScanResult Duplicate);

public sealed record SystemDriveScan(DiskScanResult Temporary, Task<DiskScanResult> Duplicate);

public sealed record CleanupBatchResult(IReadOnlyList<string> Removed, IReadOnlyList<string> Errors, string? BackupFolder = null);

public sealed class DiskCleanupService
{
    private const int ResultLimit = 4000;
    private const FileAttributes UnsafePath = FileAttributes.ReparsePoint | FileAttributes.Offline;
    private static readonly string WindowsFolder = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    private static readonly string UserFolder = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private static readonly string LocalData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static readonly string SystemDrive = Path.GetPathRoot(WindowsFolder)!;
    private static readonly string UserProfiles = Path.Combine(SystemDrive, "Users");
    private static readonly string PublicProfile = Path.Combine(UserProfiles, "Public");
    private static readonly string[] TempRoots = SystemTempRoots();
    private static readonly HashSet<string> TempAncestors = FindTempAncestors();
    private static readonly string[] ProtectedFolders =
    [
        WindowsFolder,
        Path.Combine(SystemDrive, "Windows.old"),
        Path.Combine(SystemDrive, "Recovery"),
        Path.Combine(SystemDrive, "System Volume Information"),
        Path.Combine(SystemDrive, "$Recycle.Bin"),
        Path.Combine(SystemDrive, "$WinREAgent"),
        Path.Combine(SystemDrive, "$WINDOWS.~BT"),
        Path.Combine(SystemDrive, "$WINDOWS.~WS"),
        Path.Combine(SystemDrive, "Boot"),
        Path.Combine(SystemDrive, "Program Files"),
        Path.Combine(SystemDrive, "Program Files (x86)"),
        Path.Combine(SystemDrive, "ProgramData"),
        Path.Combine(UserFolder, "AppData"),
        LocalData
    ];
    private static readonly HashSet<string> ProtectedRootFiles = new(
        new[] { "pagefile.sys", "swapfile.sys", "hiberfil.sys", "DumpStack.log.tmp" }
            .Select(name => Path.Combine(SystemDrive, name)), StringComparer.OrdinalIgnoreCase);

    public string SystemDrivePath => SystemDrive;

    public Task<DiskScanResult> ScanAsync(DiskScanKind kind, string root, long minimumBytes,
        IProgress<long>? progress, CancellationToken ct) =>
        Task.Run(() => kind switch
        {
            DiskScanKind.Temporary => IndexSystemDrive(includeDuplicates: false, progress, ct).Temporary,
            DiskScanKind.Large => ScanPersonal(root, null, minimumBytes, progress, ct).Large,
            DiskScanKind.Duplicate => ScanPersonal(null, root, minimumBytes, progress, ct).Duplicate,
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        }, ct);

    public async Task<SystemDriveScan> ScanSystemDriveAsync(IProgress<long>? progress, CancellationToken ct)
    {
        var (temporary, sizes, visited) = await Task.Run(() => IndexSystemDrive(includeDuplicates: true, progress, ct), ct)
            .ConfigureAwait(false);
        return new SystemDriveScan(temporary, Task.Run(() => sizes.FindMatches(visited, ct), ct));
    }

    public Task<PersonalFileScan> ScanPersonalAsync(string largeFolder, string duplicateFolder, long minimumBytes,
        IProgress<long>? progress, CancellationToken ct) =>
        Task.Run(() => ScanPersonal(largeFolder, duplicateFolder, minimumBytes, progress, ct), ct);

    public Task<CleanupBatchResult> RemoveAsync(DiskScanKind kind, IReadOnlyList<ScannedFile> all,
        IReadOnlyList<ScannedFile> selected, CancellationToken stop = default) =>
        Task.Run(() => Remove(kind, all, selected, stop));

    private static string[] SystemTempRoots() =>
        new[]
        {
            Path.Combine(WindowsFolder, "Temp"),
            Path.Combine(WindowsFolder, "SystemTemp"),
            Path.Combine(LocalData, "Temp"),
            Path.Combine(LocalData, "D3DSCache"),
            Path.Combine(LocalData, "NVIDIA", "DXCache"),
            Path.Combine(LocalData, "NVIDIA", "GLCache"),
            Path.Combine(UserFolder, "AppData", "LocalLow", "NVIDIA", "DXCache")
        }.Where(IsOnSystemDrive).ToArray();

    private static HashSet<string> FindTempAncestors()
    {
        var ancestors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in TempRoots)
        {
            for (var path = Path.GetDirectoryName(root); path is not null; path = Path.GetDirectoryName(path))
                ancestors.Add(path);
        }
        return ancestors;
    }

    private static (DiskScanResult Temporary, DuplicateSizes Sizes, long DuplicateVisited) IndexSystemDrive(
        bool includeDuplicates, IProgress<long>? progress, CancellationToken ct)
    {
        var temporary = new PriorityQueue<ScannedFile, long>();
        var duplicateSizes = new DuplicateSizes();
        var cutoff = DateTime.UtcNow.AddDays(-2);
        long visited = 0, tempVisited = 0, duplicateVisited = 0;
        var tempLimited = false;
        var tempFolder = false;
        var duplicateFolder = false;

        bool SkipFolder(string folder)
        {
            tempFolder = TempRoots.Any(root => IsInOrEqual(folder, root));
            duplicateFolder = includeDuplicates && !tempFolder && !IsProtected(folder);
            return !tempFolder && !duplicateFolder && !TempAncestors.Contains(folder);
        }

        foreach (var file in WalkFiles(SystemDrive, SkipFolder, ct))
        {
            if (++visited % 2048 == 0)
                progress?.Report(visited);
            if (tempFolder)
            {
                tempVisited++;
                if (file.LastWriteUtc <= cutoff)
                    tempLimited |= AddLargest(temporary, file);
            }
            else if (duplicateFolder && !ProtectedRootFiles.Contains(file.Path))
            {
                duplicateVisited++;
                duplicateSizes.Add(file);
            }
        }

        progress?.Report(visited);
        return (SortedFiles(temporary, tempVisited, tempLimited, new DriveInfo(SystemDrive).AvailableFreeSpace),
            duplicateSizes, duplicateVisited);
    }

    private static PersonalFileScan ScanPersonal(string? largeFolder, string? duplicateFolder, long minimumBytes,
        IProgress<long>? progress, CancellationToken ct)
    {
        var largeRoot = largeFolder is null ? null : RequireLocalFolder(largeFolder);
        var duplicateRoot = duplicateFolder is null ? null : RequireLocalFolder(duplicateFolder);
        var roots = new[] { largeRoot, duplicateRoot }.OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var largeFiles = new PriorityQueue<ScannedFile, long>();
        var duplicateSizes = new DuplicateSizes();
        long visited = 0, largeVisited = 0, duplicateVisited = 0;
        var largeLimited = false;
        foreach (var root in roots.Where(root => !roots.Any(parent => IsChild(root, parent))))
        {
            var scanLarge = false;
            var scanDuplicate = false;
            bool SkipFolder(string folder)
            {
                scanLarge = largeRoot is not null && IsInOrEqual(folder, largeRoot);
                scanDuplicate = duplicateRoot is not null && IsInOrEqual(folder, duplicateRoot);
                return IsProtected(folder);
            }

            foreach (var file in WalkFiles(root, SkipFolder, ct))
            {
                if (++visited % 2048 == 0)
                    progress?.Report(visited);
                if (ProtectedRootFiles.Contains(file.Path))
                    continue;
                if (scanLarge)
                {
                    largeVisited++;
                    if (file.Bytes >= minimumBytes)
                        largeLimited |= AddLargest(largeFiles, file);
                }
                if (scanDuplicate)
                {
                    duplicateVisited++;
                    duplicateSizes.Add(file);
                }
            }
        }

        progress?.Report(visited);
        return new PersonalFileScan(SortedFiles(largeFiles, largeVisited, largeLimited),
            duplicateSizes.FindMatches(duplicateVisited, ct));
    }

    private static bool AddLargest(PriorityQueue<ScannedFile, long> files, ScannedFile file)
    {
        if (files.Count == ResultLimit)
        {
            files.EnqueueDequeue(file, file.Bytes);
            return true;
        }
        files.Enqueue(file, file.Bytes);
        return false;
    }

    private static DiskScanResult SortedFiles(PriorityQueue<ScannedFile, long> files, long visited, bool limited,
        long? free = null) =>
        new(files.UnorderedItems.Select(item => item.Element).OrderByDescending(file => file.Bytes).ToArray(),
            visited, limited, free);

    private sealed class DuplicateSizes
    {
        private readonly Dictionary<long, ScannedFile> _single = [];
        private readonly Dictionary<long, List<ScannedFile>> _shared = [];

        public void Add(ScannedFile file)
        {
            if (file.Bytes == 0)
                return;
            if (_shared.TryGetValue(file.Bytes, out var bucket))
                bucket.Add(file);
            else if (_single.Remove(file.Bytes, out var first))
                _shared.Add(file.Bytes, [first, file]);
            else
                _single.Add(file.Bytes, file);
        }

        public DiskScanResult FindMatches(long visited, CancellationToken ct)
        {
            _single.Clear();
            var matches = new List<ScannedFile>();
            var groupNumber = 0;
            ct.ThrowIfCancellationRequested();
            foreach (var (size, sameSize) in _shared.OrderByDescending(bucket => bucket.Key))
            {
                ct.ThrowIfCancellationRequested();
                // Use a small sample to reject mismatches before reading large files in full.
                IEnumerable<ScannedFile> candidates = sameSize;
                if (size >= 256 * 1024)
                    candidates = sameSize.GroupBy(file => TryHash(file, ct, sampleOnly: true))
                        .Where(sample => sample.Key is not null && sample.Count() > 1)
                        .SelectMany(sample => sample);
                var hashed = candidates.Select(file => file with { Digest = TryHash(file, ct) })
                    .Where(file => file.Digest is not null);
                foreach (var group in hashed.GroupBy(file => file.Digest).Where(g => g.Count() > 1))
                {
                    var room = ResultLimit - matches.Count;
                    if (room < 2) return new DiskScanResult(matches, visited, true, null);
                    groupNumber++;
                    matches.AddRange(group.OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase)
                        .Take(room).Select(file => file with { Group = groupNumber }));
                    if (group.Count() > room) return new DiskScanResult(matches, visited, true, null);
                }
            }
            return new DiskScanResult(matches, visited, false, null);
        }
    }

    private static string? TryHash(ScannedFile file, CancellationToken ct, bool sampleOnly = false)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            if (!HasSafeParents(file.Path))
                return null;
            using var stream = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 1,
                sampleOnly ? FileOptions.RandomAccess : FileOptions.SequentialScan);
            if (!TryGetInfo(stream.SafeFileHandle, out var info) || info.Links != 1 || info.Size != file.Bytes ||
                info.LastWriteUtc != file.LastWriteUtc || (info.Attributes & (UnsafePath | FileAttributes.System)) != 0)
                return null;

            if (sampleOnly)
            {
                const int edge = 4096;
                Span<byte> sample = stackalloc byte[2 * edge];
                stream.ReadExactly(sample[..edge]);
                stream.Position = stream.Length - edge;
                stream.ReadExactly(sample[edge..]);
                return Convert.ToHexString(SHA256.HashData(sample));
            }

            return HashStream(stream, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string HashStream(FileStream stream, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var count = stream.Read(buffer);
                if (count == 0)
                    break;
                hash.AppendData(buffer, 0, count);
            }
            return Convert.ToHexString(hash.GetHashAndReset());
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static CleanupBatchResult Remove(DiskScanKind kind, IReadOnlyList<ScannedFile> all,
        IReadOnlyList<ScannedFile> selected, CancellationToken stop)
    {
        var removed = new List<string>();
        var errors = new List<string>();
        var selection = new HashSet<ScannedFile>(selected);
        var tempCutoff = DateTime.UtcNow.AddDays(-2);
        var scanned = all.ToHashSet();
        var groups = all.Where(file => file.Group.HasValue && file.Digest is not null).ToLookup(file => (file.Group, file.Digest));
        foreach (var batch in selected.Distinct().GroupBy(file => kind == DiskScanKind.Duplicate ? (file.Group, file.Digest) : default))
        {
            if (stop.IsCancellationRequested) break;
            using var retained = batch.Key.Group.HasValue ? OpenRetainedCopy(groups[batch.Key], selection, stop) : null;
            foreach (var file in batch)
            {
                if (stop.IsCancellationRequested)
                    break;

                try
                {
                    if (!scanned.Contains(file) || !Matches(file))
                        throw new IOException("File changed or moved since the scan.");

                    if (kind == DiskScanKind.Temporary)
                    {
                        if (!TempRoots.Any(root => IsChild(file.Path, root)) || file.LastWriteUtc > tempCutoff)
                            throw new IOException("File is outside the recognized temporary locations or was recently modified.");

                        DeleteFilePermanently(file);
                    }
                    else
                    {
                        if (IsProtected(file.Path) || ProtectedRootFiles.Contains(file.Path))
                            throw new IOException("Protected location.");

                        if (kind == DiskScanKind.Duplicate)
                        {
                            if (retained is null || file.Digest is null)
                                throw new IOException("A verified copy must be left in this group.");
                            if (TryHash(file, stop) != file.Digest)
                                throw new IOException("Duplicate file content changed.");
                        }
                        Recycle(file.Path);
                    }
                    removed.Add(file.Path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
                {
                    errors.Add($"{file.Path} — {ex.Message}");
                }
            }
        }
        return new CleanupBatchResult(removed, errors);
    }

    private static FileStream? OpenRetainedCopy(IEnumerable<ScannedFile> group, HashSet<ScannedFile> selected, CancellationToken ct)
    {
        foreach (var candidate in group)
        {
            if (ct.IsCancellationRequested) break;
            if (selected.Contains(candidate) || candidate.Digest is null || !Matches(candidate)) continue;
            FileStream? stream = null;
            try
            {
                stream = new FileStream(candidate.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (TryGetInfo(stream.SafeFileHandle, out var info) && info.Links == 1 &&
                    info.Size == candidate.Bytes && info.LastWriteUtc == candidate.LastWriteUtc &&
                    HashStream(stream, ct) == candidate.Digest)
                {
                    var verified = stream;
                    stream = null; // The caller holds the validated handle until this group's last removal.
                    return verified;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return null; }
            finally { stream?.Dispose(); }
        }
        return null;
    }

    internal static void Recycle(string path) =>
        FileSystem.DeleteFile(path, UIOption.AllDialogs, RecycleOption.SendToRecycleBin, UICancelOption.ThrowException);

    private static string RequireLocalFolder(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Path.IsPathFullyQualified(folder))
            throw new ArgumentException("Select an existing folder on a local fixed drive.");

        var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        if (!IsFixedDrive(path) || !Directory.Exists(path) || !HasSafeParents(path) || IsProtected(path))
            throw new ArgumentException("Select an accessible folder outside protected system and application locations.");
        return path;
    }

    internal static bool IsFixedDrive(string path)
    {
        var root = Path.GetPathRoot(path);
        return root is { Length: 3 } && root[1] == ':' && new DriveInfo(root).DriveType == DriveType.Fixed;
    }

    private static bool IsOnSystemDrive(string path) =>
        string.Equals(Path.GetPathRoot(path), SystemDrive, StringComparison.OrdinalIgnoreCase);

    private static bool IsProtected(string path) => ProtectedFolders.Any(root => IsInOrEqual(path, root)) ||
        IsChild(path, UserProfiles) && !IsInOrEqual(path, UserFolder) && !IsInOrEqual(path, PublicProfile);

    internal static bool IsInOrEqual(string path, string root) =>
        string.Equals(Path.TrimEndingDirectorySeparator(path), Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase)
        || IsChild(path, root);

    internal static bool IsChild(string path, string parent)
    {
        var child = Path.TrimEndingDirectorySeparator(path.AsSpan());
        var root = Path.TrimEndingDirectorySeparator(parent.AsSpan());
        return child.Length > root.Length && child.StartsWith(root, StringComparison.OrdinalIgnoreCase) &&
            (Path.EndsInDirectorySeparator(root) || child[root.Length] == Path.DirectorySeparatorChar ||
                child[root.Length] == Path.AltDirectorySeparatorChar);
    }

    internal static bool HasSafeParents(string path)
    {
        for (var current = path; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            var attributes = File.GetAttributes(current);
            if ((attributes & UnsafePath) != 0)
                return false;
        }
        return true;
    }

    internal static bool Matches(ScannedFile file)
    {
        try
        {
            var current = new FileInfo(file.Path);
            return current.Exists && current.Length == file.Bytes && current.LastWriteTimeUtc == file.LastWriteUtc &&
                HasSafeParents(file.Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal static IEnumerable<ScannedFile> WalkFiles(string root, Func<string, bool> skipFolder, CancellationToken ct,
        Action<string>? onDirectory = null)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = UnsafePath | FileAttributes.System };
        while (pending.TryPop(out var folder))
        {
            ct.ThrowIfCancellationRequested();
            IEnumerator<FileSystemInfo> entries;
            try
            {
                if (skipFolder(folder) || !HasSafeParents(folder))
                    continue;
                onDirectory?.Invoke(folder);
                entries = new DirectoryInfo(folder).EnumerateFileSystemInfos("*", options).GetEnumerator();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }

            using var iterator = entries;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                FileSystemInfo entry;
                try
                {
                    if (!iterator.MoveNext()) break;
                    entry = iterator.Current;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { break; }

                if (entry is DirectoryInfo)
                    pending.Push(entry.FullName);
                else if (entry is FileInfo file && ReadStamp(file) is { } stamp)
                    yield return stamp;
            }
        }
    }

    internal static ScannedFile? ReadStamp(FileInfo file)
    {
        try { return new ScannedFile(file.FullName, file.Length, file.LastWriteTimeUtc); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    internal static void DeleteFilePermanently(ScannedFile file)
    {
        const uint deleteAndReadAttributes = 0x10080;
        const uint openExisting = 3;
        const uint openReparsePoint = 0x00200000;
        using var handle = CreateFile(file.Path, deleteAndReadAttributes, FileShare.Read, IntPtr.Zero,
            openExisting, openReparsePoint, IntPtr.Zero);
        if (handle.IsInvalid || !TryGetInfo(handle, out var info) || info.Links != 1 ||
            info.Size != file.Bytes || info.LastWriteUtc != file.LastWriteUtc ||
            (info.Attributes & (UnsafePath | FileAttributes.Directory | FileAttributes.System)) != 0)
            throw new IOException("The file is in use, changed, or has another link.");

        var buffer = ArrayPool<char>.Shared.Rent(32768);
        try
        {
            var count = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Length, 0);
            if (count < 4 || count >= buffer.Length ||
                !buffer.AsSpan(0, 4).SequenceEqual(@"\\?\") ||
                !buffer.AsSpan(4, (int)count - 4).Equals(file.Path.AsSpan(), StringComparison.OrdinalIgnoreCase))
                throw new IOException("The path no longer resolves to the scanned file.");
        }
        finally
        {
            ArrayPool<char>.Shared.Return(buffer);
        }

        const int dispositionInfo = 4;
        const int dispositionInfoEx = 21;
        byte disposition = 1;
        uint readOnlyDisposition = 0x01 | 0x02 | 0x10; // Delete, POSIX semantics, ignore read-only.
        var removed = (info.Attributes & FileAttributes.ReadOnly) != 0
            ? SetFileDispositionEx(handle, dispositionInfoEx, ref readOnlyDisposition, sizeof(uint))
            : SetFileInformationByHandle(handle, dispositionInfo, ref disposition, sizeof(byte));
        if (!removed)
            throw new IOException("Windows refused to remove the file.",
                new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HandleInfo
    {
        public FileAttributes Attributes;
        public uint CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh;
        public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
        public readonly long Size => ((long)SizeHigh << 32) | SizeLow;
        public readonly DateTime LastWriteUtc => DateTime.FromFileTimeUtc(((long)WriteHigh << 32) | WriteLow);
    }

    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TryGetInfo(SafeFileHandle handle, out HandleInfo info);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, FileShare share, IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, [Out] char[] buffer, uint length, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int infoClass, ref byte info, int size);

    [DllImport("kernel32.dll", EntryPoint = "SetFileInformationByHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileDispositionEx(SafeFileHandle handle, int infoClass, ref uint info, int size);
}
