using Accession.Core.Copying;
using Accession.Core.Inventories;
using Accession.Core.Model;
using Accession.Data.Repositories;
using Accession.Data.Sessions;
using Dapper;

namespace Accession.Data.MediaManagement;

/// <summary>A folder or drive about to be added as media outside the root, as the app sees it.</summary>
/// <param name="Path">The folder (a mapped network drive as its UNC path).</param>
/// <param name="SuggestedMediaId">The volume label for a whole drive, else the folder name.</param>
/// <param name="Volume">The local drive it is on (serial and label); null for a network folder.</param>
public sealed record ExternalFolder(string Path, string SuggestedMediaId, VolumeId? Volume, bool IsWholeDrive);

/// <summary>Why a folder cannot be added (or a media moved) there; the message is for the user.</summary>
public sealed class MediaLocationException(string message) : Exception(message);

/// <summary>Media from other folders and drives besides the root: USB drives, other shares (MED-07 … MED-11).</summary>
public sealed partial class MediaService
{
    /// <summary>Longest Media ID: it becomes a folder name when files are copied out.</summary>
    public const int MaxMediaIdLength = 100;

    private static readonly IReadOnlySet<string> ReservedNames = new HashSet<string>(
        ["CON", "PRN", "AUX", "NUL", .. Enumerable.Range(1, 9).SelectMany(n => new[] { $"COM{n}", $"LPT{n}" })],
        StringComparer.OrdinalIgnoreCase);

    private static readonly char[] InvalidNameChars = ['\\', '/', ':', '*', '?', '"', '<', '>', '|'];

    /// <summary>What the folder is (drive, label, network) and the Media ID to suggest for it.</summary>
    public static ExternalFolder Describe(InventorySession session, string folder)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        var path = session.Volumes.ToStablePath(PathRules.NormalizeDirectory(folder));
        var volume = session.Volumes.Identify(path);
        var wholeDrive = volume is not null && session.Volumes.PathOnDrive(path).Length == 0;
        var name = wholeDrive
            ? (string.IsNullOrWhiteSpace(volume!.Label) ? "Drive_" + path.TrimEnd('\\', '/', ':') : volume.Label)
            : System.IO.Path.GetFileName(path.TrimEnd('\\', '/'));
        return new ExternalFolder(path, SuggestMediaId(name), volume, wholeDrive);
    }

    /// <summary>A usable Media ID from a label or folder name: characters not allowed in folder names become "_".</summary>
    public static string SuggestMediaId(string name)
    {
        var chars = (name ?? string.Empty).Trim().Select(c => char.IsControl(c) || InvalidNameChars.Contains(c) ? '_' : c).ToArray();
        var id = new string(chars).Trim().TrimEnd('.');
        return id.Length > MaxMediaIdLength ? id[..MaxMediaIdLength] : id;
    }

    /// <summary>Why <paramref name="mediaId"/> cannot be a Media ID (it names a folder when files are copied out); null when it can.</summary>
    public static string? ValidateMediaId(string? mediaId)
    {
        var id = mediaId?.Trim() ?? string.Empty;
        if (id.Length == 0)
        {
            return "Enter a Media ID.";
        }

        if (id.Length > MaxMediaIdLength)
        {
            return $"A Media ID can have at most {MaxMediaIdLength} characters.";
        }

        if (id.IndexOfAny(InvalidNameChars) >= 0 || id.Any(char.IsControl))
        {
            return "A Media ID cannot contain \\ / : * ? \" < > |.";
        }

        if (id is "." or ".." || id.EndsWith('.') || ReservedNames.Contains(id.Split('.')[0]))
        {
            return $"'{id}' cannot be used as a folder name in Windows.";
        }

        return MediaFolders.IgnoredAtRoot.Contains(id) ? $"'{id}' is a system folder name." : null;
    }

    /// <summary>
    /// Adds <paramref name="folder"/> (any folder or a whole drive) as a media outside the root, named
    /// <paramref name="mediaId"/>, with a <c>MediaAdded</c> audit entry. A folder on a local drive records the drive's
    /// serial, so the media is recognised under another drive letter and never confused with another device.
    /// </summary>
    /// <exception cref="MediaLocationException">The folder or the Media ID cannot be used.</exception>
    public Media AddExternal(InventorySession session, string folder, string mediaId)
    {
        ArgumentNullException.ThrowIfNull(session);
        session.EnsureWritable();
        if (!session.Config.AllowExternalMedia)
        {
            throw new MediaLocationException(
                "This inventory takes media from its root folder only. Allow media from other folders and drives in Inventory Properties first.");
        }

        var described = Describe(session, folder);
        var id = mediaId?.Trim() ?? string.Empty;
        if (ValidateMediaId(id) is { } idError)
        {
            throw new MediaLocationException(idError);
        }

        using var scope = session.Database.Open();
        using var transaction = scope.BeginTransaction();
        var media = new MediaRepository(scope);
        var active = media.ListActive();
        if (active.Any(m => string.Equals(m.MediaId, id, StringComparison.OrdinalIgnoreCase)))
        {
            throw new MediaLocationException($"'{id}' is already in this inventory. Choose another Media ID.");
        }

        if (session.IsRootAvailable && !string.IsNullOrWhiteSpace(session.Config.RootPath)
            && Directory.Exists(MediaFolders.FullPath(session.Config.RootPath, id)))
        {
            throw new MediaLocationException($"A folder named '{id}' is in the root folder (it would be found as a media there). Choose another Media ID.");
        }

        if (CheckLocation(session, described, active, movingKey: null) is { } locationError)
        {
            throw new MediaLocationException(locationError);
        }

        var deletedIds = media.ListDeletedMediaIds();
        var key = media.InsertExternal(id, described.Path, described.Volume?.Serial, described.Volume?.Label, Environment.MachineName,
            _timeProvider.GetUtcNow(), session.UserName);
        session.Audit.Write(scope, AuditAction.MediaAdded, id, new
        {
            Location = "External",
            SourcePath = described.Path,
            VolumeSerial = described.Volume?.Serial,
            VolumeLabel = described.Volume?.Label,
            described.IsWholeDrive,
            PreviouslyDeleted = deletedIds.Contains(id),
        });
        var added = media.Get(key)!;
        transaction.Commit();
        return added;
    }

    /// <summary>
    /// How much of a scanned media's top level is in <paramref name="folder"/>: (found, expected) names of its
    /// top-level folders and files. A low count means the folder is probably not this media. (0, 0) when never scanned.
    /// </summary>
    public static (int Found, int Expected) CompareTopLevel(InventorySession session, long mediaKey, string folder)
    {
        ArgumentNullException.ThrowIfNull(session);
        IReadOnlyList<string> names;
        using (var scope = session.Database.Open())
        {
            names = scope.Connection.Query<string>(
                """
                WITH top AS (SELECT FolderId FROM Folder WHERE MediaKey = @mediaKey AND ParentFolderId IS NULL)
                SELECT Name FROM Folder WHERE ParentFolderId IN (SELECT FolderId FROM top)
                UNION ALL
                SELECT Name FROM File WHERE FolderId IN (SELECT FolderId FROM top)
                LIMIT 500
                """,
                new { mediaKey }).AsList();
        }

        if (names.Count == 0 || !Directory.Exists(folder))
        {
            return (0, names.Count);
        }

        var onDisk = Directory.EnumerateFileSystemEntries(folder).Select(System.IO.Path.GetFileName).OfType<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return (names.Count(onDisk.Contains), names.Count);
    }

    /// <summary>
    /// Points a media outside the root at <paramref name="folder"/>: its drive letter or share changed, or the files
    /// were moved. Stored paths do not change. Audited as <c>MediaLocationChanged</c>; a Missing media gets its status back.
    /// </summary>
    /// <exception cref="MediaLocationException">The folder cannot be used.</exception>
    public Media Relocate(InventorySession session, long mediaKey, string folder)
    {
        ArgumentNullException.ThrowIfNull(session);
        session.EnsureWritable();
        var described = Describe(session, folder);
        using var scope = session.Database.Open();
        using var transaction = scope.BeginTransaction();
        var media = new MediaRepository(scope);
        if (media.Get(mediaKey) is not { IsDeleted: false } item)
        {
            throw new MediaLocationException("The media does not exist or was deleted.");
        }

        if (!item.IsExternal)
        {
            throw new MediaLocationException($"'{item.MediaId}' is in the root folder: change the root path instead.");
        }

        if (BusyStatuses.Contains(item.Status))
        {
            throw new MediaLocationException($"'{item.MediaId}' is {item.Status.ToString().ToLowerInvariant()}. Cancel the scan first.");
        }

        if (CheckLocation(session, described, media.ListActive(), movingKey: mediaKey) is { } locationError)
        {
            throw new MediaLocationException(locationError);
        }

        media.UpdateLocation(mediaKey, described.Path, described.Volume?.Serial, described.Volume?.Label);
        media.RestoreFromMissing(mediaKey);
        session.Audit.Write(scope, AuditAction.MediaLocationChanged, item.MediaId, new
        {
            Old = new { item.SourcePath, item.VolumeSerial, item.VolumeLabel },
            New = new { SourcePath = described.Path, VolumeSerial = described.Volume?.Serial, VolumeLabel = described.Volume?.Label },
        });
        var moved = media.Get(mediaKey)!;
        transaction.Commit();
        return moved;
    }

    /// <summary>Why media cannot come from <paramref name="folder"/> (overlaps, root, inventory file); null when it can.</summary>
    private static string? CheckLocation(InventorySession session, ExternalFolder folder, IReadOnlyList<Media> active, long? movingKey)
    {
        var path = folder.Path;
        if (!Directory.Exists(path))
        {
            return "The folder does not exist or cannot be reached.";
        }

        var root = session.Config.RootPath;
        if (!string.IsNullOrWhiteSpace(root))
        {
            if (CopyPaths.IsSameOrInside(path, root))
            {
                return Path.GetDirectoryName(path) is { } parent && PathRules.AreSameDirectory(parent, root)
                    ? "This folder is in the root folder: add it as a media of the root (Add media, then tick it)."
                    : "This folder is inside the root folder, under one of its media: its files would be counted twice.";
            }

            if (CopyPaths.IsSameOrInside(root, path))
            {
                return "The root folder is inside this folder: its media would be counted twice.";
            }
        }

        if (CopyPaths.IsSameOrInside(session.DbPath, path))
        {
            return "The inventory file is in this folder, so it would be inventoried as evidence. Choose another folder, or move the inventory file.";
        }

        var others = new MediaLocations(root, session.IsRootAvailable, active.Where(m => m.MediaKey != movingKey), session.Volumes);
        foreach (var other in active.Where(m => m.IsExternal && m.MediaKey != movingKey))
        {
            var location = others.Folder(other.MediaId)!;
            var sameDrive = folder.Volume is { } volume && string.Equals(volume.Serial, other.VolumeSerial, StringComparison.OrdinalIgnoreCase)
                && Overlap(session.Volumes.PathOnDrive(path), session.Volumes.PathOnDrive(other.SourcePath!));
            if (sameDrive || Overlap(path, location.Expected) || (location.Current is { } current && Overlap(path, current)))
            {
                return $"This folder overlaps the media '{other.MediaId}' ({location.Current ?? location.Expected}): its files would be counted twice.";
            }
        }

        return null;
    }

    /// <summary>True when one folder is the other or inside it ("" is a drive's root: it contains everything on the drive).</summary>
    private static bool Overlap(string a, string b) =>
        a.Length == 0 || b.Length == 0 || CopyPaths.IsSameOrInside(a, b) || CopyPaths.IsSameOrInside(b, a);
}
