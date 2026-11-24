using Accession.Data.Schema;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Accession.Data.Migrations;

/// <summary>Schema v4: InventoryConfig.AllowExternalMedia and the Media location columns. Existing media stay under the root.</summary>
internal sealed class V004ExternalMedia : IMigration
{
    public const string Script = "V004_ExternalMedia.sql";

    public int ToVersion => 4;

    public void Apply(SqliteConnection connection, SqliteTransaction transaction) =>
        connection.Execute(InventorySchema.LoadScript(Script), transaction: transaction);
}
