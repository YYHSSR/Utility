using Utility.Platform.Providers;
using Xunit;

namespace Utility.Tests;

public sealed class DiskCleanupServiceTests
{
    [Fact]
    public async Task CombinedScanRetainsSeparateResultsForOverlappingFolders()
    {
        var root = Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, $"UtilityCleanup-{Guid.NewGuid():N}"));
        var inner = root.CreateSubdirectory("duplicates");
        var a = Path.Combine(inner.FullName, "a.txt");
        var b = Path.Combine(inner.FullName, "b.txt");
        var outside = Path.Combine(root.FullName, "outside.txt");
        var duplicates = new[] { a, b };
        try
        {
            foreach (var path in duplicates.Append(outside))
                File.WriteAllText(path, "same content");

            var disk = new DiskCleanupService();
            var report = await disk.ScanPersonalAsync(root.FullName, inner.FullName + Path.DirectorySeparatorChar,
                1, null, CancellationToken.None);

            Assert.Equal(3, report.Large.Visited);
            Assert.Equal(2, report.Duplicate.Visited);
            Assert.Equal(3, report.Large.Files.Count);
            Assert.Equal(duplicates, report.Duplicate.Files.Select(file => file.Path));
        }
        finally
        {
            foreach (var path in duplicates.Append(outside))
                File.Delete(path);
            inner.Delete();
            root.Delete();
        }
    }

    [Fact]
    public async Task SampleMatchesRequireAFullHashAndAChangedKeeperBlocksRemoval()
    {
        var folder = Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, $"UtilityCleanup-{Guid.NewGuid():N}"));
        var paths = new[] { "a.bin", "b.bin", "different.bin" }.Select(name => Path.Combine(folder.FullName, name)).ToArray();
        try
        {
            var bytes = new byte[300 * 1024];
            File.WriteAllBytes(paths[0], bytes);
            File.WriteAllBytes(paths[1], bytes);
            bytes[bytes.Length / 2] = 1;
            File.WriteAllBytes(paths[2], bytes);
            var disk = new DiskCleanupService();
            var scan = await disk.ScanAsync(DiskScanKind.Duplicate, folder.FullName, 0, null, default);
            Assert.Equal(paths.Take(2), scan.Files.Select(file => file.Path));

            // Preserve both length and timestamp to force a content check on the retained copy.
            File.WriteAllBytes(paths[0], bytes);
            File.SetLastWriteTimeUtc(paths[0], scan.Files[0].LastWriteUtc);
            var result = await disk.RemoveAsync(DiskScanKind.Duplicate, scan.Files, [scan.Files[1]]);
            Assert.Empty(result.Removed);
            Assert.Single(result.Errors);
            Assert.True(File.Exists(paths[1]));
        }
        finally
        {
            foreach (var path in paths) File.Delete(path);
            folder.Delete();
        }
    }

    [Fact]
    public async Task DuplicateScanHashesFileContentsAndNeverDeletesEveryCopy()
    {
        var folder = Directory.CreateTempSubdirectory("UtilityCleanupTests-");
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var fixturePath = folder.FullName.StartsWith(localAppData, StringComparison.OrdinalIgnoreCase)
            ? Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, folder.Name)).FullName
            : folder.FullName;
        var paths = new[] { "version-a.txt", "version-b.txt", "different.txt" }
            .Select(name => Path.Combine(fixturePath, name)).ToArray();
        try
        {
            File.WriteAllText(paths[0], "same contents");
            File.WriteAllText(paths[1], "same contents");
            File.WriteAllText(paths[2], "else contents");
            var disk = new DiskCleanupService();
            var scan = await disk.ScanAsync(DiskScanKind.Duplicate, fixturePath, 1, null, CancellationToken.None);
            Assert.Equal(paths.Take(2), scan.Files.Select(item => item.Path));
            Assert.Single(scan.Files.Select(item => item.Group).Distinct());

            var result = await disk.RemoveAsync(DiskScanKind.Duplicate, scan.Files, scan.Files);
            Assert.Empty(result.Removed);
            Assert.Equal(2, result.Errors.Count);
            Assert.All(paths, path => Assert.True(File.Exists(path)));
        }
        finally
        {
            foreach (var path in paths)
                File.Delete(path);
            if (fixturePath != folder.FullName)
                Directory.Delete(fixturePath);
            folder.Delete();
        }
    }
}
