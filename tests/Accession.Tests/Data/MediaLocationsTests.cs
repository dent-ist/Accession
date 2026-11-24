using Accession.Core.Inventories;
using Accession.Core.Model;
using Accession.Data.MediaManagement;
using Accession.Tests.TestSupport;

namespace Accession.Tests.Data;

/// <summary>
/// Stored paths (<c>\MediaID\folder\file</c>) to full paths: under the root, or at a media's own folder on another
/// drive, which is recognised by its serial number wherever it is mounted now.
/// </summary>
public sealed class MediaLocationsTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly string _root;
    private readonly FakeVolumes _volumes;

    public MediaLocationsTests()
    {
        _volumes = new FakeVolumes(_temp.Path);
        _root = _temp.Combine("root");
        Directory.CreateDirectory(Path.Combine(_root, "M1", "docs"));
    }

    public void Dispose() => _temp.Dispose();

    /// <summary>A "drive": a temp folder standing for its root, with a serial.</summary>
    private string Drive(string name, string serial)
    {
        var root = _volumes.Mount(name, serial);
        Directory.CreateDirectory(Path.Combine(root, "Custodian"));
        return root;
    }

    private static Media UnderRoot(string id) => new() { MediaId = id, RelativePath = $@"\{id}\" };

    private static Media External(string id, string source, string? serial) =>
        new() { MediaId = id, RelativePath = $@"\{id}\", SourcePath = source, VolumeSerial = serial, VolumeLabel = "USB" };

    private MediaLocations Locations(params Media[] media) => new(_root, rootAvailable: true, media, _volumes);

    [Fact]
    public void A_media_under_the_root_is_root_plus_the_stored_path()
    {
        var locations = Locations(UnderRoot("M1"));

        Assert.Equal(Path.Combine(_root, "M1", "docs", "a.txt"), locations.FullPath(@"\M1\docs\a.txt"));
        Assert.Equal(Path.Combine(_root, "M1"), locations.Folder("m1")!.Current); // Media IDs are case-insensitive
        Assert.Null(locations.FullPath(@"\Unknown\a.txt"));
    }

    [Fact]
    public void A_media_on_another_drive_is_its_source_folder_plus_the_path_after_the_media_id()
    {
        var usb = Drive("usb", "1A2B-3C4D");
        var locations = Locations(UnderRoot("M1"), External("CLIENT_USB01", Path.Combine(usb, "Custodian"), "1A2B-3C4D"));

        Assert.Equal(Path.Combine(usb, "Custodian", "Mail", "x.pst"), locations.FullPath(@"\CLIENT_USB01\Mail\x.pst"));
        Assert.Equal(Path.Combine(usb, "Custodian"), locations.FullPath(@"\CLIENT_USB01\"));
        Assert.Equal([Path.Combine(usb, "Custodian")], locations.ExternalSources);
    }

    [Fact]
    public void The_drive_is_found_under_another_letter_by_its_serial()
    {
        var old = _temp.Combine("E"); // where it was when the media was added: now gone
        var now = Drive("F", "1A2B-3C4D");
        var locations = Locations(External("USB01", Path.Combine(old, "Custodian"), "1A2B-3C4D"));

        var folder = locations.Folder("USB01")!;
        Assert.Equal(Path.Combine(now, "Custodian"), folder.Current);
        Assert.Equal(Path.Combine(old, "Custodian"), folder.Expected);
        Assert.Equal(Path.Combine(now, "Custodian", "a.txt"), locations.FullPath(@"\USB01\a.txt"));
    }

    [Fact]
    public void Another_device_at_the_same_letter_is_not_the_media()
    {
        var e = Drive("E", "9999-0000"); // E: now holds someone else's drive, with the same folder on it
        var locations = Locations(External("USB01", Path.Combine(e, "Custodian"), "1A2B-3C4D"));

        Assert.False(locations.Folder("USB01")!.IsConnected);
        Assert.Null(locations.FullPath(@"\USB01\a.txt")); // never read from the wrong drive
        Assert.Equal(Path.Combine(e, "Custodian", "a.txt"), locations.ExpectedFullPath(@"\USB01\a.txt"));
    }

    [Fact]
    public void A_network_folder_without_a_serial_is_connected_when_it_exists()
    {
        var share = _temp.Combine("share", "Laptop");
        var locations = Locations(External("LAPTOP", share, serial: null));
        Assert.False(locations.Folder("LAPTOP")!.IsConnected);

        Directory.CreateDirectory(share);
        Assert.True(Locations(External("LAPTOP", share, serial: null)).Folder("LAPTOP")!.IsConnected);
    }

    [Fact]
    public void Root_media_are_not_connected_while_the_root_is_offline_but_other_drives_are()
    {
        var usb = Drive("usb", "1A2B-3C4D");
        var locations = new MediaLocations(_root, rootAvailable: false,
            [UnderRoot("M1"), External("USB01", Path.Combine(usb, "Custodian"), "1A2B-3C4D")], _volumes);

        Assert.Null(locations.FullPath(@"\M1\docs\a.txt"));
        Assert.Equal(Path.Combine(_root, "M1", "docs", "a.txt"), locations.ExpectedFullPath(@"\M1\docs\a.txt"));
        Assert.True(locations.Folder("USB01")!.IsConnected);
    }
}
