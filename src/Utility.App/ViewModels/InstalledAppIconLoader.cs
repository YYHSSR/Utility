using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using Utility.Platform.Providers;

namespace Utility.App.ViewModels;

internal static class InstalledAppIconLoader
{
    private static readonly SemaphoreSlim Readers = new(2);
    private static readonly Lazy<ILookup<string, string>> StartMenuShortcuts = new(IndexStartMenuShortcuts);

    internal static async Task<ImageSource?> LoadAsync(UninstallTarget target)
    {
        await Readers.WaitAsync().ConfigureAwait(false);
        try
        {
            return await Task.Run(() => EnsureVisible((target.PackageName is null
                ? LoadDesktopAppIcon(target)
                : LoadShellAppIcon(target.PackageAppId) ?? LoadPackageIcon(target.PackageInstallLocation) ??
                  LoadUnlistedPackageIcon(target)) ?? FallbackIcon(target), target)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                   or NotSupportedException or System.Xml.XmlException or ExternalException
                                   or FormatException or InvalidOperationException)
        {
            return null;
        }
        finally { Readers.Release(); }
    }

    private static ImageSource? LoadDesktopAppIcon(UninstallTarget target)
    {
        var registration = target.App.DesktopRegistration;
        if (registration is null)
            return null;
        if (LoadDesktopIcon(registration.DisplayIcon) is { } registeredIcon)
            return registeredIcon;
        // MSI installs keep their ARP icon in the Windows Installer cache (ARPPRODUCTICON).
        if (registration.IsMsi && LoadDesktopIcon(MsiProductIcon(registration.RegistryKeyName)) is { } productIcon)
            return productIcon;

        var name = target.App.DisplayName;
        if (!string.IsNullOrWhiteSpace(target.App.Version) &&
            name.EndsWith(" " + target.App.Version, StringComparison.OrdinalIgnoreCase))
            name = name[..^(target.App.Version.Length + 1)];
        var appName = NormalizeName(name);
        var directory = Environment.ExpandEnvironmentVariables(registration.InstallLocation?.Trim('"', ' ') ?? "");
        if (IsLocal(directory) && Directory.Exists(directory))
        {
            var directoryName = NormalizeName(Path.GetFileName(Path.TrimEndingDirectorySeparator(directory)));
            foreach (var path in Directory.EnumerateFiles(directory, "*.exe").Take(128))
            {
                var stem = NormalizeName(Path.GetFileNameWithoutExtension(path));
                if ((stem.Equals(appName, StringComparison.OrdinalIgnoreCase) ||
                     stem.Equals(directoryName, StringComparison.OrdinalIgnoreCase)) &&
                    LoadDesktopIcon(path) is { } matchingIcon)
                    return matchingIcon;
            }
        }

        foreach (var shortcut in StartMenuShortcuts.Value[appName])
        {
            if (LoadShortcutIcon(shortcut) is { } startIcon)
                return startIcon;
        }

        // Last resort inside the app's own folder: the first real program (not an installer/updater helper)
        // in the folder itself, then in bin.
        if (IsLocal(directory) && Directory.Exists(directory) && Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(directory)) is { Length: > 3 })
        {
            foreach (var folder in new[] { directory, Path.Combine(directory, "bin") })
            {
                if (!Directory.Exists(folder))
                    continue;
                foreach (var path in Directory.EnumerateFiles(folder, "*.exe").Take(64).Where(path => !IsHelperExe(path)))
                {
                    if (LoadDesktopIcon(path) is { } folderIcon)
                        return folderIcon;
                }
            }
        }
        return null;
    }

    private static readonly string[] HelperExeWords =
        ["unins", "uninst", "setup", "install", "update", "crash", "report", "helper", "elevat", "service", "launcher_", "dump"];

    private static bool IsHelperExe(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        return HelperExeWords.Any(word => name.Contains(word, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Path of the icon an MSI product registered (ARPPRODUCTICON), or null.</summary>
    private static string? MsiProductIcon(string productCode)
    {
        if (!Guid.TryParse(productCode, out _))
            return null;
        var length = 1024u;
        var buffer = new char[length];
        return MsiGetProductInfo(productCode, "ProductIcon", buffer, ref length) == 0 && length > 0
            ? new string(buffer, 0, (int)length)
            : null;
    }

    /// <summary>
    /// "Family!AppId" from the package manifest. Sparse packages (assets outside the package folder) are
    /// listed without an app id, but the shell can still draw their tile icon from it.
    /// </summary>
    private static string? ManifestAppId(UninstallTarget target)
    {
        if (target.App.PackageFamilyName is not { } family || target.PackageInstallLocation is not { } root || !IsLocal(root))
            return null;
        var manifest = Path.Combine(root, "AppxManifest.xml");
        if (!File.Exists(manifest))
            return null;
        var id = XDocument.Load(manifest).Descendants().FirstOrDefault(element => element.Name.LocalName == "Application")?
            .Attribute("Id")?.Value;
        return string.IsNullOrWhiteSpace(id) ? null : family + "!" + id;
    }

    /// <summary>
    /// When an app has no icon anywhere: the Windows Installer icon for MSI products (as Control Panel shows),
    /// otherwise a letter tile, so no row is left blank.
    /// </summary>
    private static ImageSource? FallbackIcon(UninstallTarget target)
    {
        if (target.App.DesktopRegistration is { IsMsi: true } &&
            LoadDesktopIcon(Path.Combine(Environment.SystemDirectory, "msiexec.exe")) is { } installerIcon)
            return installerIcon;

        return LetterTile(target.App.DisplayName);
    }

    /// <summary>
    /// Packages listed without an app id. A sparse package uses the logos in its real folder; when that folder
    /// is gone (the owning app removed it), the shell would only hand back a blank page, so return nothing and
    /// let the letter tile stand in.
    /// </summary>
    private static ImageSource? LoadUnlistedPackageIcon(UninstallTarget target)
    {
        if (SparsePackageRoot(target) is { } sparse)
            return Directory.Exists(sparse)
                ? LoadPackageIcon(target.PackageInstallLocation, sparse) ?? LoadShellAppIcon(ManifestAppId(target))
                : null;
        return LoadShellAppIcon(ManifestAppId(target));
    }

    /// <summary>
    /// Sparse packages ("package with external location") keep their assets outside WindowsApps; Windows records
    /// that folder as PackageRootFolder in the per-user AppModel repository.
    /// </summary>
    private static string? SparsePackageRoot(UninstallTarget target)
    {
        if (string.IsNullOrWhiteSpace(target.PackageFullName) || target.PackageFullName.Contains('\\'))
            return null;
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
            @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages\" + target.PackageFullName);
        var root = key?.GetValue("PackageRootFolder") as string;
        return string.IsNullOrWhiteSpace(root) || string.Equals(Path.TrimEndingDirectorySeparator(root),
            Path.TrimEndingDirectorySeparator(target.PackageInstallLocation ?? ""), StringComparison.OrdinalIgnoreCase)
            ? null
            : root;
    }

    private static readonly System.Drawing.Color[] LetterTileColors =
    [
        System.Drawing.Color.FromArgb(0x4F, 0x7C, 0xF7), System.Drawing.Color.FromArgb(0x16, 0xA3, 0x4A),
        System.Drawing.Color.FromArgb(0xD9, 0x77, 0x06), System.Drawing.Color.FromArgb(0x93, 0x33, 0xEA),
        System.Drawing.Color.FromArgb(0x0E, 0x95, 0xA8), System.Drawing.Color.FromArgb(0xDB, 0x27, 0x77),
        System.Drawing.Color.FromArgb(0x64, 0x74, 0x8B)
    ];

    /// <summary>
    /// For an app that has no icon anywhere: a rounded tile with its first letter, colour picked from the name
    /// (stable between runs). Drawn with GDI+, which is safe on the loader's background threads.
    /// </summary>
    private static ImageSource? LetterTile(string name)
    {
        var letter = name.FirstOrDefault(char.IsLetterOrDigit);
        if (letter == default)
            letter = '?';
        var hash = 0;
        foreach (var c in name)
            hash = unchecked(hash * 31 + c);
        var color = LetterTileColors[(hash & 0x7FFFFFFF) % LetterTileColors.Length];

        using var bitmap = new System.Drawing.Bitmap(TileSize, TileSize, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
        using (var brush = new System.Drawing.SolidBrush(color))
        using (var path = new System.Drawing.Drawing2D.GraphicsPath())
        using (var font = new System.Drawing.Font("Microsoft YaHei UI", 15f, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Pixel))
        using (var format = new System.Drawing.StringFormat { Alignment = System.Drawing.StringAlignment.Center, LineAlignment = System.Drawing.StringAlignment.Center })
        {
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            const int d = 14;
            path.AddArc(0, 0, d, d, 180, 90);
            path.AddArc(TileSize - 1 - d, 0, d, d, 270, 90);
            path.AddArc(TileSize - 1 - d, TileSize - 1 - d, d, d, 0, 90);
            path.AddArc(0, TileSize - 1 - d, d, d, 90, 90);
            path.CloseFigure();
            graphics.FillPath(brush, path);
            graphics.DrawString(char.ToUpperInvariant(letter).ToString(), font, System.Drawing.Brushes.White,
                new System.Drawing.RectangleF(0, 1, TileSize, TileSize), format);
        }

        var data = bitmap.LockBits(new System.Drawing.Rectangle(0, 0, TileSize, TileSize),
            System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        try
        {
            var source = BitmapSource.Create(TileSize, TileSize, 96, 96, PixelFormats.Pbgra32, null,
                data.Scan0, data.Stride * TileSize, data.Stride);
            source.Freeze();
            return source;
        }
        finally { bitmap.UnlockBits(data); }
    }

    private const int TileSize = 32;

    /// <summary>
    /// Many system packages (codec extensions, winget sources) ship a white logo on a transparent background,
    /// meant to sit on a coloured Start tile; on the white table it is invisible. Like Start, put such logos on a
    /// rounded tile in the package's own tile colour (Windows blue when it declares none). Pixel work only, so it
    /// is safe on the loader's background (MTA) threads.
    /// </summary>
    private static ImageSource? EnsureVisible(ImageSource? icon, UninstallTarget target)
    {
        if (icon is not BitmapSource source || source.PixelWidth == 0 || source.PixelHeight == 0)
            return icon;
        var scaled = new FormatConvertedBitmap(source.PixelWidth == TileSize - 8 && source.PixelHeight == TileSize - 8
            ? source
            : new TransformedBitmap(source, new ScaleTransform((TileSize - 8.0) / source.PixelWidth, (TileSize - 8.0) / source.PixelHeight)),
            PixelFormats.Bgra32, null, 0);
        var size = scaled.PixelWidth;
        var logo = new byte[size * scaled.PixelHeight * 4];
        scaled.CopyPixels(logo, size * 4, 0);

        // Visible = noticeably darker than white and not see-through; under 4% of the area means "can't see it".
        var visible = 0;
        for (var i = 0; i < logo.Length; i += 4)
        {
            if (logo[i + 3] >= 40 && (0.114 * logo[i] + 0.587 * logo[i + 1] + 0.299 * logo[i + 2]) / 255 < 0.85)
                visible++;
        }
        if (visible >= logo.Length / 4 * 0.04)
            return icon;

        var (r, g, b) = ManifestTileColor(target) ?? ((byte)0x00, (byte)0x78, (byte)0xD4);
        var tile = new byte[TileSize * TileSize * 4];
        const double radius = 7;
        for (var y = 0; y < TileSize; y++)
        for (var x = 0; x < TileSize; x++)
        {
            // Rounded-square coverage with a 1px soft edge.
            var dx = Math.Max(Math.Max(radius - x - 0.5, x + 0.5 - (TileSize - radius)), 0);
            var dy = Math.Max(Math.Max(radius - y - 0.5, y + 0.5 - (TileSize - radius)), 0);
            var coverage = Math.Clamp(radius - Math.Sqrt(dx * dx + dy * dy) + 0.5, 0, 1);
            double outB = b, outG = g, outR = r;
            var lx = x - (TileSize - size) / 2;
            var ly = y - (TileSize - scaled.PixelHeight) / 2;
            if (lx >= 0 && ly >= 0 && lx < size && ly < scaled.PixelHeight)
            {
                var j = (ly * size + lx) * 4;
                var a = logo[j + 3] / 255.0;
                outB = logo[j] * a + b * (1 - a);
                outG = logo[j + 1] * a + g * (1 - a);
                outR = logo[j + 2] * a + r * (1 - a);
            }
            var k = (y * TileSize + x) * 4;
            // Premultiplied BGRA.
            tile[k] = (byte)(outB * coverage);
            tile[k + 1] = (byte)(outG * coverage);
            tile[k + 2] = (byte)(outR * coverage);
            tile[k + 3] = (byte)(255 * coverage);
        }
        var result = BitmapSource.Create(TileSize, TileSize, 96, 96, PixelFormats.Pbgra32, null, tile, TileSize * 4);
        result.Freeze();
        return result;
    }

    /// <summary>The package's declared tile colour (VisualElements BackgroundColor), unless it is transparent.</summary>
    private static (byte R, byte G, byte B)? ManifestTileColor(UninstallTarget target)
    {
        if (target.PackageInstallLocation is not { } root || !IsLocal(root))
            return null;
        var manifest = Path.Combine(root, "AppxManifest.xml");
        if (!File.Exists(manifest))
            return null;
        var value = XDocument.Load(manifest).Descendants()
            .FirstOrDefault(element => element.Name.LocalName == "VisualElements")?.Attribute("BackgroundColor")?.Value;
        if (string.IsNullOrWhiteSpace(value) || value.Equals("transparent", StringComparison.OrdinalIgnoreCase))
            return null;
        try
        {
            var color = (Color)ColorConverter.ConvertFromString(value);
            // A near-white declared colour would hide the logo again.
            return color.R + color.G + color.B > 690 ? null : (color.R, color.G, color.B);
        }
        catch (FormatException) { return null; }
    }

    [DllImport("msi.dll", CharSet = CharSet.Unicode, EntryPoint = "MsiGetProductInfoW")]
    private static extern uint MsiGetProductInfo(string product, string property, [Out] char[] value, ref uint length);

    private static string NormalizeName(string name) => string.Concat(name.Where(char.IsLetterOrDigit));

    private static ILookup<string, string> IndexStartMenuShortcuts()
    {
        var links = new List<string>();
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            MaxRecursionDepth = 5,
            AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Offline
        };
        foreach (var folder in new[] { Environment.SpecialFolder.Programs, Environment.SpecialFolder.CommonPrograms })
        {
            var root = Environment.GetFolderPath(folder);
            if (IsLocal(root) && Directory.Exists(root))
                links.AddRange(Directory.EnumerateFiles(root, "*.lnk", options).Take(1500));
        }
        return links.ToLookup(link => NormalizeName(Path.GetFileNameWithoutExtension(link)), StringComparer.OrdinalIgnoreCase);
    }

    private static ImageSource? LoadShortcutIcon(string path)
    {
        var type = Type.GetTypeFromProgID("WScript.Shell");
        if (type is null || Activator.CreateInstance(type) is not { } shell)
            return null;
        object? link = null;
        try
        {
            link = ((dynamic)shell).CreateShortcut(path);
            return LoadDesktopIcon((string)((dynamic)link).IconLocation) ??
                LoadDesktopIcon((string)((dynamic)link).TargetPath);
        }
        finally
        {
            if (link is not null)
                Marshal.ReleaseComObject(link);
            Marshal.ReleaseComObject(shell);
        }
    }

    private static ImageSource? LoadDesktopIcon(string? specification)
    {
        if (string.IsNullOrWhiteSpace(specification))
            return null;
        var path = specification.Trim();
        var index = 0;
        var comma = path.LastIndexOf(',');
        if (comma > 0 && int.TryParse(path.AsSpan(comma + 1), out var resourceIndex))
        {
            path = path[..comma].TrimEnd();
            index = resourceIndex;
        }
        path = Environment.ExpandEnvironmentVariables(path.Trim('"'));
        if (!IsLocal(path) || !File.Exists(path))
            return null;
        if (Path.GetExtension(path).ToLowerInvariant() is ".ico" or ".png" or ".jpg" or ".bmp")
            return Decode(path);

        _ = ExtractIconEx(path, index, out var large, out var small, 1);
        var icon = large != IntPtr.Zero ? large : small;
        if (large != IntPtr.Zero && small != IntPtr.Zero)
            DestroyIcon(small);
        if (icon == IntPtr.Zero)
            return null;
        try
        {
            var source = Imaging.CreateBitmapSourceFromHIcon(icon, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(32, 32));
            source.Freeze();
            return source;
        }
        finally { DestroyIcon(icon); }
    }

    /// <param name="manifestRoot">Folder holding AppxManifest.xml.</param>
    /// <param name="assetRoot">Folder the logo paths are relative to; the manifest folder unless the package is sparse.</param>
    private static ImageSource? LoadPackageIcon(string? manifestRoot, string? assetRoot = null)
    {
        if (manifestRoot is null || !IsLocal(manifestRoot) || assetRoot is not null && (!IsLocal(assetRoot) || !Directory.Exists(assetRoot)))
            return null;
        var manifestPath = Path.Combine(manifestRoot, "AppxManifest.xml");
        if (!File.Exists(manifestPath))
            return null;
        var package = XDocument.Load(manifestPath).Root;
        if (package is null)
            return null;
        var root = assetRoot ?? manifestRoot;

        var logos = package.Descendants().Where(element => element.Name.LocalName == "VisualElements")
            .Select(element => (string?)element.Attribute("Square44x44Logo"))
            .Append(package.Elements().FirstOrDefault(element => element.Name.LocalName == "Properties")?
                .Elements().FirstOrDefault(element => element.Name.LocalName == "Logo")?.Value);
        var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        foreach (var logo in logos)
        {
            if (string.IsNullOrWhiteSpace(logo) || Path.IsPathRooted(logo) || logo.Contains(':'))
                continue;
            var path = Path.GetFullPath(Path.Combine(root, logo));
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;
            if (File.Exists(path))
                return Decode(path);
            var directory = Path.GetDirectoryName(path)!;
            if (!Directory.Exists(directory))
                continue;
            var stem = Path.GetFileNameWithoutExtension(path);
            var alternate = Directory.EnumerateFiles(directory, stem + ".*" + Path.GetExtension(path))
                .OrderByDescending(candidate => Path.GetFileName(candidate).Contains("targetsize-32", StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(candidate => Path.GetFileName(candidate).Contains("altform-unplated", StringComparison.OrdinalIgnoreCase))
                .FirstOrDefault();
            if (alternate is not null)
                return Decode(alternate);
        }
        return null;
    }

    private static ImageSource? LoadShellAppIcon(string? appId)
    {
        if (string.IsNullOrWhiteSpace(appId) || !appId.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-' or '!'))
            return null;
        var apartment = CoInitializeEx(IntPtr.Zero, 0);
        try
        {
            var iid = typeof(IShellItemImageFactory).GUID;
            if (SHCreateItemFromParsingName("shell:AppsFolder\\" + appId, IntPtr.Zero, in iid, out var factory) < 0)
                return null;
            try
            {
                if (factory.GetImage(new IconSize(32, 32), 4, out var bitmap) < 0 || bitmap == IntPtr.Zero)
                    return null;
                try
                {
                    var source = Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero, Int32Rect.Empty,
                        BitmapSizeOptions.FromEmptyOptions());
                    source.Freeze();
                    return source;
                }
                finally { DeleteObject(bitmap); }
            }
            finally { Marshal.ReleaseComObject(factory); }
        }
        finally
        {
            if (apartment >= 0)
                CoUninitialize();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct IconSize(int Width, int Height);

    [ComImport, Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(IconSize size, int flags, out IntPtr bitmap);
    }

    private static bool IsLocal(string path) => Path.IsPathFullyQualified(path) &&
        Path.GetPathRoot(path) is { Length: 3 } root && root[1] == ':' && new DriveInfo(root).DriveType == DriveType.Fixed;

    private static BitmapImage Decode(string path)
    {
        using var stream = File.OpenRead(path);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.DecodePixelWidth = 32;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string file, int iconIndex, out IntPtr largeIcon, out IntPtr smallIcon, uint count);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr context, in Guid iid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory factory);

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr reserved, uint flags);

    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr bitmap);
}
