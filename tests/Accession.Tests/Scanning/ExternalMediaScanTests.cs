using Accession.Core.Model;
using Accession.Core.Runtime;
using Accession.Core.Scanning;
using Accession.Data.MediaManagement;
using Accession.Data.Repositories;
using Accession.Data.Scanning;
using Accession.Data.Sessions;
using Accession.Tests.TestSupport;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;

namespace Accession.Tests.Scanning;

/// <summary>Scanning a media outside the root: a whole USB drive, found by its serial, also while the root is offline.</summary>
public sealed class ExternalMediaScanTests : IAsyncDisposable
{
    private readonly TestSession _test = new();
    private readonly FakeVolumes _volumes;
    private readonly ScanCoordinator _coordinator;

    public ExternalMediaScanTests()
    {
        _volumes = new FakeVolumes(_test.Temp.Combine("drives"));
        _test.Session.Volumes = _volumes;
        new InventoryPropertiesService().SetAllowExternalMedia(_test.Session, true);
        _coordinator = new ScanCoordinator(_test.Session, new FakeLister(), new FakeHasher(), () => new ScanOptions(2, 2, 50),
            new App(), TimeProvider.System, NullLogger<ScanCoordinator>.Instance);
    }

    public async ValueTask DisposeAsync()
    {
        await _coordinator.DisposeAsync();
        _test.Dispose();
    }

    /// <summary>A drive E: with files, a recycle bin and a system folder at its top.</summary>
    private string Drive(string name = "E")
    {
        var root = _volumes.Mount(name, "1A2B-3C4D", "CLIENT_USB01");
        Write(Path.Combine(root, "Mail", "a.msg"), "message");
        Write(Path.Combine(root, "readme.txt"), "hello");
        Write(Path.Combine(root, "$RECYCLE.BIN", "S-1-5", "deleted.doc"), "x");
        Write(Path.Combine(root, "System Volume Information", "tracking.log"), "y");
        return root;
    }

    private static void Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private async Task WaitIdleAsync()
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (_coordinator.IsBusy)
        {
            Assert.True(DateTime.UtcNow < deadline, "The scan did not finish.");
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
    }

    private List<string> Paths(string table)
    {
        using var scope = _test.Session.Database.Open();
        return [.. scope.Connection.Query<string>(table == "File"
            ? "SELECT fo.RelativePath || f.Name FROM File f JOIN Folder fo ON fo.FolderId = f.FolderId ORDER BY 1"
            : "SELECT RelativePath FROM Folder ORDER BY 1")];
    }

    [Fact]
    public async Task A_whole_drive_is_scanned_with_its_media_id_as_the_first_path_segment_and_without_its_system_folders()
    {
        var media = new MediaService(_test.Time).AddExternal(_test.Session, Drive(), "USB01");

        _coordinator.Enqueue(media.MediaKey, ScanType.Full);
        await WaitIdleAsync();

        Assert.Equal([@"\USB01\", @"\USB01\Mail\"], Paths("Folder"));
        Assert.Equal([@"\USB01\Mail\a.msg", @"\USB01\readme.txt"], Paths("File"));
        using var scope = _test.Session.Database.Open();
        var scanned = new MediaRepository(scope).Get(media.MediaKey)!;
        Assert.Equal((MediaStatus.Completed, 2L, 2L), (scanned.Status, scanned.FileCount, scanned.HashedCount));
    }

    [Fact]
    public async Task The_drive_is_scanned_under_its_new_letter_and_not_when_it_is_unplugged()
    {
        var media = new MediaService(_test.Time).AddExternal(_test.Session, Drive("E"), "USB01");
        _volumes.Unmount("E");

        var error = Assert.Throws<InvalidOperationException>(() => _coordinator.Enqueue(media.MediaKey, ScanType.Full));
        Assert.Contains("'USB01' is not connected", error.Message, StringComparison.Ordinal);

        Drive("F"); // plugged in again, as F:
        _test.Session.IsRootAvailable = false; // and the root is offline: this media can still be scanned
        _coordinator.Enqueue(media.MediaKey, ScanType.Full);
        await WaitIdleAsync();

        Assert.Contains(@"\USB01\Mail\a.msg", Paths("File"));
    }

    private sealed class App : IAppInfo
    {
        public string Version => "0.1.0";
    }
}
