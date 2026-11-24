namespace Accession.Core.Model;

/// <summary>The single <c>InventoryConfig</c> row: inventory identity, matter information and root folder.</summary>
public sealed class InventoryConfig
{
    public Guid InventoryGuid { get; set; }
    public int SchemaVersion { get; set; }

    /// <summary>
    /// Folder whose subfolders are media. Stored paths start with the Media ID; for these media the full path is
    /// root + stored path.
    /// </summary>
    public string RootPath { get; set; } = string.Empty;

    /// <summary>Media may also come from other folders and drives (USB drives, other shares), not only the root.</summary>
    public bool AllowExternalMedia { get; set; }

    public string ClientName { get; set; } = string.Empty;

    /// <summary>The user-facing "Client ID".</summary>
    public string ClientCode { get; set; } = string.Empty;

    public string MatterName { get; set; } = string.Empty;

    /// <summary>The user-facing "Matter ID".</summary>
    public string MatterCode { get; set; } = string.Empty;

    public string? Description { get; set; }
    public string? MatterUrl { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
    public string CreatedOnMachine { get; set; } = string.Empty;
    public string CreatedAppVersion { get; set; } = string.Empty;

    public DateTimeOffset? LastOpenedAtUtc { get; set; }
    public string? LastOpenedBy { get; set; }

    /// <summary>Where the inventory file was last opened from.</summary>
    public string? LastDbPath { get; set; }
}
