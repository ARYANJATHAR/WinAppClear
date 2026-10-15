using System.Diagnostics;
using System.Windows;
using WinAppClear.Core.Util;
using Microsoft.Win32;

namespace WinAppClear.App.Infrastructure;

/// <summary>Thin wrappers over message boxes and file pickers so view models stay simple.</summary>
public static class Dialogs
{
    private static Window? Owner => Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive)
                                    ?? Application.Current?.MainWindow;

    public static bool Confirm(string message, string title = "WinAppClear", bool warning = false)
    {
        var owner = Owner;
        var icon = warning ? MessageBoxImage.Warning : MessageBoxImage.Question;
        var result = owner is null
            ? MessageBox.Show(message, title, MessageBoxButton.YesNo, icon, MessageBoxResult.No)
            : MessageBox.Show(owner, message, title, MessageBoxButton.YesNo, icon, MessageBoxResult.No);
        return result == MessageBoxResult.Yes;
    }

    public static void Info(string message, string title = "WinAppClear")
    {
        var owner = Owner;
        if (owner is null) MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);
        else MessageBox.Show(owner, message, title, MessageBoxButton.OK, MessageBoxImage.Information);
    }

    public static void Error(string message, string title = "WinAppClear")
    {
        var owner = Owner;
        if (owner is null) MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);
        else MessageBox.Show(owner, message, title, MessageBoxButton.OK, MessageBoxImage.Error);
    }

    public static string? PickFile(string filter = "All files|*.*", string? title = null)
    {
        var dlg = new OpenFileDialog { Filter = filter, Title = title ?? "Choose a file", CheckFileExists = true };
        return dlg.ShowDialog(Owner) == true ? dlg.FileName : null;
    }

    public static string[] PickFiles(string? title = null)
    {
        var dlg = new OpenFileDialog { Filter = "All files|*.*", Title = title ?? "Choose files", Multiselect = true };
        return dlg.ShowDialog(Owner) == true ? dlg.FileNames : [];
    }

    public static string? PickFolder(string? title = null)
    {
        var dlg = new OpenFolderDialog { Title = title ?? "Choose a folder" };
        return dlg.ShowDialog(Owner) == true ? dlg.FolderName : null;
    }

    public static void OpenInExplorer(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            if (File.Exists(path)) Process.Start("explorer.exe", $"/select,\"{path}\"");
            else if (Directory.Exists(path)) Process.Start("explorer.exe", $"\"{path}\"");
            else Info($"This location no longer exists:\n{path}");
        }
        catch (Exception ex)
        {
            Log.Warn($"Explorer: {ex.Message}");
        }
    }

    public static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn($"Open URL: {ex.Message}");
        }
    }

    /// <summary>Opens Registry Editor at a key (regedit remembers the last key it showed).</summary>
    public static void OpenRegedit(string keyPath)
    {
        try
        {
            var (hive, sub) = RegistryPath.Parse(keyPath);
            var full = hive switch
            {
                RegistryHive.LocalMachine => "HKEY_LOCAL_MACHINE",
                RegistryHive.CurrentUser => "HKEY_CURRENT_USER",
                RegistryHive.ClassesRoot => "HKEY_CLASSES_ROOT",
                RegistryHive.Users => "HKEY_USERS",
                _ => "Computer",
            };
            using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Applets\Regedit"))
                key.SetValue("LastKey", $@"Computer\{full}\{sub}");
            foreach (var p in Process.GetProcessesByName("regedit")) p.Kill();
            Process.Start(new ProcessStartInfo("regedit.exe", "-m") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn($"Regedit: {ex.Message}");
        }
    }

    public static void CopyText(string text)
    {
        try { Clipboard.SetText(text); } catch { /* clipboard busy */ }
    }
}
