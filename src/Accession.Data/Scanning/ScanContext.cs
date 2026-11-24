using Accession.Core.Model;
using Accession.Core.Scanning;

namespace Accession.Data.Scanning;

/// <summary>A folder waiting to be listed.</summary>
public sealed record FolderWork(long FolderId, string RelativePath);

/// <summary>A file waiting to be hashed, with the size and modified time captured at enumeration (SCN-41).</summary>
public sealed record HashWork(long FileId, string RelativePath, long Size, DateTimeOffset? ModifiedUtc);

/// <summary>Everything the scan phases share for one media scan run.</summary>
public sealed class ScanContext
{
    public required long MediaKey { get; init; }
    public required string MediaId { get; init; }
    public required long ScanId { get; init; }
    /// <summary>The media's folder on disk now: <c>&lt;root&gt;\&lt;MediaID&gt;</c>, or its source folder on another drive.</summary>
    public required string MediaFolder { get; init; }
    public required ScanDbWriter Writer { get; init; }
    public required ScanIdAllocator Ids { get; init; }
    public required ScanCounters Counters { get; init; }
    public required PauseGate Pause { get; init; }
    public required NetworkRetry Retry { get; init; }
    public required TimeProvider TimeProvider { get; init; }

    /// <summary>Full path of a stored path (<c>\&lt;MediaID&gt;\…</c>) of this media.</summary>
    public string FullPath(string relativePath) => MediaManagement.MediaLocations.Below(MediaFolder, relativePath);

    /// <summary>
    /// True when the media is a whole drive (<c>E:\</c>): its top level has <c>$RECYCLE.BIN</c> and <c>System Volume
    /// Information</c>, which are skipped there as at the root (DSC-05).
    /// </summary>
    public bool IsDriveRoot => string.Equals(Path.GetPathRoot(MediaFolder), MediaFolder, StringComparison.OrdinalIgnoreCase);

    public ErrorCommand Error(string relativePath, ScanItemType itemType, ScanErrorType type, ScanErrorSeverity severity, int? code, string message) =>
        new(new ScanErrorEntry
        {
            MediaKey = MediaKey,
            ScanId = ScanId,
            RelativePath = relativePath,
            ItemType = itemType,
            ErrorType = type,
            Severity = severity,
            ErrorCode = code,
            Message = message,
            OccurredAtUtc = TimeProvider.GetUtcNow(),
        });

    public ErrorCommand Error(string relativePath, ScanItemType itemType, Exception exception)
    {
        var info = ScanErrorClassifier.Classify(exception);
        return Error(relativePath, itemType, info.Type, info.Severity, info.ErrorCode, info.Message);
    }
}
