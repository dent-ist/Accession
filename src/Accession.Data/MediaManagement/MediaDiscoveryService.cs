using Accession.Core.Model;
using Accession.Data.Repositories;
using Accession.Data.Sessions;

namespace Accession.Data.MediaManagement;

/// <summary>A folder under the root that is not registered as media.</summary>
public sealed record DiscoveredFolder(string MediaId, string FullPath, bool PreviouslyDeleted);

/// <summary>Outcome of comparing the root's subfolders with registered media (requirements 5.3).</summary>
public sealed record DiscoveryResult(
    bool RootAvailable,
    IReadOnlyList<DiscoveredFolder> NewFolders,
    IReadOnlyList<Media> MissingMedia,
    IReadOnlyList<Media> NeverScanned,
    IReadOnlyList<Media> Incomplete)
{
    public static readonly DiscoveryResult RootUnavailable = new(false, [], [], [], []);

    public bool HasFindings => NewFolders.Count > 0 || MissingMedia.Count > 0 || NeverScanned.Count > 0 || Incomplete.Count > 0;
}

/// <summary>Finds new, missing (or not connected), never-scanned and incomplete media (DSC-01 … DSC-05, MED-12).</summary>
public sealed class MediaDiscoveryService
{
    /// <summary>
    /// Lists the root's immediate subfolders and compares them with active media (case-insensitive), and checks that
    /// the media on other drives are connected. In a writable session, media whose folder disappeared (or whose drive
    /// is not connected) are marked Missing and restored when it is back. When the root cannot be reached, the media
    /// on other drives are still checked.
    /// </summary>
    public DiscoveryResult Discover(InventorySession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var root = session.Config.RootPath;

        IReadOnlyList<string>? folderNames = null; // null: the root cannot be reached
        try
        {
            if (Directory.Exists(root))
            {
                folderNames = MediaFolders.ListCandidateNames(root);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            folderNames = null;
        }

        var rootAvailable = folderNames is not null;
        var onDisk = (folderNames ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!rootAvailable && !session.Config.AllowExternalMedia)
        {
            return DiscoveryResult.RootUnavailable;
        }

        using var scope = session.Database.Open();
        var media = new MediaRepository(scope);
        var active = media.ListActive();

        // Is the media's folder there? Root media by the listing; media on other drives by their drive (serial).
        // Null: not known now (the root is offline).
        var locations = new MediaLocations(root, rootAvailable, active, session.Volumes);
        bool? Present(Media item) => item.IsExternal ? locations.Folder(item.MediaId)!.IsConnected
            : rootAvailable ? onDisk.Contains(item.MediaId)
            : null;

        if (!session.IsReadOnly)
        {
            using var transaction = scope.BeginTransaction();
            foreach (var item in active)
            {
                var present = Present(item);
                if (present == false && item.Status != MediaStatus.Missing)
                {
                    media.MarkMissing(item.MediaKey);
                }
                else if (present == true && item.Status == MediaStatus.Missing)
                {
                    media.RestoreFromMissing(item.MediaKey);
                }
            }

            transaction.Commit();
            active = media.ListActive();
        }

        var activeIds = active.Select(m => m.MediaId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var deletedIds = media.ListDeletedMediaIds();
        var newFolders = (folderNames ?? [])
            .Where(name => !activeIds.Contains(name))
            .Select(name => new DiscoveredFolder(name, MediaFolders.FullPath(root, name), deletedIds.Contains(name)))
            .ToList();

        // In a read-only session statuses can't be updated, so derive "missing" from the check directly.
        var missing = active.Where(m => m.Status == MediaStatus.Missing || Present(m) == false).ToList();
        return new DiscoveryResult(
            rootAvailable,
            newFolders,
            missing,
            active.Where(m => m.Status == MediaStatus.New && Present(m) == true).ToList(),
            active.Where(m => m.Status == MediaStatus.Incomplete && Present(m) == true).ToList());
    }
}
