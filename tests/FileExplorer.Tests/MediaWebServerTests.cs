using System.Net;
using System.Text;
using FileExplorer.Services;

namespace FileExplorer.Tests;

public sealed class MediaWebServerTests : IDisposable
{
    private readonly string _root;
    private readonly MediaWebServer _server;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };

    public MediaWebServerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mws-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "sub"));
        File.WriteAllText(Path.Combine(_root, "hello.txt"), "hello world");
        File.WriteAllBytes(Path.Combine(_root, "a.jpg"), new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
        File.WriteAllText(Path.Combine(_root, "sub", "nested.txt"), "nested");

        _server = new MediaWebServer { ThumbnailProvider = (_, _, _) => Task.FromResult<byte[]?>(null) };
        _server.Start(_root);
    }

    private string Base => $"http://localhost:{_server.Port}";

    [Fact]
    public async Task Root_redirects_to_directory_listing()
    {
        var res = await _http.GetAsync($"{Base}/?k={_server.Token}");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadAsStringAsync();
        Assert.Contains("hello.txt", body);
        Assert.Contains("a.jpg", body);
        Assert.Contains("sub", body);
    }

    [Fact]
    public async Task Missing_or_wrong_token_is_forbidden()
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await _http.GetAsync($"{Base}/dir?p=")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _http.GetAsync($"{Base}/dir?p=&k=nope")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _http.GetAsync($"{Base}/file?p=hello.txt&k=nope")).StatusCode);
    }

    [Fact]
    public async Task Static_assets_are_served_without_a_token()
    {
        var css = await _http.GetAsync($"{Base}/assets/app.css");
        var js = await _http.GetAsync($"{Base}/assets/app.js");
        Assert.Equal(HttpStatusCode.OK, css.StatusCode);
        Assert.Equal(HttpStatusCode.OK, js.StatusCode);
        Assert.Contains("window.__DATA", await js.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task File_route_serves_content()
    {
        var res = await _http.GetAsync($"{Base}/file?p=hello.txt&k={_server.Token}");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("hello world", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task File_route_honours_range_requests()
    {
        var req = new HttpRequestMessage(HttpMethod.Get, $"{Base}/file?p=hello.txt&k={_server.Token}");
        req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(6, 10);
        var res = await _http.SendAsync(req);

        Assert.Equal(HttpStatusCode.PartialContent, res.StatusCode);
        Assert.Equal("world", await res.Content.ReadAsStringAsync());
        Assert.Equal("bytes 6-10/11", res.Content.Headers.ContentRange!.ToString());
    }

    [Fact]
    public async Task Path_traversal_is_rejected()
    {
        var escape = Uri.EscapeDataString("../../windows/win.ini");
        var res = await _http.GetAsync($"{Base}/file?p={escape}&k={_server.Token}");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task Subdirectory_listing_works()
    {
        var res = await _http.GetAsync($"{Base}/dir?p=sub&k={_server.Token}");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("nested.txt", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Slideshow_page_lists_only_images()
    {
        var res = await _http.GetAsync($"{Base}/slideshow?p=&k={_server.Token}");
        var body = await res.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("a.jpg", body);
        Assert.DoesNotContain("hello.txt", body);
    }

    [Fact]
    public async Task Post_is_rejected()
    {
        var res = await _http.PostAsync($"{Base}/file?p=hello.txt&k={_server.Token}", new StringContent(""));
        Assert.Equal(HttpStatusCode.MethodNotAllowed, res.StatusCode);
    }

    [Fact]
    public async Task Directory_page_includes_the_masonry_grid_and_filter_bar()
    {
        var res = await _http.GetAsync($"{Base}/dir?p=&k={_server.Token}");
        var body = await res.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        // Filter bar markup.
        Assert.Contains("id=\"f-name\"", body);
        Assert.Contains("id=\"f-size-min\"", body);
        Assert.Contains("id=\"f-size-max\"", body);
        Assert.Contains("id=\"f-kinds\"", body);
        Assert.Contains("id=\"f-count\"", body);
    }

    [Fact]
    public async Task App_js_filters_by_name_size_and_kind_client_side()
    {
        var js = await _http.GetAsync($"{Base}/assets/app.js");
        var body = await js.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, js.StatusCode);

        // The filtering logic itself (name substring, size range in MB, kind checkboxes) and the
        // masonry-aware lightbox wiring (openLightbox now takes the currently-filtered list) all
        // live in this asset - a regression here would silently ship a folder page whose filters
        // do nothing, so pin the load-bearing pieces by name.
        Assert.Contains("function initFilters(", body);
        Assert.Contains("activeKinds.has(e.kind)", body);
        Assert.Contains("nameQ && !e.name.toLowerCase().includes(nameQ)", body);
        Assert.Contains("window.openLightbox = (list, i)", body);
    }

    [Fact]
    public async Task App_css_uses_column_based_masonry_for_the_grid()
    {
        var css = await _http.GetAsync($"{Base}/assets/app.css");
        var body = await css.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, css.StatusCode);
        Assert.Contains("column-width", body);
        Assert.Contains("break-inside: avoid", body);
    }

    public void Dispose()
    {
        _server.Stop();
        _http.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }
}
