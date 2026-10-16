using WinAppClear.Core.Cleanup;
using WinAppClear.Core.History;
using WinAppClear.Core.Leftovers;
using WinAppClear.Core.Models;
using WinAppClear.Core.Monitor;
using WinAppClear.Core.Safety;
using WinAppClear.Core.Uninstall;
using WinAppClear.Core.Util;
using Microsoft.Win32;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace WinAppClear.Core.Tests;

// All writes stay in this test's generated sandbox; no real uninstallers or registry writes.
public sealed class DemoDataTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "WinAppClear-DemoTests", Guid.NewGuid().ToString("N"));
    public DemoDataTests()
    {
        Directory.CreateDirectory(root);
        AppPaths.OverrideRoot(Path.Combine(root, "AppData"));
    }

    public void Dispose()
    {
        AppPaths.OverrideRoot(null);
        var allowed = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "WinAppClear-DemoTests")) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(root).StartsWith(allowed, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Test cleanup escaped its sandbox.");
        Directory.Delete(root, true);
    }

    private string DemoFile(string relative, string content = "Demo application data")
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private static InstalledProgram Program(string? folder = null, string? command = null,
        string? code = null, bool msi = false, string name = "Nebula Editor") => new()
    {
        Id = "demo-nebula", DisplayName = name, Publisher = "Nebula Labs Inc.",
        InstallLocation = folder, UninstallString = command, MsiProductCode = code,
        IsWindowsInstaller = msi, RegistryKeyPath = @"HKCU\Software\WinAppClear-Demo-NotRegistered",
        Hive = RegistryHive.CurrentUser, View = RegistryView.Registry64, KeyName = "demo-nebula",
    };

    [Fact]
    public void RenamedAppUsesNewDataDirectoryForFreshInstall()
    {
        Assert.Equal(Path.Combine(root, "WinAppClear"), AppPaths.ResolveDefaultRoot(root));
    }

    [Fact]
    public void RenamedAppPreservesLegacyDataAndBackupPaths()
    {
        var legacy = Path.Combine(root, "ClearOut");
        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, "settings.json"), "legacy-settings");
        Assert.Equal(legacy, AppPaths.ResolveDefaultRoot(root));
        Assert.Equal("legacy-settings", File.ReadAllText(Path.Combine(legacy, "settings.json")));
        Directory.CreateDirectory(Path.Combine(root, "WinAppClear"));
        Assert.Equal(Path.Combine(root, "WinAppClear"), AppPaths.ResolveDefaultRoot(root));
    }

    [Fact]
    public void RenamedAssemblyLoadsEmbeddedBlocklist()
    {
        var list = WinAppClear.Core.Health.Blocklist.Load();
        Assert.NotEmpty(list.Entries);
        Assert.NotNull(list.Match("Ask Toolbar"));
    }

    [Fact]
    public void LowercaseUserBlocklistIsLoadedAndMatched()
    {
        File.WriteAllText(AppPaths.UserBlocklistFile,
            """{"entries":[{"pattern":"^DemoUnwantedApp$","type":"PUP","description":"Demo finding"}]}""");
        var match = WinAppClear.Core.Health.Blocklist.Load().Match("DemoUnwantedApp");
        Assert.NotNull(match);
        Assert.Equal("Demo finding", match.Description);
    }

    private static LeftoverItem Item(string path, LeftoverKind kind = LeftoverKind.File,
        Confidence confidence = Confidence.High) => new()
    {
        Path = path, Kind = kind, Confidence = confidence, Reason = "Synthetic test item",
    };

    private LeftoverScanner Scanner(string folderRoot, params InstalledProgram[] others) => new(new()
    {
        FolderRoots = [folderRoot], RegistryRoots = [], IncludeSystemItems = false, MeasureSizes = true,
    }, others);

    [Theory]
    [InlineData("Nebula Editor 2.3 (x64)", "nebulaeditor")]
    [InlineData("NebulaEditor [Portable]", "nebulaeditor")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void NormalizesDemoNames(string? input, string expected) => Assert.Equal(expected, NameMatcher.Normalize(input));

    [Theory]
    [InlineData("Nebula Software Inc.", "nebula")]
    [InlineData("Nebula Labs LLC", "nebula")]
    public void NormalizesPublishers(string input, string expected) => Assert.Equal(expected, NameMatcher.NormalizePublisher(input));

    [Theory]
    [InlineData("Common Files")]
    [InlineData("Microsoft")]
    [InlineData("Shared")]
    [InlineData("Temp")]
    public void GenericFoldersNeverMatch(string name) => Assert.Equal(0, NameMatcher.Score(name, [NameMatcher.Normalize(name)]));

    [Theory]
    [InlineData("Nebula Editor", 2)]
    [InlineData("Nebula Editor Helper", 1)]
    [InlineData("Unrelated Tool", 0)]
    public void ScoresDemoMatches(string name, int expected) => Assert.Equal(expected, NameMatcher.Score(name, ["nebulaeditor"]));

    [Theory]
    [InlineData(@"C:\Apps\Nebula\editor.exe", @"C:\Apps\Nebula", true)]
    [InlineData(@"C:\Apps\NebulaOther\editor.exe", @"C:\Apps\Nebula", false)]
    [InlineData(@"c:\apps\NEBULA", @"C:\Apps\Nebula", true)]
    [InlineData(@"C:\Apps\Nebula\editor.exe", @"C:\", true)]
    public void PathContainmentHonorsBoundaries(string path, string parent, bool expected) =>
        Assert.Equal(expected, PathUtil.IsUnderOrEqual(path, parent));

    [Theory]
    [InlineData("relative\\file.exe")]
    [InlineData("")]
    [InlineData(null)]
    public void RejectsNonAbsolutePaths(string? path) => Assert.Null(PathUtil.Normalize(path));

    [Theory]
    [InlineData("\"C:\\Demo App\\remove.exe\" /quiet", @"C:\Demo App\remove.exe", "/quiet")]
    [InlineData("C:\\Demo App\\remove.exe /quiet", @"C:\Demo App\remove.exe", "/quiet")]
    [InlineData("msiexec.exe /x {00112233-4455-6677-8899-AABBCCDDEEFF}", "msiexec.exe", "/x {00112233-4455-6677-8899-AABBCCDDEEFF}")]
    public void ParsesCommandsWithoutExecuting(string input, string executable, string arguments)
    {
        var command = UninstallCommand.Parse(input);
        Assert.NotNull(command);
        Assert.Equal(executable, command.FileName);
        Assert.Equal(arguments, command.Arguments);
    }

    [Fact]
    public void MsiCommandsUseUninstallAndQuietFlags()
    {
        const string code = "{00112233-4455-6677-8899-AABBCCDDEEFF}";
        var p = Program(command: "msiexec.exe /i " + code, code: code, msi: true);
        Assert.Equal("/x " + code, UninstallCommand.Normal(p)!.Arguments);
        Assert.Equal("/x " + code + " /qn /norestart", UninstallCommand.Quiet(p)!.Arguments);
        Assert.Equal("/fa " + code, UninstallCommand.Repair(p)!.Arguments);
    }

    [Fact]
    public void InnoQuietFlagsAreGenerated() =>
        Assert.Contains("/VERYSILENT", UninstallCommand.Quiet(Program(command: @"C:\Demo\unins000.exe"))!.Arguments);

    [Fact]
    public void UnknownUninstallerDoesNotInventSilentFlags() =>
        Assert.Null(UninstallCommand.Quiet(Program(command: @"C:\Demo\unknown.exe")));

    [Theory]
    [InlineData(@"C:\")]
    [InlineData(@"C:\Windows")]
    [InlineData(@"C:\Program Files")]
    [InlineData(@"\\server\share\app")]
    [InlineData("relative")]
    public void ProtectsDangerousPaths(string path) => Assert.NotNull(SafetyGuard.ProtectedPathReason(path));

    [Fact]
    public void ProtectsRealKnownFoldersWithoutChangingThem()
    {
        Assert.True(SafetyGuard.IsProtectedPath(KnownPaths.Windows));
        Assert.True(SafetyGuard.IsProtectedPath(KnownPaths.UserProfile));
        Assert.True(SafetyGuard.IsProtectedPath(KnownPaths.Documents));
        Assert.Null(SafetyGuard.ProtectedPathReason(Path.Combine(root, "Nebula")));
    }

    [Theory]
    [InlineData(@"HKLM\SOFTWARE")]
    [InlineData(@"HKLM\SYSTEM\CurrentControlSet\Services\WinDefend")]
    [InlineData(@"HKCU\Software\Microsoft")]
    [InlineData(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall")]
    [InlineData("invalid")]
    public void ProtectsSharedRegistryKeysWithoutAccessingRegistry(string path) => Assert.True(SafetyGuard.IsProtectedRegistryKey(path));

    [Fact]
    public void AllowsSpecificDemoRegistryKeysAndStartupValues()
    {
        Assert.False(SafetyGuard.IsProtectedRegistryKey(@"HKCU\Software\NebulaEditor"));
        Assert.False(SafetyGuard.IsProtectedRegistryKey(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\NebulaEditor"));
        Assert.False(SafetyGuard.IsProtectedRegistryValue(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Run", "NebulaEditor"));
        Assert.True(SafetyGuard.IsProtectedRegistryValue(@"HKCU\Software\NebulaEditor", ""));
    }

    [Fact]
    public void ProtectsWindowsServicesAndTasks()
    {
        Assert.NotNull(SafetyGuard.Check(Item("WinDefend", LeftoverKind.Service)));
        Assert.NotNull(SafetyGuard.Check(Item(@"\Microsoft\Windows\Demo", LeftoverKind.ScheduledTask)));
        Assert.Null(SafetyGuard.Check(Item("NebulaDemoService", LeftoverKind.Service)));
    }

    [Fact]
    public void ScannerFindsOnlyMatchingDemoFoldersAndMeasuresBytes()
    {
        var file = DemoFile(@"Apps\Nebula Editor\settings.json", "12345");
        DemoFile(@"Apps\Unrelated Tool\keep.txt");
        DemoFile(@"Apps\Common Files\keep.txt");
        var found = Scanner(Path.Combine(root, "Apps")).Scan(ScanTarget.FromFolder(Path.GetDirectoryName(file)!));
        var item = Assert.Single(found);
        Assert.Equal(Path.GetDirectoryName(file), item.Path);
        Assert.Equal(Confidence.High, item.Confidence);
        Assert.Equal(5L, item.SizeBytes);
    }

    [Fact]
    public void ScannerPreservesFoldersOwnedByAnotherInstalledProgram()
    {
        var file = DemoFile(@"Apps\Nebula Editor\SharedTool\keep.txt");
        var target = Directory.GetParent(Path.GetDirectoryName(file)!)!.FullName;
        var other = Program(folder: Path.GetDirectoryName(file), name: "Other Tool");
        Assert.Empty(Scanner(Path.Combine(root, "Apps"), other).Scan(ScanTarget.FromFolder(target)));
    }

    [Fact]
    public void ScannerCancellationStopsBeforeFolderScan()
    {
        Directory.CreateDirectory(Path.Combine(root, "Apps"));
        Assert.Throws<OperationCanceledException>(() => Scanner(Path.Combine(root, "Apps")).Scan(
            ScanTarget.FromFolder(Path.Combine(root, "Missing")), new CancellationToken(true)));
    }

    [Fact]
    public void DemoCleanupBacksUpAndRestoresFileAndFolder()
    {
        var file = DemoFile(@"Nebula\settings.json", "original settings");
        var cacheFile = DemoFile(@"Cache\nested\cache.bin", "cached bytes");
        var cache = Path.Combine(root, "Cache");
        var report = CleanupExecutor.Execute("Demo cleanup", [Item(file), Item(cache, LeftoverKind.Folder)]);
        Assert.Equal(2, report.Removed);
        Assert.Equal(0, report.Failed);
        Assert.False(File.Exists(file));
        Assert.False(Directory.Exists(cache));
        var backup = Assert.Single(BackupService.List());
        Assert.Equal(report.BackupId, backup.Id);
        Assert.Equal(2, backup.Entries.Count);
        var restored = BackupService.Restore(backup);
        Assert.Equal(2, restored.Restored);
        Assert.Equal(0, restored.Failed);
        Assert.Equal("original settings", File.ReadAllText(file));
        Assert.Equal("cached bytes", File.ReadAllText(cacheFile));
        Assert.Equal(0, BackupService.Restore(backup).Restored);
    }

    [Fact]
    public void CancelledCleanupLeavesDemoDataUntouched()
    {
        var file = DemoFile("keep.txt");
        var report = CleanupExecutor.Execute("Cancelled demo", [Item(file)], ct: new CancellationToken(true));
        Assert.Equal(1, report.Skipped);
        Assert.True(File.Exists(file));
        Assert.Empty(BackupService.List());
    }

    [Fact]
    public void ProtectedCleanupIsSkippedBeforeMutation()
    {
        var report = CleanupExecutor.Execute("Protected demo", [Item(KnownPaths.Windows, LeftoverKind.Folder)]);
        Assert.Equal(1, report.Skipped);
        Assert.Equal(0, report.Removed);
        Assert.Empty(BackupService.List());
    }

    [Fact]
    public void RestoreDoesNotOverwriteRecreatedFile()
    {
        var file = DemoFile("settings.json", "old");
        CleanupExecutor.Execute("Demo", [Item(file)]);
        File.WriteAllText(file, "new");
        var report = BackupService.Restore(Assert.Single(BackupService.List()));
        Assert.Equal(1, report.Failed);
        Assert.Equal("new", File.ReadAllText(file));
    }

    [Fact]
    public void RestoreDoesNotOverwriteRecreatedFilesInsideFolder()
    {
        var file = DemoFile(@"Nebula\settings.json", "old");
        var folder = Path.GetDirectoryName(file)!;
        CleanupExecutor.Execute("Demo", [Item(folder, LeftoverKind.Folder)]);
        Directory.CreateDirectory(folder);
        File.WriteAllText(file, "new");
        BackupService.Restore(Assert.Single(BackupService.List()));
        Assert.Equal("new", File.ReadAllText(file));
    }

    [Fact]
    public void FailedBackupCopyDoesNotClaimRestorableBackup()
    {
        var file = DemoFile("locked.bin");
        using var locked = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var session = BackupService.Begin("Locked demo");
        Assert.Null(BackupService.CopyPath(session, LeftoverKind.File, file));
        Assert.Empty(session.Entries);
    }

    [Fact]
    public void LockedFileCleanupRefusesDeletionWithoutBackup()
    {
        var file = DemoFile("locked-cleanup.bin", "keep me");
        using var locked = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var report = CleanupExecutor.Execute("Locked demo", [Item(file)]);
        Assert.Equal(1, report.Failed);
        Assert.Equal(0, report.Scheduled);
        Assert.True(File.Exists(file));
        Assert.Empty(BackupService.List());
    }

    [Fact]
    public void FolderRestoreRetainsConflictsAndCanBeRetried()
    {
        var file = DemoFile(@"RestoreDemo\settings.json", "old");
        var other = DemoFile(@"RestoreDemo\nested\data.bin", "data");
        var folder = Path.GetDirectoryName(file)!;
        CleanupExecutor.Execute("Restore demo", [Item(folder, LeftoverKind.Folder)]);
        Directory.CreateDirectory(folder);
        File.WriteAllText(file, "new");
        var backup = Assert.Single(BackupService.List());
        var first = BackupService.Restore(backup);
        Assert.Equal(1, first.Failed);
        Assert.False(backup.Restored);
        Assert.Equal("new", File.ReadAllText(file));
        Assert.Equal("data", File.ReadAllText(other));
        Assert.Equal("old", File.ReadAllText(Path.Combine(backup.Entries[0].BackupFile!, "settings.json")));
        File.Delete(file); // remove only the generated conflicting demo file, then retry
        Assert.Equal(1, BackupService.Restore(backup).Restored);
        Assert.Equal("old", File.ReadAllText(file));
        Assert.True(backup.Restored);
    }

    [Fact]
    public void HistorySupportsDemoCreateUpdateRemove()
    {
        var entry = new HistoryEntry { Name = "Nebula Editor", Method = RemovalMethod.Forced };
        HistoryStore.Add(entry);
        entry.Result = "Demo complete";
        HistoryStore.Update(entry);
        Assert.Equal("Demo complete", Assert.Single(HistoryStore.All()).Result);
        HistoryStore.Remove(entry.Id);
        Assert.Empty(HistoryStore.All());
    }

    [Fact]
    public void WorkflowRecordsRemovedAndRemainingDemoItems()
    {
        var removed = Item(Path.Combine(root, "removed.bin")); removed.SizeBytes = 42;
        var remaining = Item(Path.Combine(root, "remaining.bin"), confidence: Confidence.Medium);
        var low = Item(Path.Combine(root, "low.bin"), confidence: Confidence.Low);
        var report = new CleanupReport("demo-backup", [new(removed, CleanupStatus.Removed, null)]);
        var entry = UninstallWorkflow.Record(Program(), RemovalMethod.Forced, null, [removed, remaining, low], report);
        Assert.Equal(3, entry.LeftoversFound);
        Assert.Equal(1, entry.LeftoversRemoved);
        Assert.Equal(42, entry.BytesFreed);
        Assert.Equal(remaining.Key, Assert.Single(entry.RemainingLeftovers).Key);
        Assert.Single(HistoryStore.All());
    }

    [Fact]
    public void JsonStoreRecoversFromCorruptedDemoData()
    {
        var file = DemoFile("bad.json", "{broken");
        Assert.Empty(JsonStore.Load<HistoryData>(file).Entries);
        JsonStore.Save(file, new HistoryData { Entries = [new() { Name = "Demo" }] });
        Assert.Equal("Demo", Assert.Single(JsonStore.Load<HistoryData>(file).Entries).Name);
        Assert.False(File.Exists(file + ".tmp"));
    }

    [Fact]
    public void SnapshotRoundTripsSyntheticFilesAndRegistryData()
    {
        var snapshot = new SystemSnapshot();
        snapshot.Files[@"D:\Demo\資料.bin"] = (123, 456);
        snapshot.Directories.Add(@"D:\Demo");
        snapshot.RegistryKeys.Add(@"64|HKCU\Software\Nebula");
        snapshot.RegistryValues["64|HKCU\\Software\\Nebula\0Setting"] = 987;
        var file = Path.Combine(root, "demo.snapshot");
        snapshot.Save(file);
        var loaded = SystemSnapshot.Load(file);
        Assert.Equal(snapshot.Taken, loaded.Taken);
        Assert.Equal((123L, 456L), loaded.Files[@"d:\demo\資料.bin"]);
        Assert.Equal(snapshot.Directories, loaded.Directories);
        Assert.Equal(987UL, loaded.RegistryValues["64|HKCU\\Software\\Nebula\0Setting"]);
    }

    [Fact]
    public void SnapshotDiffSeparatesAddedModifiedAndUnchangedData()
    {
        var before = new SystemSnapshot();
        before.Files[@"D:\Demo\existing.bin"] = (1, 1);
        before.Files[@"D:\Demo\unchanged.bin"] = (2, 2);
        before.RegistryKeys.Add(@"64|HKCU\Software\Nebula");
        before.RegistryValues["64|HKCU\\Software\\Nebula\0Setting"] = 1;
        var after = new SystemSnapshot();
        after.Files[@"D:\Demo\existing.bin"] = (3, 3);
        after.Files[@"D:\Demo\unchanged.bin"] = (2, 2);
        after.Directories.UnionWith([@"D:\Demo\Added", @"D:\Demo\Added\Nested"]);
        after.Files[@"D:\Demo\Added\Nested\new.bin"] = (4, 4);
        after.RegistryKeys.UnionWith(before.RegistryKeys);
        after.RegistryValues["64|HKCU\\Software\\Nebula\0Setting"] = 2;
        after.RegistryValues["64|HKCU\\Software\\Nebula\0NewSetting"] = 3;
        var diff = InstallMonitor.Diff(before, after, "Demo", null);
        Assert.Equal(@"D:\Demo\Added", Assert.Single(diff.AddedFolders));
        Assert.Empty(diff.AddedFiles);
        Assert.Equal(@"D:\Demo\existing.bin", Assert.Single(diff.ModifiedFiles));
        Assert.Single(diff.ModifiedValues);
        Assert.Single(diff.AddedValues);
    }

    [Fact]
    public void RegistrySerializationRoundTripsBinaryAndNumbers()
    {
        byte[] bytes = [0, 1, 127, 255];
        Assert.Equal(bytes, (byte[])BackupService.DeserializeValue(RegistryValueKind.Binary, BackupService.SerializeValue(bytes)));
        Assert.Equal(-1, BackupService.DeserializeValue(RegistryValueKind.DWord, BackupService.SerializeValue(-1)));
        Assert.Equal(long.MaxValue, BackupService.DeserializeValue(RegistryValueKind.QWord, BackupService.SerializeValue(long.MaxValue)));
    }

    [Fact]
    public void EmptyRegistryMultiStringRoundTrips()
    {
        Assert.Empty((string[])BackupService.DeserializeValue(RegistryValueKind.MultiString, BackupService.SerializeValue(Array.Empty<string>())));
    }

    [Fact]
    public void RegistryMultiStringsPreserveEmptyElementsAndLegacyBackups()
    {
        foreach (var values in new[] { new[] { "" }, new[] { "", "alpha", "" }, new[] { "[literal]", "日本語" } })
            Assert.Equal(values, (string[])BackupService.DeserializeValue(RegistryValueKind.MultiString, BackupService.SerializeValue(values)));
        Assert.Equal(new[] { "one", "two" },
            (string[])BackupService.DeserializeValue(RegistryValueKind.MultiString, "one\0two", null));
        Assert.Empty((string[])BackupService.DeserializeValue(RegistryValueKind.MultiString, "", null));
    }

    [Fact]
    public void ShredderRemovesOnlyDisposableDemoFiles()
    {
        var file = DemoFile(@"Shred\nested\demo.bin");
        var keep = DemoFile("keep.txt");
        var results = FileShredder.Shred([Path.Combine(root, "Shred")], 2);
        Assert.True(Assert.Single(results).Success);
        Assert.False(File.Exists(file));
        Assert.True(File.Exists(keep));
    }

    [Fact]
    public void LogStorePersistsSyntheticInstallLog()
    {
        var log = new InstallLog { Name = "Demo Install", AddedFolders = [Path.Combine(root, "Nebula")] };
        InstallLogStore.Save(log);
        Assert.Equal(log.Id, Assert.Single(InstallLogStore.All()).Id);
        InstallLogStore.Delete(log);
        Assert.Empty(InstallLogStore.All());
    }

    [Fact]
    public void UpdateParserReadsDemoTableAndIgnoresProgressAndSummary()
    {
        static string Row(string name, string id, string version, string available, string source) =>
            $"{name,-24}{id,-24}{version,-16}{available,-16}{source}";
        var output = "Downloading...\rComplete\n" + Row("Name", "Id", "Version", "Available", "Source") +
            "\n" + new string('-', 90) + "\n" + Row("Nebula Editor", "Demo.Nebula", "1.0", "2.0", "winget") +
            "\n1 upgrades available.\n";
        var update = Assert.Single(WinAppClear.Core.Updates.WingetClient.Parse(output));
        Assert.Equal("Demo.Nebula", update.Id);
        Assert.Equal("1.0", update.InstalledVersion);
        Assert.Equal("2.0", update.AvailableVersion);
        Assert.Empty(WinAppClear.Core.Updates.WingetClient.Parse("No installed package found matching input criteria."));
    }

    [Fact]
    public void AndroidParserReadsSyntheticPackageListWithoutDevice()
    {
        var apps = WinAppClear.Core.Android.AdbService.ParsePackages(
            "package:/data/app/~~abc==/demo/base.apk=com.demo.nebula\r\npackage:com.demo.simple\nnoise", false);
        Assert.Equal(2, apps.Count);
        Assert.Equal("com.demo.nebula", apps[0].Package);
        Assert.Equal("/data/app/~~abc==/demo/base.apk", apps[0].ApkPath);
        Assert.False(apps[0].IsSystem);
        Assert.Null(apps[1].ApkPath);
    }

    [Fact]
    public async Task MissingUninstallerReturnsControlledResultWithoutLaunchingAnything()
    {
        var result = await UninstallRunner.RunAsync(Program(), false);
        Assert.Equal(UninstallOutcome.NoUninstaller, result.Outcome);
        var missing = await UninstallRunner.RunAsync(Program(command: Path.Combine(root, "missing.exe")), false);
        Assert.Equal(UninstallOutcome.NoUninstaller, missing.Outcome);
    }

    [Fact]
    public void WpfPagesLoadAndProgramSearchWorksWithDemoRows()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                // Initialize resources only. Do not call Run/OnStartup: those load real inventory and prune backups.
                var app = new WinAppClear.App.App();
                app.InitializeComponent();
                var vm = new WinAppClear.App.ViewModels.ProgramsViewModel();
                foreach (var name in new[] { "Nebula Editor", "Aurora Player", "Orbit Notes" })
                    vm.Rows.Add(new(Program(folder: Path.Combine(root, name), name: name)));
                Assert.Equal(3, vm.View.Cast<object>().Count());
                Assert.StartsWith("3 programs ·", vm.Summary);
                vm.Search = "aurora";
                Assert.Equal("Aurora Player", Assert.Single(vm.View.Cast<WinAppClear.App.ViewModels.ProgramRow>()).Name);
                Assert.StartsWith("1 matching program ·", vm.Summary);
                Assert.Null(vm.EmptyText);
                vm.Search = "  AURORA  ";
                Assert.Single(vm.View.Cast<object>());
                vm.Search = "NO MATCH";
                Assert.Empty(vm.View.Cast<object>());
                Assert.StartsWith("0 matching programs ·", vm.Summary);
                Assert.Contains("NO MATCH", vm.EmptyText);
                vm.Search = "";
                Assert.Null(vm.EmptyText);
                Assert.StartsWith("3 programs ·", vm.Summary);

                var assembly = typeof(WinAppClear.App.Views.ProgramsView).Assembly;
                var pageTypes = assembly.GetTypes().Where(t => t.Namespace == "WinAppClear.App.Views" &&
                    t.IsSubclassOf(typeof(System.Windows.Controls.UserControl))).ToList();
                Assert.Equal(13, pageTypes.Count);
                foreach (var type in pageTypes)
                {
                    var view = (System.Windows.FrameworkElement)Activator.CreateInstance(type)!;
                    if (view is WinAppClear.App.Views.ProgramsView) view.DataContext = vm;
                    view.Measure(new System.Windows.Size(1100, 680));
                    view.Arrange(new System.Windows.Rect(0, 0, 1100, 680));
                    view.UpdateLayout();
                    Assert.True(view.ActualWidth > 0, type.Name);
                    if (view is WinAppClear.App.Views.ProgramsView)
                    {
                        var search = Assert.IsType<System.Windows.Controls.TextBox>(view.FindName("ProgramSearch"));
                        search.ApplyTemplate();
                        var host = Assert.IsType<System.Windows.Controls.ScrollViewer>(search.Template.FindName("PART_ContentHost", search));
                        Assert.Equal(new System.Windows.Thickness(0), host.Margin);
                        var grid = Assert.IsType<System.Windows.Controls.DataGrid>(view.FindName("Grid"));
                        Assert.Equal(3, grid.Items.Count);
                        // Standalone controls have no presentation source to resolve star column widths.
                        // Pin this column for the off-screen artifact only; production XAML is unchanged.
                        grid.Columns[1].Width = 400;
                        view.Measure(new System.Windows.Size(1100, 680));
                        view.Arrange(new System.Windows.Rect(0, 0, 1100, 680));
                        view.UpdateLayout();
                        var artifactRoot = Environment.GetEnvironmentVariable("WINAPPCLEAR_TEST_ARTIFACTS");
                        if (!string.IsNullOrEmpty(artifactRoot))
                        {
                            Directory.CreateDirectory(artifactRoot);
                            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(1100, 680, 96, 96,
                                System.Windows.Media.PixelFormats.Pbgra32);
                            bitmap.Render(view);
                            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                            using var file = File.Create(Path.Combine(artifactRoot, "demo-programs.png"));
                            encoder.Save(file);
                        }
                    }
                }
                app.Shutdown();
            }
            catch (Exception ex) { failure = ex; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(45)), "WPF demo test timed out");
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
