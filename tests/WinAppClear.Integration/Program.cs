using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32;
using WinAppClear.Core.Cleanup;
using WinAppClear.Core.Inventory;
using WinAppClear.Core.Leftovers;
using WinAppClear.Core.Models;
using WinAppClear.Core.Monitor;
using WinAppClear.Core.Uninstall;
using WinAppClear.Core.Util;

internal static class Program
{
    private const string Name="WinAppClearDemoFixture";
    private static string Root="", RunId="", Key="", SettingsKey="";
    private static readonly List<object> Results=[];
    private static string Dotnet => Path.GetFullPath(Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(),"../../../dotnet.exe"));
    private static string Assembly => AssemblyLocation();
    private static string AssemblyLocation()=>typeof(Program).Assembly.Location;
    private static string InstallDir=>Path.Combine(Root,"Installed",Name);
    private static string DataDir=>Path.Combine(Root,"Roaming",Name);
    private static string Sentinel=>Path.Combine(Root,"Unrelated","keep.txt");
    private static string UninstallArgs=>$"\"{Assembly}\" --uninstall \"{Root}\"";
    private static void Check(bool pass,string label)
    {
        Results.Add(new {Test=label,Passed=pass}); Console.WriteLine($"{(pass?"PASS":"FAIL")}: {label}");
        if(!pass)throw new InvalidOperationException(label);
    }
    private static void Configure(string root)
    {
        Root=Path.GetFullPath(root);RunId=Path.GetFileName(Root);
        var allowed=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"WinAppClear-Integration")+Path.DirectorySeparatorChar;
        if(!Root.StartsWith(allowed,StringComparison.OrdinalIgnoreCase)||!Guid.TryParseExact(RunId,"N",out _))throw new InvalidOperationException("Outside fixture boundary");
        Key=ProgramInventory.UninstallSubKey+"\\WinAppClearDemoFixture-"+RunId;
        SettingsKey=@"Software\WinAppClearIntegrationTests\"+RunId+"\\"+Name;
    }
    private static void Install()
    {
        Directory.CreateDirectory(InstallDir);Directory.CreateDirectory(DataDir);
        File.WriteAllText(Path.Combine(InstallDir,"demo-app.txt"),"Harmless demo application payload");
        File.WriteAllText(Path.Combine(DataDir,"settings.json"),"{\"demo\":true,\"theme\":\"green\"}");
        using(var key=Registry.CurrentUser.CreateSubKey(SettingsKey)){key.SetValue("Theme","green");key.SetValue("Count",7,RegistryValueKind.DWord);}
        using(var key=Registry.CurrentUser.CreateSubKey(Key))
        {
            key.SetValue("DisplayName",Name);key.SetValue("Publisher","WinAppClear Test Fixtures");key.SetValue("DisplayVersion","1.0.0");
            key.SetValue("InstallLocation",InstallDir);key.SetValue("UninstallString",$"\"{Dotnet}\" {UninstallArgs}");
            key.SetValue("QuietUninstallString",$"\"{Dotnet}\" {UninstallArgs}");
        }
    }
    private static void Uninstall()
    {
        File.Delete(Path.Combine(InstallDir,"demo-app.txt"));
        Registry.CurrentUser.DeleteSubKeyTree(Key,false);
        // Deliberately leave settings behind, like a typical application's uninstaller.
    }
    private static async Task<int> Main(string[] args)
    {
        if(args.Length==2 && args[0] is "--install" or "--uninstall")
        {
            Configure(args[1]);if(args[0]=="--install")Install();else Uninstall();return 0;
        }
        var evidence=Path.GetFullPath(args.Length>0?args[0]:"artifacts/integration-evidence");
        if(Directory.Exists(evidence)&&Directory.EnumerateFileSystemEntries(evidence).Any())throw new InvalidOperationException("Choose a fresh evidence directory.");
        Directory.CreateDirectory(evidence);
        Configure(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"WinAppClear-Integration",Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(Root);AppPaths.OverrideRoot(Path.Combine(evidence,"app-data"));
        bool passed=false;string? error=null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Sentinel)!);File.WriteAllText(Sentinel,"DO NOT REMOVE");
            var regParent=@"Software\WinAppClearIntegrationTests\"+RunId;
            using(var parent=Registry.CurrentUser.CreateSubKey(regParent)){}
            var roots=new[]{Root};var regRoots=new[]{(RegistryHive.CurrentUser,RegistryView.Registry64,regParent),(RegistryHive.CurrentUser,RegistryView.Registry64,Key)};
            var before=SystemSnapshot.Take(fileRoots:roots,registryRoots:regRoots);
            before.Save(Path.Combine(evidence,"before.snapshot"));
            var installer=new ProcessStartInfo(Dotnet){UseShellExecute=false};installer.ArgumentList.Add(Assembly);installer.ArgumentList.Add("--install");installer.ArgumentList.Add(Root);
            using(var process=Process.Start(installer)!){await process.WaitForExitAsync();Check(process.ExitCode==0,"Real demo installer process succeeds");}
            var program=ProgramInventory.GetInstalledPrograms().Single(p=>p.KeyName=="WinAppClearDemoFixture-"+RunId);
            Check(File.Exists(Path.Combine(InstallDir,"demo-app.txt"))&&ProgramInventory.StillInstalled(program),"Inventory detects the real per-user demo installation");
            var after=SystemSnapshot.Take(fileRoots:roots,registryRoots:regRoots);after.Save(Path.Combine(evidence,"after.snapshot"));
            var log=InstallMonitor.Diff(SystemSnapshot.Load(Path.Combine(evidence,"before.snapshot")),after,Name,null);InstallLogStore.Save(log);
            File.WriteAllText(Path.Combine(evidence,"installation-log.json"),JsonSerializer.Serialize(log,new JsonSerializerOptions{WriteIndented=true}));
            Check(after.Directories.Contains(InstallDir)&&log.AddedFolders.Any(p=>PathUtil.IsUnderOrEqual(InstallDir,p))&&log.AddedKeys.Any(p=>p.Contains(Name)),"Monitor records real added folders and registry keys");
            Check(InstallLogStore.All().Any(l=>l.Id==log.Id),"Recorded installation log persists and reloads");
            var uninstall=await UninstallRunner.RunAsync(program,true,ct:new CancellationTokenSource(TimeSpan.FromSeconds(40)).Token);
            Check(uninstall.Outcome==UninstallOutcome.Removed&&uninstall.ExitCode==0,"WinAppClear invokes actual registered uninstaller successfully");
            Check(!ProgramInventory.StillInstalled(program)&&!File.Exists(Path.Combine(InstallDir,"demo-app.txt")),"Uninstall removes registration and primary app payload");
            Check(Directory.Exists(DataDir),"Fixture deliberately leaves settings for cleanup review");
            var scanner=new LeftoverScanner(new LeftoverScanOptions{FolderRoots=[Path.Combine(Root,"Installed"),Path.Combine(Root,"Roaming")],RegistryRoots=[(RegistryHive.CurrentUser,RegistryView.Registry64,regParent)],IncludeSystemItems=false},[]);
            var items=scanner.Scan(ScanTarget.FromProgram(program));
            Check(items.Any(i=>i.Path.Equals(DataDir,StringComparison.OrdinalIgnoreCase)),"Leftover scanner finds real demo roaming-data folder");
            Check(items.Any(i=>i.Kind==LeftoverKind.RegistryKey&&i.Path.EndsWith(SettingsKey,StringComparison.OrdinalIgnoreCase)),"Leftover scanner finds real demo settings registry key");
            Check(!items.Any(i=>i.Path.Contains("Unrelated",StringComparison.OrdinalIgnoreCase)),"Unrelated sentinel is excluded from cleanup suggestions");
            foreach(var item in items)
            {
                var allowed=item.Kind==LeftoverKind.Folder?item.Path.StartsWith(Root+"\\",StringComparison.OrdinalIgnoreCase):item.Kind==LeftoverKind.RegistryKey&&item.Path.StartsWith("HKCU\\"+regParent+"\\",StringComparison.OrdinalIgnoreCase);
                Check(allowed,"Cleanup target restricted to owned fixture: "+item.Kind);
            }
            var settings=Path.Combine(DataDir,"settings.json");var hash=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(settings)));
            var report=CleanupExecutor.Execute("Integration fixture leftovers",items);
            Check(report.Removed==items.Count&&report.Failed==0&&report.Scheduled==0,"Selected demo leftovers are backed up and removed without reboot");
            using(var key=Registry.CurrentUser.OpenSubKey(SettingsKey))Check(key is null&&!Directory.Exists(DataDir),"Original leftover paths and registry key are absent after cleanup");
            var backup=BackupService.List().Single(b=>b.Id==report.BackupId);
            Check(backup.Entries.Count==items.Count&&BackupService.SizeOnDisk(backup)>0,"Backup manifest and actual backup payload exist");
            var restored=BackupService.Restore(backup);
            Check(restored.Failed==0&&restored.Restored==items.Count,"Restore succeeds for all backed-up fixture items");
            Check(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(settings)))==hash,"Restored settings file matches original SHA-256");
            using(var key=Registry.CurrentUser.OpenSubKey(SettingsKey))Check((string?)key?.GetValue("Theme")=="green"&&(int?)key?.GetValue("Count")==7,"Registry restore preserves string and DWORD values");
            Check(!ProgramInventory.StillInstalled(program),"Restoring leftovers does not incorrectly reinstall the app registration");
            Check(File.ReadAllText(Sentinel)=="DO NOT REMOVE","Unrelated file remains unchanged through uninstall, cleanup and restore");
            passed=true;
        }
        catch(Exception ex){error=ex.ToString();Console.Error.WriteLine(error);}
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(Key,false);Registry.CurrentUser.DeleteSubKeyTree(@"Software\WinAppClearIntegrationTests\"+RunId,false);
            if(Directory.Exists(Root))Directory.Delete(Root,true);
            AppPaths.OverrideRoot(null);
            File.WriteAllText(Path.Combine(evidence,"results.json"),JsonSerializer.Serialize(new{Passed=passed,Tests=Results,Error=error,FixtureRoot=Root,Scope="Real per-user EXE fixture; restricted filesystem/registry snapshot roots; no MSI or machine-wide integrations",CompletedUtc=DateTime.UtcNow},new JsonSerializerOptions{WriteIndented=true}));
        }
        return passed?0:1;
    }
}
