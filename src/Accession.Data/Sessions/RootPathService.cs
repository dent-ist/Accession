using Accession.Core.Inventories;
using Accession.Core.Model;
using Accession.Data.Repositories;

namespace Accession.Data.Sessions;

/// <summary>Result of checking a candidate root folder before relocating (INV-09).</summary>
/// <param name="MediaNotFound">Registered media whose folder does not exist under the new root.</param>
/// <param name="Conflict">Why the folder cannot be the root (it overlaps a media on another drive); null when it can.</param>
public sealed record RootPathPreview(string NewRootPath, bool Exists, IReadOnlyList<string> MediaNotFound, string? Conflict = null);

/// <summary>Changes the inventory root folder after the inventory and media were moved (requirement INV-09).</summary>
public sealed class RootPathService
{
    /// <summary>Checks the new root and lists media that would not be found under it. Changes nothing.</summary>
    public RootPathPreview Preview(InventorySession session, string newRootPath)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(newRootPath);

        var normalized = PathRules.NormalizeDirectory(newRootPath);
        if (!Directory.Exists(normalized))
        {
            return new RootPathPreview(normalized, false, []);
        }

        using var scope = session.Database.Open();
        var active = new MediaRepository(scope).ListActive();

        // Media on other drives are not in the root: only the root's media are looked for, and the new root must not
        // overlap one of the others (its files would be counted twice).
        var missing = active
            .Where(m => !m.IsExternal && !Directory.Exists(Path.Combine(normalized, m.MediaId)))
            .Select(m => m.MediaId)
            .ToList();
        var overlapping = active.FirstOrDefault(m => m.IsExternal
            && (Core.Copying.CopyPaths.IsSameOrInside(normalized, m.SourcePath!) || Core.Copying.CopyPaths.IsSameOrInside(m.SourcePath!, normalized)));
        var conflict = overlapping is null ? null
            : $"This folder overlaps the media '{overlapping.MediaId}' ({overlapping.SourcePath}), which is not in the root: its files would be counted twice.";
        return new RootPathPreview(normalized, true, missing, conflict);
    }

    /// <summary>Updates <c>InventoryConfig.RootPath</c> and audits <c>RootPathChanged</c> with old/new path.</summary>
    public RootPathPreview Apply(InventorySession session, string newRootPath)
    {
        ArgumentNullException.ThrowIfNull(session);
        session.EnsureWritable();

        var preview = Preview(session, newRootPath);
        if (!preview.Exists)
        {
            throw new DirectoryNotFoundException($"The folder '{preview.NewRootPath}' does not exist or cannot be reached.");
        }

        if (preview.Conflict is { } conflict)
        {
            throw new InvalidOperationException(conflict);
        }

        var oldPath = session.Config.RootPath;
        using (var scope = session.Database.Open())
        using (var transaction = scope.BeginTransaction())
        {
            new InventoryConfigRepository(scope).UpdateRootPath(preview.NewRootPath);
            session.Audit.Write(scope, AuditAction.RootPathChanged, details: new
            {
                OldPath = oldPath,
                NewPath = preview.NewRootPath,
                preview.MediaNotFound,
            });
            transaction.Commit();
        }

        session.ReloadConfig();
        session.IsRootAvailable = true;
        return preview;
    }
}
