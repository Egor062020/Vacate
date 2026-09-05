using System.IO;
using System.Windows;
using System.Windows.Controls;
using Vacate.App.Localization;
using Vacate.Core.Journal;
using Vacate.Core.Safety;
using Vacate.Platform.Windows.Files;
using Vacate.Platform.Windows.Registry;

namespace Vacate.App.Views;

public partial class DashboardPage : UserControl
{
    public DashboardPage()
    {
        InitializeComponent();
        Loaded += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        var warnings = new List<WarningItem>();

        await Task.Run(() =>
        {
            var volumes = new VolumeInfoProvider();
            var free = volumes.GetFreeSpaceByVolume();
            var systemRoot = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) ?? @"C:\";

            long freeBytes = free.GetValueOrDefault(systemRoot);

            var startupCount = new StartupScanner().Scan()
                .Count(e => e.Source != Abstractions.Model.StartupSource.Service);

            Dispatcher.Invoke(() =>
            {
                FreeSpaceValue.Text = Format.Size(freeBytes);
                StartupValue.Text = startupCount.ToString();
            });

            // Заполненность диска — самая частая причина, по которой сюда заходят.
            try
            {
                var drive = new DriveInfo(systemRoot);

                if (drive.IsReady && drive.TotalSize > 0)
                {
                    var usedPercent = 100.0 * (drive.TotalSize - drive.AvailableFreeSpace) / drive.TotalSize;

                    if (usedPercent >= 90)
                    {
                        warnings.Add(new WarningItem(
                            Format.Text("Dashboard.DiskFull", $"{usedPercent:0}"),
                            Strings.Get("Dashboard.DiskFullNote")));
                    }
                }
            }
            catch (IOException)
            {
                // Сведения о диске могут быть недоступны — это не повод падать.
            }

            foreach (var disk in new DiskHealthReader().Read().Where(d => d.NeedsAttention))
            {
                warnings.Add(new WarningItem(
                    Format.Text("Dashboard.DiskAttention", disk.Model),
                    Strings.Get("Dashboard.DiskAttentionNote")));
            }

            if (startupCount >= 12)
            {
                warnings.Add(new WarningItem(
                    Format.Text("Dashboard.ManyStartup", startupCount),
                    Strings.Get("Dashboard.ManyStartupNote")));
            }
        });

        WarningsList.ItemsSource = warnings;

        await LoadLastSessionAsync();
    }

    private async Task LoadLastSessionAsync()
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Vacate", "journal");

            var journal = new JsonlOperationJournal(directory);
            var sessions = await journal.GetRecentSessionsAsync(1, CancellationToken.None);

            if (sessions.Count == 0)
            {
                return;
            }

            var session = sessions[0];

            // Две цифры рядом — суть честного счётчика: сколько удалено
            // и сколько места это на самом деле дало.
            LastSessionText.Text = Format.Text(
                "Dashboard.LastSession",
                session.StartedAtUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm"),
                session.ItemCount,
                Format.Size(session.ClaimedBytes),
                Format.Size(session.ActuallyFreedBytes));
        }
        catch (IOException)
        {
            // Журнал может быть недоступен.
        }
    }

    private async void OnScan(object sender, RoutedEventArgs e)
    {
        ScanButton.IsEnabled = false;
        ScanHint.Text = Strings.Get("Dashboard.Searching");

        try
        {
            var (count, size) = await Task.Run(() =>
            {
                var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                var systemDrive = Path.GetPathRoot(windows) ?? @"C:\";

                var own = new List<string> { AppContext.BaseDirectory };
                own.AddRange(FileSystemQuarantine.EnumerateStores());

                var policy = PathPolicy.CreateDefault(windows, systemDrive, own);
                var plan = new TempFilesScanner(policy).Scan(TempLocation.Standard(), CancellationToken.None);

                return (plan.TotalCount, plan.TotalSizeOnDiskBytes);
            });

            JunkValue.Text = Format.Size(size);
            ScanHint.Text = count == 0
                ? Strings.Get("Clean.Empty")
                : Format.Text("Dashboard.FoundJunk", count);
        }
        finally
        {
            ScanButton.IsEnabled = true;
        }
    }

    private sealed record WarningItem(string Title, string Detail);
}
