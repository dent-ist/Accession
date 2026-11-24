using System.IO;
using Accession.Core.Model;
using Accession.Data.MediaManagement;
using Accession.Data.Sessions;
using Accession.Presentation.Mvvm;
using Accession.Presentation.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Accession.Presentation.ViewModels;

/// <summary>
/// Adds a media from another folder or drive (MED-09 … MED-11): the folder, its Media ID (a drive's label by default,
/// editable) and what the app recognises the drive by. With <c>moving</c>, points that media at its new place instead.
/// </summary>
public sealed partial class ExternalMediaViewModel : DialogViewModelBase
{
    private readonly InventorySession _session;
    private readonly MediaService _media;
    private readonly IDialogService _dialogs;
    private bool _idEdited;
    private bool _settingId;

    public ExternalMediaViewModel(InventorySession session, MediaService media, IDialogService dialogs, bool canScan, string? folder = null,
        Media? moving = null)
    {
        _session = session;
        _media = media;
        _dialogs = dialogs;
        Moving = moving;
        CanScan = canScan && moving is null;
        StartScanning = CanScan;
        Title = moving is null ? "Add media from another folder or drive" : $"Change location of {moving.MediaId}";
        if (moving is not null)
        {
            _settingId = true;
            MediaId = moving.MediaId;
            _settingId = false;
            _idEdited = true;
        }

        Folder = folder ?? string.Empty;
    }

    /// <summary>The media being moved; null when adding one.</summary>
    public Media? Moving { get; }

    public bool IsMoving => Moving is not null;

    public string CurrentLocation => Moving is { } m ? $"{m.SourcePath}{(m.VolumeLabel is { Length: > 0 } label ? $"  (drive {label})" : string.Empty)}" : string.Empty;

    public bool CanScan { get; }

    [ObservableProperty]
    public partial string Folder { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string MediaId { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool StartScanning { get; set; }

    /// <summary>What the folder is: a whole drive with its label and serial, a folder on a drive, or a network folder.</summary>
    [ObservableProperty]
    public partial string FolderInfo { get; set; } = string.Empty;

    /// <summary>Moving: how much of the media's top level is in the chosen folder.</summary>
    [ObservableProperty]
    public partial string MatchInfo { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool MatchIsLow { get; set; }

    [ObservableProperty]
    public partial string FolderError { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string MediaIdError { get; set; } = string.Empty;

    /// <summary>The media added or moved when the dialog closed with OK.</summary>
    public Media? Result { get; private set; }

    public string GoText => IsMoving ? "Change location" : "Add media";

    partial void OnFolderChanged(string value)
    {
        FolderError = string.Empty;
        FolderInfo = string.Empty;
        MatchInfo = string.Empty;
        MatchIsLow = false;
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        ExternalFolder described;
        try
        {
            described = MediaService.Describe(_session, value.Trim());
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException)
        {
            FolderError = "The path is not valid.";
            return;
        }

        if (!Directory.Exists(described.Path))
        {
            FolderError = "The folder does not exist or cannot be reached.";
            return;
        }

        FolderInfo = Describe(described);
        if (!_idEdited)
        {
            _settingId = true;
            MediaId = described.SuggestedMediaId;
            _settingId = false;
        }

        if (Moving is { } moving)
        {
            var (found, expected) = MediaService.CompareTopLevel(_session, moving.MediaKey, described.Path);
            if (expected > 0)
            {
                MatchIsLow = found * 2 < expected;
                MatchInfo = $"{found:N0} of the {expected:N0} folders and files at the top of {moving.MediaId} are in this folder." +
                            (MatchIsLow ? " It is probably not the same media." : string.Empty);
            }
        }
    }

    partial void OnMediaIdChanged(string value)
    {
        if (!_settingId)
        {
            _idEdited = true;
        }

        MediaIdError = IsMoving ? string.Empty : MediaService.ValidateMediaId(value) ?? string.Empty;
    }

    [RelayCommand]
    private void Browse()
    {
        if (_dialogs.PickFolder(IsMoving ? $"Select the folder or drive of {Moving!.MediaId}" : "Select a folder or drive to add as media",
                Directory.Exists(Folder) ? Folder : null) is { } picked)
        {
            Folder = picked;
        }
    }

    /// <summary>In the background: adding reads the drive and writes to the inventory, which can wait for a running scan.</summary>
    [RelayCommand]
    private async Task Go()
    {
        if (string.IsNullOrWhiteSpace(Folder))
        {
            FolderError = "Choose a folder or drive.";
            return;
        }

        if (!IsMoving && MediaService.ValidateMediaId(MediaId) is { } idError)
        {
            MediaIdError = idError;
            return;
        }

        if (IsMoving && MatchIsLow && !_dialogs.Confirm(Title, MatchInfo + "\n\nUse this folder anyway?"))
        {
            return;
        }

        var folder = Folder.Trim();
        var id = MediaId.Trim();
        try
        {
            Result = await Task.Run(() => IsMoving ? _media.Relocate(_session, Moving!.MediaKey, folder) : _media.AddExternal(_session, folder, id));
            Close(true);
        }
        catch (MediaLocationException ex)
        {
            if (ex.Message.Contains("Media ID", StringComparison.Ordinal) || ex.Message.Contains("already in this inventory", StringComparison.Ordinal))
            {
                MediaIdError = ex.Message;
            }
            else
            {
                FolderError = ex.Message;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        {
            _dialogs.ShowError(Title, $"The media could not be {(IsMoving ? "moved" : "added")}.\n\n{ex.Message}", ex);
        }
    }

    [RelayCommand]
    private void Cancel() => Close(false);

    private static string Describe(ExternalFolder folder) => folder switch
    {
        { Volume: { } v, IsWholeDrive: true } =>
            $"The whole drive {folder.Path}, label “{v.Label}”, serial {v.Serial}. It is recognised by its serial if its drive letter changes.",
        { Volume: { } v } =>
            $"A folder on the drive with serial {v.Serial}{(v.Label.Length > 0 ? $" (“{v.Label}”)" : string.Empty)}. It is recognised by the serial if the drive letter changes.",
        _ when folder.Path.StartsWith(@"\\", StringComparison.Ordinal) => $"A network folder: {folder.Path}",
        _ => $"A folder: {folder.Path}",
    };
}
