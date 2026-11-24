using Accession.Data;
using Dapper;

namespace Accession.Tests.TestSupport;

/// <summary>Turns a newly created inventory back into a schema v1 file, as older versions of Accession wrote it.</summary>
public static class SchemaDowngrade
{
    /// <summary>A schema v3 file: no media outside the root (no location columns).</summary>
    public static void ToV3(string dbPath)
    {
        using var connection = SqliteConnectionFactory.Open(dbPath);
        connection.Execute(
            """
            ALTER TABLE InventoryConfig DROP COLUMN AllowExternalMedia;
            ALTER TABLE Media DROP COLUMN SourcePath;
            ALTER TABLE Media DROP COLUMN VolumeSerial;
            ALTER TABLE Media DROP COLUMN VolumeLabel;
            ALTER TABLE Media DROP COLUMN AddedOnMachine;
            UPDATE SchemaMigration SET Version = 3;
            UPDATE InventoryConfig SET SchemaVersion = 3;
            """);
    }

    /// <summary>A schema v2 file: saved searches without the computer columns.</summary>
    public static void ToV2(string dbPath)
    {
        ToV3(dbPath);
        using var connection = SqliteConnectionFactory.Open(dbPath);
        connection.Execute(
            """
            ALTER TABLE SavedSearch DROP COLUMN CreatedOnMachine;
            ALTER TABLE SavedSearch DROP COLUMN ModifiedOnMachine;
            UPDATE SchemaMigration SET Version = 2;
            UPDATE InventoryConfig SET SchemaVersion = 2;
            """);
    }

    public static void ToV1(string dbPath)
    {
        ToV3(dbPath);
        using var connection = SqliteConnectionFactory.Open(dbPath);
        connection.Execute(
            """
            DROP TABLE SavedSearchFile;
            DROP TABLE SavedSearch;
            UPDATE SchemaMigration SET Version = 1; -- a new inventory has one row, for the version it was created with
            UPDATE InventoryConfig SET SchemaVersion = 1;
            """);
    }
}
