using System.Windows;
using System.Windows.Controls;
using DeathNote.Wpf.Infrastructure;
using DeathNote.Wpf.Services;
using System.Diagnostics;

namespace DeathNote.Wpf.Views;

public partial class SettingsView : UserControl
{
    private static string RemoveTrailingNotePeriod(string note) => note.TrimEnd().TrimEnd('.').TrimEnd();

    private string? _updateUrl;
    private bool _updateInstallBusy;
    public event Func<string,Task>? ImportRequested;
    public SettingsView()
    {
        InitializeComponent();
    }

    private string? _gameDirectory;
    private string? _configFile;
    private Civ5IntegrationService.Inspection? _integration;
    private bool _integrationBusy;

    private void SetIntegrationBusy(bool busy)
    {
        _integrationBusy=busy;
        CheckCivModulesButton.IsEnabled=!busy;
        RepairCivModulesButton.IsEnabled=!busy;
    }

    private async void CheckCivModules(object sender, RoutedEventArgs e)
    {
        if(_integrationBusy)return;
        SetIntegrationBusy(true);
        CivIntegrationSummary.Text="Checking installed game files and logging…";
        try
        {
            var result=await Task.Run(() => Civ5IntegrationService.Inspect(_gameDirectory,_configFile));
            _integration=result;
            if(!string.IsNullOrWhiteSpace(result.GameDirectory))_gameDirectory=result.GameDirectory;
            if(!string.IsNullOrWhiteSpace(result.ConfigFile))_configFile=result.ConfigFile;
            CivIntegrationSummary.Text=(result.Ready ? "File check passed. " : "Repair required. ")+
                "Game: "+(string.IsNullOrWhiteSpace(result.GameDirectory) ? "Not found" : result.GameDirectory)+
                Environment.NewLine+result.Summary;
        }
        catch(Exception ex)
        {
            _integration=null;
            CivIntegrationSummary.Text="Check failed: "+ex.Message;
        }
        finally
        {
            CivIntegrationReportScroll.ScrollToTop();
            SetIntegrationBusy(false);
        }
    }

    private void ChooseGameFolder(object sender,RoutedEventArgs e)
    {
        // .NET 8+ WPF folder picker: select the game installation DIRECTORY,
        // not a Lua file. This also works if StagingRoom.lua needs repair.
        string? detected = _gameDirectory ?? Civ5IntegrationService.DiscoverGameDirectory();
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Choose the Sid Meier's Civilization V installation folder",
            InitialDirectory = detected is not null && Directory.Exists(detected)
                ? detected
                : Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Multiselect = false
        };
        if(dialog.ShowDialog()!=true)return;
        try
        {
            Civ5IntegrationService.SaveGamePath(dialog.FolderName);
            _gameDirectory=Path.GetFullPath(dialog.FolderName);
            _integration=null;
            CivIntegrationSummary.Text="Game folder selected: "+_gameDirectory+Environment.NewLine+
                "Click Check modules to inspect the installation.";
            CivIntegrationReportScroll.ScrollToTop();
        }
        catch(Exception ex){MessageBox.Show(ex.Message,"Death Note — game folder",MessageBoxButton.OK,MessageBoxImage.Error);}
    }

    private void ChooseConfigFile(object sender,RoutedEventArgs e)
    {
        var dialog=new Microsoft.Win32.OpenFileDialog
        {
            Title="Choose Civilization V config.ini (My Games/Sid Meier's Civilization 5)",
            Filter="config.ini|config.ini|INI files (*.ini)|*.ini",
            CheckFileExists=true,
            FileName="config.ini"
        };
        if(dialog.ShowDialog()!=true)return;
        try
        {
            Civ5IntegrationService.SaveConfigPath(dialog.FileName);
            _configFile=dialog.FileName;
            _integration=null;
            CivIntegrationSummary.Text="Configuration path selected. Click Check modules.";
            CivIntegrationReportScroll.ScrollToTop();
        }
        catch(Exception ex){MessageBox.Show(ex.Message,"Death Note — config.ini",MessageBoxButton.OK,MessageBoxImage.Error);}
    }

    private async void RepairCivModules(object sender,RoutedEventArgs e)
    {
        if(_integrationBusy)return;
        SetIntegrationBusy(true);
        try
        {
            var result=await Task.Run(() => Civ5IntegrationService.Inspect(_gameDirectory,_configFile));
            _integration=result;
            _gameDirectory=result.GameDirectory;
            _configFile=result.ConfigFile;
            if(string.IsNullOrWhiteSpace(_gameDirectory) || result.ConfigFile is null)
            {
                CivIntegrationSummary.Text="Game folder or config.ini not found. Use Choose game folder / Choose config.ini, then check again.\n"+result.Summary;
                return;
            }
            if(Civ5IntegrationService.IsGameRunning())
            {
                MessageBox.Show("Close Civilization V completely before repairing its modules.",
                    "Death Note — game integration",MessageBoxButton.OK,MessageBoxImage.Warning);
                return;
            }
            if(result.Ready)
            {
                CivIntegrationSummary.Text="All installed modules are up to date.\n"+result.Summary;
                return;
            }
            string warning="Reinstall the complete Death Note integration for Civilization V?\n\n"+
                "Game: "+_gameDirectory+"\n\n"+
                "The bundled StagingRoom.lua replaces the installed lobby script; custom third-party lobby modifications may need to be installed again. " +
                "The repair also removes obsolete lobby badge XML/DDS files and updates all InGame.lua trackers and config.ini.\n\n"+
                "Every modified original file will be backed up first. The game must stay closed. " +
                "Your SQLite player/match database will not be changed.";
            if(MessageBox.Show(warning,"Death Note — confirm complete repair",
                MessageBoxButton.YesNo,MessageBoxImage.Warning)!=MessageBoxResult.Yes)return;
            CivIntegrationSummary.Text="Repairing game files and creating backups…";
            var repaired=await Task.Run(() => Civ5IntegrationService.Repair(_gameDirectory,result.ConfigFile));
            var after=await Task.Run(() => Civ5IntegrationService.Inspect(_gameDirectory,result.ConfigFile));
            _integration=after;
            CivIntegrationSummary.Text=$"Updated {repaired.FilesChanged} game/config files.\n"+
                $"Backups: {repaired.BackupDirectory}\n"+after.Summary;
            MessageBox.Show(after.Ready ? "Repair completed successfully. Restart Civilization V and enter a lobby to confirm live signals." :
                "Repair finished but some checks still need attention. See the Settings report.",
                "Death Note — game integration",MessageBoxButton.OK,
                after.Ready ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch(Exception ex)
        {
            CivIntegrationSummary.Text="Repair failed: "+ex.Message;
            MessageBox.Show(ex.Message,"Death Note — repair failed",MessageBoxButton.OK,MessageBoxImage.Error);
        }
        finally
        {
            CivIntegrationReportScroll.ScrollToTop();
            SetIntegrationBusy(false);
        }
    }

    private void OpenWorkspaceFolder(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo { FileName = App.Workspace, UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Death Note", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private async void SaveNickname(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm) await vm.SetLocalNicknameAsync();
    }

    private async void AutoKickChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        AutoKickToggle.IsEnabled = false;
        try { await vm.SetAutoKickEnabledAsync(AutoKickToggle.IsChecked == true); }
        finally { AutoKickToggle.IsEnabled = true; }
    }

    private async void Backup(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm) await vm.CreateBackupAsync();
    }

    private async void Integrity(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm) await vm.CheckIntegrityAsync();
    }

    private async void ExportDatabase(object sender,RoutedEventArgs e)
    {
        if(DataContext is not MainViewModel vm)return;
        var dialog=new Microsoft.Win32.SaveFileDialog
        {
            Title="Export Death Note database",
            Filter="SQLite database (*.db)|*.db",
            DefaultExt=".db",
            FileName="DeathNote-export-"+DateTime.Now.ToString("yyyyMMdd")+".db",
            OverwritePrompt=true
        };
        if(dialog.ShowDialog()!=true)return;
        try
        {
            await vm.ExportDatabaseAsync(dialog.FileName);
            MessageBox.Show("Database exported successfully.\n"+dialog.FileName,
                "Death Note",MessageBoxButton.OK,MessageBoxImage.Information);
        }
        catch(Exception ex)
        {
            MessageBox.Show(ex.Message,"Death Note — export failed",MessageBoxButton.OK,MessageBoxImage.Error);
        }
    }

    private async void ImportDatabase(object sender,RoutedEventArgs e)
    {
        var dialog=new Microsoft.Win32.OpenFileDialog
        {
            Title="Import a complete Death Note database",
            Filter="SQLite database (*.db)|*.db",
            CheckFileExists=true,Multiselect=false
        };
        if(dialog.ShowDialog()!=true)return;
        if(MessageBox.Show("Replace the current database with the selected file?\n"+
            "A full recovery backup will be created first. The application will close after import.",
            "Death Note — confirm import",MessageBoxButton.YesNo,MessageBoxImage.Warning)!=MessageBoxResult.Yes)return;
        if(ImportRequested is not null)await ImportRequested.Invoke(dialog.FileName);
    }

    private async void CheckUpdates(object sender, RoutedEventArgs e)
    {
        CheckUpdateButton.IsEnabled = false;
        DownloadUpdateButton.IsEnabled = false;
        _updateUrl = null;
        UpdateStatus.Text = "Checking the official release…";
        try
        {
            if (DataContext is not MainViewModel vm) return;
            await vm.CheckForUpdatesAsync();
            _updateUrl = vm.UpdateDownloadUrl;
            UpdateStatus.Text = RemoveTrailingNotePeriod(vm.UpdateNotice);
            DownloadUpdateButton.IsEnabled = !_updateInstallBusy && _updateUrl is not null;
        }
        catch (Exception)
        {
            UpdateStatus.Text = "Update check unavailable — verify the connection or open the release page";
        }
        finally { CheckUpdateButton.IsEnabled = true; }
    }

    private async void DownloadUpdate(object sender, RoutedEventArgs e)
    {
        if (_updateUrl is null || _updateInstallBusy) return;
        if (MessageBox.Show(
            "Download the latest Death Note installer and start the update now?\n\n"+
            "Your player and match data stay in LocalAppData and are not deleted by the installer.",
            "Death Note update", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        _updateInstallBusy = true;
        DownloadUpdateButton.IsEnabled = false;
        CheckUpdateButton.IsEnabled = false;
        try
        {
            var progress = new Progress<int>(percent =>
            {
                UpdateStatus.Text = percent > 0 && percent < 100
                    ? $"Downloading installer… {percent}%"
                    : "Downloading installer…";
            });
            var installer = await InstallerUpdateService.DownloadAsync(_updateUrl, progress);
            UpdateStatus.Text = $"Installer {installer.VersionLabel} downloaded. Launching update…";
            InstallerUpdateService.LaunchInstaller(installer.FilePath);
            Application.Current?.Shutdown();
        }
        catch (Exception ex)
        {
            UpdateStatus.Text = "The installer could not be downloaded. Use Release page instead.";
            MessageBox.Show(ex.Message, "Death Note update", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _updateInstallBusy = false;
            CheckUpdateButton.IsEnabled = true;
            if (_updateUrl is not null)
                DownloadUpdateButton.IsEnabled = true;
        }
    }

    private void OpenReleases(object sender, RoutedEventArgs e)
    {
        try { UpdateCheckService.OpenReleases(); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Death Note update", MessageBoxButton.OK, MessageBoxImage.Error); }
    }
}
