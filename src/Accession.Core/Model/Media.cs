namespace Accession.Core.Model;

/// <summary>A media folder registered in the inventory (one row of the <c>Media</c> table).</summary>
public sealed class Media
{
    public long MediaKey { get; set; }

    /// <summary>
    /// The business "Media ID": the folder name under the root, or the name chosen for a media on another drive or
    /// folder. It is the first segment of every stored path of the media.
    /// </summary>
    public string MediaId { get; set; } = string.Empty;

    /// <summary>Stored path of the media folder, <c>\&lt;MediaID&gt;\</c>, e.g. <c>\123-123_001\</c>.</summary>
    public string RelativePath { get; set; } = string.Empty;

    /// <summary>
    /// For a media outside the root (a USB drive, another share): its folder, e.g. <c>E:\</c> or <c>\\srv\intake\Laptop</c>.
    /// Null for a media under the root (<c>&lt;root&gt;\&lt;MediaID&gt;</c>).
    /// </summary>
    public string? SourcePath { get; set; }

    /// <summary>Serial number of the local drive the source is on (<c>1A2B-3C4D</c>): the drive is found again by it.</summary>
    public string? VolumeSerial { get; set; }

    /// <summary>Label of that drive when the media was added (or last relocated).</summary>
    public string? VolumeLabel { get; set; }

    /// <summary>The computer the media was added on (drive letters are per computer).</summary>
    public string? AddedOnMachine { get; set; }

    /// <summary>True for a media outside the root folder.</summary>
    public bool IsExternal => SourcePath is not null;

    public MediaStatus Status { get; set; }

    /// <summary>Status to restore when a Missing folder reappears.</summary>
    public MediaStatus? StatusBeforeMissing { get; set; }

    public DateTimeOffset AddedAtUtc { get; set; }
    public string AddedBy { get; set; } = string.Empty;

    /// <summary>Number of full scans (resumes are not counted).</summary>
    public int ScanCount { get; set; }

    public DateTimeOffset? LastScanStartedUtc { get; set; }
    public DateTimeOffset? LastScanCompletedUtc { get; set; }

    public long FolderCount { get; set; }
    public long FileCount { get; set; }
    public long TotalBytes { get; set; }
    public long HashedCount { get; set; }
    public long ErrorCount { get; set; }

    public bool IsDeleted { get; set; }
    public DateTimeOffset? DeletedAtUtc { get; set; }
    public string? DeletedBy { get; set; }
}
