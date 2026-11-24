using Accession.Core.Model;
using Accession.Data.MediaManagement;
using Accession.Data.Repositories;
using Accession.Data.Sessions;
using Accession.Tests.TestSupport;

namespace Accession.Tests.Data;

/// <summary>Media from other folders and drives besides the root (USB drives, other shares).</summary>
public sealed class ExternalMediaTests : IDisposable
{
    private readonly TestSession _test = new();
    private readonly FakeVolumes _volumes;
    private readonly MediaService _media;

    public ExternalMediaTests()
    {
        _volumes = new FakeVolumes(_test.Temp.Combine("drives"));
        _test.Session.Volumes = _volumes;
        _media = new MediaService(_test.Time);
        new InventoryPropertiesService().SetAllowExternalMedia(_test.Session, true);
    }

    public void Dispose() => _test.Dispose();

    /// <summary>A USB drive with a custodian folder on it.</summary>
    private string Usb(string name = "E", string serial = "1A2B-3C4D", string label = "CLIENT_USB01")
    {
        var root = _volumes.Mount(name, serial, label);
        Directory.CreateDirectory(Path.Combine(root, "Custodian", "Mail"));
        return root;
    }

    private IReadOnlyList<AuditEntry> Audit(AuditAction action) => _test.Session.Audit.Query(new() { Actions = [action] });

    [Fact]
    public void A_whole_drive_is_suggested_by_its_label_and_a_folder_by_its_name()
    {
        var usb = Usb();

        var drive = MediaService.Describe(_test.Session, usb);
        Assert.Equal(("CLIENT_USB01", true, "1A2B-3C4D"), (drive.SuggestedMediaId, drive.IsWholeDrive, drive.Volume!.Serial));

        var folder = MediaService.Describe(_test.Session, Path.Combine(usb, "Custodian"));
        Assert.Equal(("Custodian", false), (folder.SuggestedMediaId, folder.IsWholeDrive));

        Assert.Equal("ACME_ disk_1", MediaService.SuggestMediaId(" ACME/ disk:1. "));
    }

    [Fact]
    public void A_drive_is_added_with_its_serial_and_audited()
    {
        var usb = Usb();

        var added = _media.AddExternal(_test.Session, usb, "CLIENT_USB01");

        Assert.Equal((@"\CLIENT_USB01\", usb, "1A2B-3C4D", "CLIENT_USB01", MediaStatus.New),
            (added.RelativePath, added.SourcePath, added.VolumeSerial, added.VolumeLabel, added.Status));
        Assert.True(added.IsExternal);
        var entry = Assert.Single(Audit(AuditAction.MediaAdded));
        Assert.Equal("CLIENT_USB01", entry.MediaId);
        Assert.Contains("\"location\":\"External\"", entry.Details, StringComparison.Ordinal);
        Assert.Contains("1A2B-3C4D", entry.Details, StringComparison.Ordinal);
        Assert.Equal(Path.Combine(usb, "Mail.pst"), MediaLocations.Load(_test.Session).FullPath(@"\CLIENT_USB01\Mail.pst"));
    }

    [Theory]
    [InlineData("", "Enter a Media ID.")]
    [InlineData("a/b", "cannot contain")]
    [InlineData("CON", "cannot be used as a folder name")]
    [InlineData("disk.", "cannot be used as a folder name")]
    [InlineData("$RECYCLE.BIN", "system folder")]
    public void Media_ids_must_work_as_folder_names(string id, string message)
    {
        var error = Assert.Throws<MediaLocationException>(() => _media.AddExternal(_test.Session, Usb(), id));
        Assert.Contains(message, error.Message, StringComparison.Ordinal);
        Assert.Null(MediaService.ValidateMediaId("CLIENT_USB01"));
    }

    [Fact]
    public void Media_ids_are_unique_and_do_not_shadow_a_folder_in_the_root()
    {
        _media.AddExternal(_test.Session, Usb(), "USB01");
        var second = Usb("F", "5555-6666");
        Assert.Contains("already in this inventory", Assert.Throws<MediaLocationException>(() => _media.AddExternal(_test.Session, second, "usb01")).Message,
            StringComparison.Ordinal);

        _test.CreateFolders("Laptop");
        Assert.Contains("is in the root folder", Assert.Throws<MediaLocationException>(() => _media.AddExternal(_test.Session, second, "Laptop")).Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Folders_that_overlap_another_media_the_root_or_the_inventory_file_are_refused()
    {
        var usb = Usb();
        _media.AddExternal(_test.Session, Path.Combine(usb, "Custodian"), "CUST");

        string Refused(string folder) => Assert.Throws<MediaLocationException>(() => _media.AddExternal(_test.Session, folder, "X")).Message;

        Assert.Contains("overlaps the media 'CUST'", Refused(usb), StringComparison.Ordinal); // the whole drive contains it
        Assert.Contains("overlaps the media 'CUST'", Refused(Path.Combine(usb, "Custodian", "Mail")), StringComparison.Ordinal);

        _test.CreateFolders("M1");
        Assert.Contains("add it as a media of the root", Refused(Path.Combine(_test.Root, "M1")), StringComparison.Ordinal);
        Directory.CreateDirectory(Path.Combine(_test.Root, "M1", "sub"));
        Assert.Contains("inside the root folder", Refused(Path.Combine(_test.Root, "M1", "sub")), StringComparison.Ordinal);
        Assert.Contains("root folder is inside", Refused(_test.Temp.Path), StringComparison.Ordinal);

        Assert.Contains("does not exist", Refused(Path.Combine(usb, "nothing")), StringComparison.Ordinal);
        Assert.NotNull(_media.AddExternal(_test.Session, Usb("F", "5555-6666"), "F_DRIVE").SourcePath); // another drive is fine
    }

    [Fact]
    public void The_same_drive_under_another_letter_still_overlaps()
    {
        var e = Usb();
        _media.AddExternal(_test.Session, Path.Combine(e, "Custodian"), "CUST");
        _volumes.Unmount("E");
        var f = _volumes.Mount("F", "1A2B-3C4D", "CLIENT_USB01"); // the same drive, now F:
        Directory.CreateDirectory(Path.Combine(f, "Custodian", "Mail"));

        var error = Assert.Throws<MediaLocationException>(() => _media.AddExternal(_test.Session, f, "WHOLE"));

        Assert.Contains("overlaps the media 'CUST'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Media_from_other_drives_need_the_option_and_the_option_stays_while_they_exist()
    {
        var properties = new InventoryPropertiesService();
        _media.AddExternal(_test.Session, Usb(), "USB01");

        Assert.Throws<InvalidOperationException>(() => properties.SetAllowExternalMedia(_test.Session, false));

        using (var scope = _test.Session.Database.Open())
        {
            _media.Delete(_test.Session, new MediaRepository(scope).FindActive("USB01")!.MediaKey);
        }

        Assert.True(properties.SetAllowExternalMedia(_test.Session, false));
        Assert.False(_test.Session.Config.AllowExternalMedia);
        Assert.Contains("root folder only", Assert.Throws<MediaLocationException>(() => _media.AddExternal(_test.Session, Usb("F", "2"), "X")).Message,
            StringComparison.Ordinal);
        Assert.Contains(Audit(AuditAction.ConfigUpdated), e => e.Details!.Contains("AllowExternalMedia", StringComparison.Ordinal));
    }

    [Fact]
    public void A_media_can_be_pointed_at_its_new_place_and_gets_its_status_back()
    {
        var old = Usb();
        var added = _media.AddExternal(_test.Session, old, "USB01");
        using (var scope = _test.Session.Database.Open())
        {
            new MediaRepository(scope).MarkMissing(added.MediaKey);
        }

        var copy = _test.Temp.Combine("share", "USB01 copy");
        Directory.CreateDirectory(copy);
        var moved = _media.Relocate(_test.Session, added.MediaKey, copy);

        Assert.Equal((copy, (string?)null, MediaStatus.New), (moved.SourcePath, moved.VolumeSerial, moved.Status));
        Assert.Single(Audit(AuditAction.MediaLocationChanged));
        Assert.Equal(Path.Combine(copy, "a.txt"), MediaLocations.Load(_test.Session).FullPath(@"\USB01\a.txt"));
    }

    [Fact]
    public void Comparing_the_top_level_tells_whether_a_folder_is_the_same_media()
    {
        var usb = Usb();
        var added = _media.AddExternal(_test.Session, usb, "USB01");
        using (var scope = _test.Session.Database.Open())
        {
            var top = ScanRows.AddFolder(scope, added.MediaKey, @"\USB01\");
            ScanRows.AddFolder(scope, added.MediaKey, @"\USB01\Custodian\", top);
            ScanRows.AddFile(scope, added.MediaKey, top, "readme.txt", 1);
        }

        Assert.Equal((1, 2), MediaService.CompareTopLevel(_test.Session, added.MediaKey, usb)); // Custodian is there, readme.txt is not
        Assert.Equal((0, 2), MediaService.CompareTopLevel(_test.Session, added.MediaKey, _test.Root));
    }
}
