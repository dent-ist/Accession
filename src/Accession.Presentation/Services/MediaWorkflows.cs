using System.IO;
using Accession.Core.Model;
using Accession.Core.Settings;
using Accession.Core.Threading;
using Accession.Data.MediaManagement;
using Accession.Presentation.Platform;
using Accession.Presentation.ViewModels;
using Accession.UI.Components;
using Microsoft.Extensions.Logging;

namespace Accession.Presentation.Services;

public enum DiscoveryMode
{
    /// <summary>After opening or creating: prompt for new folders, update the notice.</summary>
    OnOpen,

    /// <summary>Inventory → Discover Media: always report the outcome.</summary>
    Manual,

    /// <summary>Refresh statuses and the notice without prompting.</summary>
    Silent,
}

/// <summary>User-level media actions: discover, add, delete, open in Explorer.</summary>
public sealed class MediaWorkflows
{
    private readonly IDesktop _desktop;
    private readonly ToastService? _toasts;
    private readonly InventoryHost _host;
    private readonly IDialogService _dialogs;
    private readonly BusyTracker _busy;
    private readonly MediaDiscoveryService _discovery;
    private readonly MediaService _media;
    private readonly ISettingsService _settings;
    private readonly ILogger<MediaWorkflows> _logger;
    private readonly ScanHost _scans;

    public MediaWorkflows(
        InventoryHost host,
        IDialogService dialogs,
        BusyTracker busy,
        MediaDiscoveryService discovery,
        MediaService media,
        ISettingsService settings,
        ScanHost scans,
        ILogger<MediaWorkflows> logger,
        IDesktop desktop,
        ToastService? toasts = null)
    {
        _toasts = toasts;
        _desktop = desktop;
        _scans = scans;
        _host = host;
        _dialogs = dialogs;
        _busy = busy;
        _discovery = discovery;
        _media = media;
        _settings = settings;
        _logger = logger;
    }

    /// <summary>Discovery lists the root; with media on other drives it also checks that they are connected.</summary>
    public bool CanDiscover => _host.HasSession && (_host.IsRootAvailable || AllowsExternal);

    /// <summary>The inventory also takes media from other folders and drives (USB drives, other shares).</summary>
    public bool AllowsExternal => _host.Config?.AllowExternalMedia == true;

    /// <summary>Media can be added: from the root while it is reachable, or from anywhere when allowed.</summary>
    public bool CanAddMedia => _host.CanModify && (_host.IsRootAvailable || AllowsExternal);

    /// <summary>Compares the root's subfolders with registered media and reacts according to <paramref name="mode"/>.</summary>
    public async Task RunDiscoveryAsync(DiscoveryMode mode)
    {
        if (_host.Session is not { } session || (!session.IsRootAvailable && !AllowsExternal))
        {
            if (mode == DiscoveryMode.Manual)
            {
                _dialogs.ShowWarning("Discover media", "The root folder is not available, so media folders cannot be checked.");
            }

            return;
        }

        DiscoveryResult result;
        try
        {
            result = await _busy.RunAsync(_ => Task.FromResult(_discovery.Discover(session)), "Checking media folders…");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            _logger.LogError(ex, "Media discovery failed");
            if (mode == DiscoveryMode.Manual)
            {
                _dialogs.ShowError("Discover media", $"Media folders could not be checked.\n\n{ex.Message}", ex);
            }

            return;
        }

        _host.NotifyMediaChanged();
        if (!result.RootAvailable && AllowsExternal)
        {
            // The media on other drives were still checked.
            UpdateNotice(result, rootReachable: false);
            if (mode == DiscoveryMode.Manual)
            {
                _dialogs.ShowWarning("Discover media", "The root folder cannot be reached. The media on other drives were checked.");
            }

            return;
        }

        if (!result.RootAvailable)
        {
            _host.SetNotice("The root folder cannot be reached.");
            if (mode == DiscoveryMode.Manual)
            {
                _dialogs.ShowWarning("Discover media", "The root folder cannot be reached.");
            }

            return;
        }

        UpdateNotice(result);
        if (mode == DiscoveryMode.Silent)
        {
            return;
        }

        if (result.NewFolders.Count > 0 && _host.CanModify)
        {
            ShowAddDialog("New Media Found", result.NewFolders);
        }
        else if (result.NewFolders.Count > 0 && mode == DiscoveryMode.Manual)
        {
            _dialogs.ShowInfo("Discover media",
                $"{result.NewFolders.Count:N0} folder(s) under the root are not in this inventory. Open the inventory with write access to add them.");
        }
        else if (mode == DiscoveryMode.Manual)
        {
            _dialogs.ShowInfo("Discover media", "No new media folders were found under the root folder.");
        }
    }

    /// <summary>Media → Add Media: shows unregistered folders under the root plus a folder browser.</summary>
    public async Task AddMediaAsync()
    {
        if (_host.Session is not { } session || !CanAddMedia)
        {
            return;
        }

        if (!session.IsRootAvailable)
        {
            ShowExternalDialog(null); // the root is offline: media from other drives can still be added
            return;
        }

        IReadOnlyList<DiscoveredFolder> folders = [];
        try
        {
            var result = await _busy.RunAsync(_ => Task.FromResult(_discovery.Discover(session)), "Checking media folders…");
            folders = result.NewFolders;
            UpdateNotice(result);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            _logger.LogWarning(ex, "Listing media folders failed");
        }

        ShowAddDialog("Add Media", folders);
    }

    public async Task DeleteMediaAsync(Media media)
    {
        ArgumentNullException.ThrowIfNull(media);
        if (_host.Session is not { } session || !_host.CanModify)
        {
            return;
        }

        if (MediaService.BusyStatuses.Contains(media.Status))
        {
            _dialogs.ShowWarning("Delete media", $"'{media.MediaId}' is being scanned. Cancel the scan before deleting it.");
            return;
        }

        var dialog = new DeleteMediaViewModel(media, _settings.Current.SizeUnit);
        if (_dialogs.ShowDialog(dialog) != true)
        {
            return;
        }

        try
        {
            await _busy.RunAsync(_ => Task.FromResult(_media.Delete(session, media.MediaKey)), $"Deleting {media.MediaId}…");
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            _logger.LogError(ex, "Deleting media {MediaId} failed", media.MediaId);
            _dialogs.ShowError("Delete media", $"'{media.MediaId}' could not be deleted.\n\n{ex.Message}", ex);
        }

        await RunDiscoveryAsync(DiscoveryMode.Silent);
    }

    /// <summary>Notice bar "Scan now": full scan for never-scanned media, resume for incomplete ones (DSC-04).</summary>
    public void ScanPendingMedia()
    {
        if (_host.Session is not { } session)
        {
            return;
        }

        IReadOnlyList<Media> media;
        using (var scope = session.Database.Open())
        {
            media = new Accession.Data.Repositories.MediaRepository(scope).ListActive();
        }

        var requests = media
            .Where(m => m.Status is MediaStatus.New or MediaStatus.Incomplete)
            .Select(m => (m, m.Status == MediaStatus.New ? ScanType.Full : ScanType.Resume))
            .ToList();
        if (requests.Count > 0)
        {
            _ = _scans.EnqueueAsync(requests);
            _host.SetNotice(_host.Session?.OpenNotice ?? string.Empty);
        }
    }

    /// <summary>
    /// Media screen scan actions. <paramref name="type"/> null = "Scan": New → full scan, Incomplete → resume,
    /// already scanned → rescan after confirmation.
    /// </summary>
    public void Scan(IReadOnlyCollection<Media> media, ScanType? type)
    {
        if (media.Count == 0)
        {
            return;
        }

        var requests = media.Select(m => (m, type ?? (m.Status switch
        {
            MediaStatus.Incomplete => ScanType.Resume,
            _ => ScanType.Full,
        }))).ToList();

        var rescans = requests.Where(r => r.Item2 == ScanType.Full && r.m.Status is MediaStatus.Completed or MediaStatus.CompletedWithErrors).ToList();
        if (rescans.Count > 0 && !_dialogs.Confirm("Rescan",
                $"Rescanning replaces the existing results of {rescans.Count:N0} media " +
                $"({string.Join(", ", rescans.Take(5).Select(r => r.m.MediaId))}{(rescans.Count > 5 ? ", …" : string.Empty)}). Continue?"))
        {
            return;
        }

        _ = _scans.EnqueueAsync(requests);
    }

    public void OpenInExplorer(Media media)
    {
        ArgumentNullException.ThrowIfNull(media);
        if (_host.Locations()?.Folder(media.MediaId) is not { } location)
        {
            return;
        }

        if (location.Current is not { } path)
        {
            _dialogs.ShowWarning("Open in Explorer", location.IsExternal
                ? $"{media.MediaId} is not connected: '{location.Expected}' (or its drive) cannot be found."
                : $"The folder '{location.Expected}' cannot be found.");
            return;
        }

        _desktop.OpenFolder(path);
    }

    private void ShowAddDialog(string title, IReadOnlyList<DiscoveredFolder> folders)
    {
        if (_host.Session is not { } session)
        {
            return;
        }

        var dialog = new AddMediaViewModel(title, session, folders, _media, _dialogs, _scans.CanScan);
        var added = _dialogs.ShowDialog(dialog) == true;
        if (dialog.ExternalFolder is { } external)
        {
            ShowExternalDialog(external); // a folder outside the root was chosen
            return;
        }

        if (added)
        {
            _host.NotifyMediaChanged();
            if (dialog.StartScanning && dialog.Added.Count > 0)
            {
                _ = _scans.EnqueueAsync([.. dialog.Added.Select(m => (m, ScanType.Full))]);
            }

            _ = RunDiscoveryAsync(DiscoveryMode.Silent);
        }
    }

    /// <summary>Adds a media from another folder or drive (MED-09), optionally queueing its scan.</summary>
    private void ShowExternalDialog(string? folder)
    {
        if (_host.Session is not { } session)
        {
            return;
        }

        var dialog = new ExternalMediaViewModel(session, _media, _dialogs, _scans.CanScan, folder);
        if (_dialogs.ShowDialog(dialog) == true && dialog.Result is { } media)
        {
            _host.NotifyMediaChanged();
            if (dialog.StartScanning)
            {
                _ = _scans.EnqueueAsync([(media, ScanType.Full)]);
            }
        }
    }

    /// <summary>Media screen → Change location: a media outside the root is now at another folder or drive letter (MED-11).</summary>
    public void ChangeLocation(Media media)
    {
        ArgumentNullException.ThrowIfNull(media);
        if (_host.Session is not { } session || !_host.CanModify || !media.IsExternal)
        {
            return;
        }

        var dialog = new ExternalMediaViewModel(session, _media, _dialogs, canScan: false, moving: media);
        if (_dialogs.ShowDialog(dialog) == true && dialog.Result is { } moved)
        {
            _host.NotifyMediaChanged();
            _toasts?.Show($"{moved.MediaId} is now read from {moved.SourcePath}", ToastKind.Success);
        }
    }

    private void UpdateNotice(DiscoveryResult result, bool rootReachable = true)
    {
        var parts = new List<string>();
        if (_host.Session?.OpenNotice is { Length: > 0 } openNotice)
        {
            parts.Add(openNotice); // e.g. "the last session was not closed properly" stays visible
        }

        if (result.NeverScanned.Count > 0)
        {
            parts.Add($"{result.NeverScanned.Count:N0} media not scanned yet");
        }

        if (result.Incomplete.Count > 0)
        {
            parts.Add($"{result.Incomplete.Count:N0} media with an incomplete scan");
        }

        if (!rootReachable)
        {
            parts.Add("The root folder cannot be reached");
        }

        var notConnected = result.MissingMedia.Count(m => m.IsExternal);
        if (result.MissingMedia.Count - notConnected > 0 && rootReachable)
        {
            parts.Add($"{result.MissingMedia.Count - notConnected:N0} media folder(s) missing from the root");
        }

        if (notConnected > 0)
        {
            parts.Add($"{notConnected:N0} media on other drives not connected");
        }

        _host.SetNotice(string.Join(" · ", parts));
    }
}
