using Accession.Data.MediaManagement;
using Accession.Data.Repositories;
using Accession.Data.Sessions;
using Accession.Presentation.ViewModels;
using Accession.Presentation.WebForms;
using Accession.Tests.TestSupport;
using Accession.Tests.WebUi;

namespace Accession.Tests.Presentation;

/// <summary>The dialog that adds a media from another folder or drive, or points one at its new place.</summary>
public sealed class ExternalMediaViewModelTests : IDisposable
{
    private readonly TestSession _test = new();
    private readonly FakeVolumes _volumes;
    private readonly MediaService _media;

    public ExternalMediaViewModelTests()
    {
        _volumes = new FakeVolumes(_test.Temp.Combine("drives"));
        _test.Session.Volumes = _volumes;
        _media = new MediaService(_test.Time);
        new InventoryPropertiesService().SetAllowExternalMedia(_test.Session, true);
    }

    public void Dispose() => _test.Dispose();

    private ExternalMediaViewModel Create(string? folder = null, Accession.Core.Model.Media? moving = null) =>
        new(_test.Session, _media, new NoDialogs(), canScan: true, folder, moving);

    [Fact]
    public async Task Choosing_a_drive_suggests_its_label_and_says_how_it_is_recognised()
    {
        var usb = _volumes.Mount("E", "1A2B-3C4D", "CLIENT_USB01");
        var vm = Create();

        vm.Folder = usb;

        Assert.Equal("CLIENT_USB01", vm.MediaId);
        Assert.Contains("serial 1A2B-3C4D", vm.FolderInfo, StringComparison.Ordinal);
        Assert.Contains("if its drive letter changes", vm.FolderInfo, StringComparison.Ordinal);

        vm.MediaId = "SMITH_USB_1"; // the label is only a suggestion
        await vm.GoCommand.ExecuteAsync(null);

        Assert.Equal(("SMITH_USB_1", usb), (vm.Result!.MediaId, vm.Result.SourcePath));
        Assert.True(vm.StartScanning);
    }

    [Fact]
    public async Task Problems_are_shown_on_the_fields()
    {
        var vm = Create(_test.Temp.Combine("nowhere"));
        Assert.Equal("The folder does not exist or cannot be reached.", vm.FolderError);

        vm.Folder = _volumes.Mount("E", "1");
        vm.MediaId = "a|b";
        Assert.Contains("cannot contain", vm.MediaIdError, StringComparison.Ordinal);

        vm.MediaId = "OK";
        File.WriteAllText(Path.Combine(vm.Folder, "x.txt"), "x");
        _test.CreateFolders("OK"); // a root folder of that name
        await vm.GoCommand.ExecuteAsync(null);
        Assert.Contains("is in the root folder", vm.MediaIdError, StringComparison.Ordinal);
        Assert.Null(vm.Result);
    }

    [Fact]
    public async Task Moving_a_media_compares_its_top_level_and_keeps_its_id()
    {
        var old = _volumes.Mount("E", "1A2B-3C4D", "CLIENT_USB01");
        var added = _media.AddExternal(_test.Session, old, "USB01");
        using (var scope = _test.Session.Database.Open())
        {
            var top = ScanRows.AddFolder(scope, added.MediaKey, @"\USB01\");
            ScanRows.AddFolder(scope, added.MediaKey, @"\USB01\Mail\", top);
        }

        var copy = _test.Temp.Combine("share", "USB01");
        Directory.CreateDirectory(Path.Combine(copy, "Mail"));
        var vm = Create(moving: added);

        vm.Folder = copy;

        Assert.Equal("USB01", vm.MediaId);
        Assert.Equal("1 of the 1 folders and files at the top of USB01 are in this folder.", vm.MatchInfo);
        Assert.False(vm.MatchIsLow);
        await vm.GoCommand.ExecuteAsync(null);
        Assert.Equal(copy, vm.Result!.SourcePath);

        var html = await FormDialogTests.Render(DialogForms.Build(Create(_volumes.Mount("F", "77C0-1E2F", "CLIENT_USB02")), () => { })!, "form-external-media");
        Assert.Contains("Add media from another folder or drive", html);
        Assert.Contains("CLIENT_USB02", html);
        Assert.Contains("Start scanning it", html);
    }
}
