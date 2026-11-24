using Accession.Core.Inventories;
using Accession.Core.Model;
using Accession.Core.Scanning;
using Accession.Data.Repositories;
using Accession.Data.Sessions;

namespace Accession.Data.MediaManagement;

/// <summary>Where a media's folder is now: <see cref="Current"/> (null when it cannot be reached) and where it is expected.</summary>
/// <param name="Expected">The recorded place: <c>&lt;root&gt;\&lt;MediaID&gt;</c> or the media's source folder.</param>
/// <param name="Current">The folder to read now: the expected one, or the same drive found under another letter; null when not connected.</param>
public sealed record MediaFolderLocation(string MediaId, bool IsExternal, string Expected, string? Current)
{
    public bool IsConnected => Current is not null;
}

/// <summary>
/// Turns stored paths (<c>\&lt;MediaID&gt;\folder\file.pdf</c>) into full paths. A media under the root is at
/// <c>&lt;root&gt;\&lt;MediaID&gt;</c>; a media from another folder or drive is at its source folder. A source on a
/// local drive must still be on the same drive (serial number): when the letter now holds another device the media is
/// not connected, and when the drive is found under another letter, that one is used. Make one per operation: each
/// media is looked up once.
/// </summary>
public sealed class MediaLocations
{
    private readonly string _root;
    private readonly bool _rootAvailable;
    private readonly IVolumeProbe _volumes;
    private readonly Dictionary<string, Media> _media;
    private readonly Dictionary<string, MediaFolderLocation> _resolved = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();
    private IReadOnlyList<(string Root, VolumeId? Id)>? _drives;

    public MediaLocations(string rootPath, bool rootAvailable, IEnumerable<Media> media, IVolumeProbe volumes)
    {
        ArgumentNullException.ThrowIfNull(media);
        _root = rootPath;
        _rootAvailable = rootAvailable;
        _volumes = volumes ?? throw new ArgumentNullException(nameof(volumes));
        _media = media.Where(m => !m.IsDeleted).ToDictionary(m => m.MediaId, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The session's media (active ones), read now.</summary>
    public static MediaLocations Load(InventorySession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        using var scope = session.Database.Open();
        return new MediaLocations(session.Config.RootPath, session.IsRootAvailable, new MediaRepository(scope).ListActive(), session.Volumes);
    }

    /// <summary>Every source folder of a media outside the root, as recorded (nothing may be written into them).</summary>
    public IReadOnlyList<string> ExternalSources => [.. _media.Values.Where(m => m.IsExternal).Select(m => m.SourcePath!)];

    /// <summary>
    /// Folders of the media outside the root, where recorded and (when a drive moved) where they are now: like the
    /// root, nothing is ever written into them.
    /// </summary>
    public IReadOnlyList<string> ExternalFolders() =>
        [.. _media.Values.Where(m => m.IsExternal)
            .Select(m => Folder(m.MediaId)!)
            .SelectMany(f => f.Current is { } current && !string.Equals(current, f.Expected, StringComparison.OrdinalIgnoreCase)
                ? new[] { f.Expected, current }
                : [f.Expected])];

    /// <summary>The media's folder now and where it is expected; null for an unknown Media ID.</summary>
    public MediaFolderLocation? Folder(string mediaId)
    {
        lock (_gate)
        {
            if (_resolved.TryGetValue(mediaId, out var known))
            {
                return known;
            }

            if (!_media.TryGetValue(mediaId, out var media))
            {
                return null;
            }

            var location = media.IsExternal ? ResolveExternal(media) : ResolveUnderRoot(media);
            _resolved[mediaId] = location;
            return location;
        }
    }

    /// <summary>Full path of a stored path to read now; null when its media is not connected (or unknown).</summary>
    public string? FullPath(string relativePath) => Combine(relativePath, current: true);

    /// <summary>Where a stored path is now, or where it was recorded when its media is not connected (to show or copy, not to read).</summary>
    public string DisplayPath(string relativePath) => FullPath(relativePath) ?? ExpectedFullPath(relativePath);

    /// <summary>Full path of a stored path where it is expected, connected or not (for messages and the clipboard).</summary>
    public string ExpectedFullPath(string relativePath) =>
        Combine(relativePath, current: false) ?? ScanPaths.ToFullPath(_root, relativePath);

    /// <summary>The Media ID a stored path belongs to: its first segment.</summary>
    public static string MediaIdOf(string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        var trimmed = relativePath.TrimStart('\\');
        var end = trimmed.IndexOf('\\', StringComparison.Ordinal);
        return end < 0 ? trimmed : trimmed[..end];
    }

    /// <summary>A stored path inside a media folder: the media's folder plus the path after <c>\&lt;MediaID&gt;\</c>.</summary>
    public static string Below(string mediaFolder, string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        var parts = relativePath.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length <= 1 ? mediaFolder : Path.Combine([mediaFolder, .. parts[1..]]);
    }

    private string? Combine(string relativePath, bool current)
    {
        if (Folder(MediaIdOf(relativePath)) is not { } location)
        {
            return null;
        }

        var folder = current ? location.Current : location.Expected;
        return folder is null ? null : Below(folder, relativePath);
    }

    private MediaFolderLocation ResolveUnderRoot(Media media)
    {
        var expected = MediaFolders.FullPath(_root, media.MediaId);
        return new MediaFolderLocation(media.MediaId, false, expected, _rootAvailable && Directory.Exists(expected) ? expected : null);
    }

    private MediaFolderLocation ResolveExternal(Media media)
    {
        var expected = media.SourcePath!;
        return new MediaFolderLocation(media.MediaId, true, expected, FindExternal(expected, media.VolumeSerial));
    }

    /// <summary>The source folder, on the drive with this serial (wherever it is now mounted); null when not connected.</summary>
    private string? FindExternal(string expected, string? serial)
    {
        if (string.IsNullOrEmpty(serial))
        {
            return Directory.Exists(expected) ? expected : null; // a network folder, or a drive whose serial is unknown
        }

        if (string.Equals(_volumes.Identify(expected)?.Serial, serial, StringComparison.OrdinalIgnoreCase) && Directory.Exists(expected))
        {
            return expected;
        }

        // The drive under another letter (or the letter now holds another device: then it is not this media).
        var tail = _volumes.PathOnDrive(expected);
        foreach (var (root, id) in Drives())
        {
            if (string.Equals(id?.Serial, serial, StringComparison.OrdinalIgnoreCase))
            {
                var candidate = tail.Length == 0 ? root : Path.Combine(root, tail);
                if (Directory.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private IReadOnlyList<(string Root, VolumeId? Id)> Drives() =>
        _drives ??= [.. _volumes.LocalDriveRoots().Select(root => (root, _volumes.Identify(root)))];
}
