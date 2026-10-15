using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WinAppClear.Core.Util;

namespace WinAppClear.App.Infrastructure;

/// <summary>Extracts and caches program icons (from "path,index" DisplayIcon values, exes, .ico and .png files).</summary>
public static class IconCache
{
    private static readonly ConcurrentDictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static Task<ImageSource?> GetAsync(string? iconSpec, string? fallbackExe = null) =>
        Task.Run(() => Get(iconSpec) ?? Get(fallbackExe));

    public static ImageSource? Get(string? iconSpec)
    {
        if (string.IsNullOrWhiteSpace(iconSpec)) return null;
        return Cache.GetOrAdd(iconSpec, Load);
    }

    private static ImageSource? Load(string spec)
    {
        try
        {
            var value = Environment.ExpandEnvironmentVariables(spec.Trim().Trim('"'));
            var index = 0;
            var comma = value.LastIndexOf(',');
            if (comma > 0 && int.TryParse(value[(comma + 1)..].Trim(), out var i))
            {
                index = i;
                value = value[..comma].Trim().Trim('"');
            }
            if (!File.Exists(value)) return null;

            var ext = Path.GetExtension(value).ToLowerInvariant();
            if (ext is ".png" or ".jpg" or ".jpeg" or ".bmp")
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.DecodePixelWidth = 64;
                bmp.UriSource = new Uri(value);
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }

            var large = new IntPtr[1];
            var small = new IntPtr[1];
            var count = ExtractIconEx(value, index, large, small, 1);
            var handle = large[0] != IntPtr.Zero ? large[0] : small[0];
            if (count == 0 || handle == IntPtr.Zero)
            {
                // Fall back to the shell's icon for the file.
                var info = new ShFileInfo();
                if (SHGetFileInfo(value, 0, ref info, (uint)Marshal.SizeOf<ShFileInfo>(), 0x100 /* SHGFI_ICON */) == IntPtr.Zero ||
                    info.hIcon == IntPtr.Zero)
                    return null;
                handle = info.hIcon;
            }
            try
            {
                var source = Imaging.CreateBitmapSourceFromHIcon(handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                return source;
            }
            finally
            {
                if (large[0] != IntPtr.Zero) DestroyIcon(large[0]);
                if (small[0] != IntPtr.Zero) DestroyIcon(small[0]);
                if (handle != large[0] && handle != small[0]) DestroyIcon(handle);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Icon {spec}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Store app logos are declared as "Assets\Logo.png" but shipped as "Logo.scale-100.png" etc.</summary>
    public static string? ResolveStoreLogo(string? logoPath)
    {
        if (string.IsNullOrWhiteSpace(logoPath)) return null;
        if (File.Exists(logoPath)) return logoPath;
        var dir = Path.GetDirectoryName(logoPath);
        if (dir is null || !Directory.Exists(dir)) return null;
        var stem = Path.GetFileNameWithoutExtension(logoPath);
        try
        {
            return Directory.EnumerateFiles(dir, stem + "*.png")
                .OrderBy(f => f.Contains("scale-100") ? 0 : f.Contains("scale-200") ? 1 : 2)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string file, int index, IntPtr[]? large, IntPtr[]? small, uint count);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileInfo
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string path, uint attributes, ref ShFileInfo info, uint size, uint flags);
}
