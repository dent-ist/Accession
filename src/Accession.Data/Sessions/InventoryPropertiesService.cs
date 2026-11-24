using Accession.Core.Inventories;
using Accession.Core.Model;
using Accession.Data.Repositories;

namespace Accession.Data.Sessions;

/// <summary>Edits the matter fields of an open inventory (requirement INV-07).</summary>
public sealed class InventoryPropertiesService
{
    /// <summary>
    /// Validates and saves <paramref name="matter"/>. Only changed fields are audited (<c>ConfigUpdated</c> with old
    /// and new values). Returns false when nothing changed.
    /// </summary>
    /// <exception cref="InventoryValidationException">A field is not valid.</exception>
    public bool UpdateMatter(InventorySession session, MatterInfo matter)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(matter);
        session.EnsureWritable();

        var errors = InventoryValidation.ValidateMatter(matter.ClientName, matter.ClientCode, matter.MatterName, matter.MatterCode, matter.MatterUrl);
        if (errors.Count > 0)
        {
            throw new InventoryValidationException(errors);
        }

        var updated = Normalize(matter);
        var current = session.Config;
        var changes = new Dictionary<string, object?>();
        void Compare(string field, string? oldValue, string? newValue)
        {
            if (!string.Equals(oldValue, newValue, StringComparison.Ordinal))
            {
                changes[field] = new { Old = oldValue, New = newValue };
            }
        }

        Compare(InventoryFields.ClientName, current.ClientName, updated.ClientName);
        Compare(InventoryFields.ClientCode, current.ClientCode, updated.ClientCode);
        Compare(InventoryFields.MatterName, current.MatterName, updated.MatterName);
        Compare(InventoryFields.MatterCode, current.MatterCode, updated.MatterCode);
        Compare(InventoryFields.Description, current.Description, updated.Description);
        Compare(InventoryFields.MatterUrl, current.MatterUrl, updated.MatterUrl);
        if (changes.Count == 0)
        {
            return false;
        }

        using (var scope = session.Database.Open())
        using (var transaction = scope.BeginTransaction())
        {
            new InventoryConfigRepository(scope).UpdateMatter(updated);
            session.Audit.Write(scope, AuditAction.ConfigUpdated, details: new { Changes = changes });
            transaction.Commit();
        }

        session.ReloadConfig();
        return true;
    }

    /// <summary>
    /// Allows media from other folders and drives besides the root, or stops allowing them (only while none is in the
    /// inventory). Audited as <c>ConfigUpdated</c>. Returns false when nothing changed.
    /// </summary>
    public bool SetAllowExternalMedia(InventorySession session, bool allow)
    {
        ArgumentNullException.ThrowIfNull(session);
        session.EnsureWritable();
        if (session.Config.AllowExternalMedia == allow)
        {
            return false;
        }

        using (var scope = session.Database.Open())
        using (var transaction = scope.BeginTransaction())
        {
            if (!allow && new MediaRepository(scope).ListActive().Count(m => m.IsExternal) is > 0 and var external)
            {
                throw new InvalidOperationException(
                    $"{external:N0} media in this inventory are outside the root folder. Delete them first to allow media from the root folder only.");
            }

            new InventoryConfigRepository(scope).UpdateAllowExternalMedia(allow);
            session.Audit.Write(scope, AuditAction.ConfigUpdated, details: new
            {
                Changes = new Dictionary<string, object?> { ["AllowExternalMedia"] = new { Old = !allow, New = allow } },
            });
            transaction.Commit();
        }

        session.ReloadConfig();
        return true;
    }

    private static MatterInfo Normalize(MatterInfo matter) => new(
        matter.ClientName.Trim(),
        matter.ClientCode.Trim(),
        matter.MatterName.Trim(),
        matter.MatterCode.Trim(),
        string.IsNullOrWhiteSpace(matter.Description) ? null : matter.Description.Trim(),
        string.IsNullOrWhiteSpace(matter.MatterUrl) ? null : matter.MatterUrl.Trim());
}
