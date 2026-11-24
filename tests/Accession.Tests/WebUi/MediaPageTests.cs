using Accession.UI.App;
using Accession.UI.MediaScreen;
using Accession.UI.Shell;

namespace Accession.Tests.WebUi;

public sealed class MediaPageTests
{
    [Fact]
    public async Task Media_table_and_details_of_the_selected_media()
    {
        var html = await RenderInShell(new FakeMedia(), "media");

        Assert.Contains("5 media, 4 scanned.", html);
        Assert.Contains("123-124_001", html);
        Assert.Contains("Completed with errors", html);
        Assert.Contains(@"\\evidence01\intake\NW-2026-0142\123-123_002", html);
        Assert.Contains("Scan history", html);
        Assert.Contains("RetryFailed", html);
        Assert.Contains("Browse files", html);
        Assert.Contains("Delete media", html);
    }

    [Fact]
    public async Task Missing_media_explains_that_its_data_is_kept()
    {
        var media = new FakeMedia();
        media.SelectedRow = media.Rows.First(r => r.MediaId == "123-124_001");

        var html = await RenderInShell(media);

        Assert.Contains("The media folder was not found under the root", html);
        Assert.Contains("is-missing", html);
    }

    [Fact]
    public async Task Media_from_other_drives_show_their_location_and_whether_they_are_connected()
    {
        var media = new FakeMedia(external: true);
        media.SelectedRow = media.Rows.First(r => r.MediaId == "LAPTOP_JDOE");

        var html = await RenderInShell(media, "media-external");

        Assert.Contains(">Location<", html);
        Assert.Contains("Root folder", html);
        Assert.Contains(@"F:\Collections\JDoe", html);
        Assert.Contains("Not connected", html);
        Assert.Contains("COLLECT02", html);
        Assert.Contains("serial 77C0-1E2F (recognised under any drive letter)", html);
        Assert.Contains("Change location", html);
        Assert.Contains("Connect the drive (any drive letter)", html);
        Assert.DoesNotContain("was not found under the root", html);

        Assert.DoesNotContain(">Location<", await RenderInShell(new FakeMedia())); // not shown for inventories without them
    }

    [Fact]
    public async Task Running_scan_shows_live_progress()
    {
        var html = await RenderInShell(new FakeMedia(scanning: true), "media-scanning");

        Assert.Contains("Scan running", html);
        Assert.Contains("Hashing 41,210 of 96,020 files", html);
        Assert.Contains("width:42%", html);
    }

    [Fact]
    public async Task Read_only_inventory_hides_delete()
    {
        var html = await RenderInShell(new FakeMedia(readOnly: true));

        Assert.DoesNotContain("Delete media", html);
    }

    [Fact]
    public async Task No_media_offers_add_and_discover()
    {
        var html = await RenderInShell(new FakeMedia(empty: true));

        Assert.Contains("No media yet", html);
        Assert.Contains("Discover", html);
        Assert.Contains("Add media", html);
    }

    private static async Task<string> RenderInShell(IMediaModel media, string? previewName = null)
    {
        var shell = new FakeShell(new FakeDashboard(), media: media);
        shell.SelectedItem = shell.NavItems.First(n => n.Key == "Media");
        var html = await WebUiRenderer.RenderAsync<AppRoot>(new Dictionary<string, object?> { [nameof(AppRoot.Model)] = new FakeApp(shell) });
        if (previewName is not null)
        {
            WebUiRenderTests.WritePreview(previewName, html);
        }

        return html;
    }
}
