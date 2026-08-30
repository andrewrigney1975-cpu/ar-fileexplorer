using System.Text;
using System.Text.Json;

namespace FileExplorer.Services;

/// The static HTML/CSS/JS served by <see cref="MediaWebServer"/>. Everything is inline / same-origin
/// - no CDN, matching the rest of the app's offline stance.
internal static class WebAssets
{
    private static string Kind(string path)
    {
        var ext = Path.GetExtension(path);
        if (IconHelper.IsPreviewableImage(ext)) return "image";
        if (IconHelper.IsPreviewableVideo(ext)) return "video";
        if (IconHelper.IsPreviewableAudio(ext)) return "audio";
        return "other";
    }

    private static string RelOf(string root, string full) =>
        full.Length <= root.Length ? "" : full[(root.Length + 1)..].Replace('\\', '/');

    private static string Enc(string s) => Uri.EscapeDataString(s);

    private static string Html(string s) => s
        .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    // ----- pages -----

    public static string BuildDirectoryPage(string root, string dir, string rel, string token)
    {
        var folderName = dir.Length <= root.Length ? Path.GetFileName(root) : Path.GetFileName(dir);
        var entries = new List<object>();

        try
        {
            foreach (var sub in Directory.EnumerateDirectories(dir).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
            {
                if (IsHidden(sub)) continue;
                entries.Add(new { name = Path.GetFileName(sub), kind = "dir", p = RelOf(root, sub) });
            }

            foreach (var file in Directory.EnumerateFiles(dir).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
            {
                if (IsHidden(file)) continue;
                long size = 0;
                try { size = new FileInfo(file).Length; } catch (IOException) { }
                entries.Add(new { name = Path.GetFileName(file), kind = Kind(file), p = RelOf(root, file), size });
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        var data = JsonSerializer.Serialize(new { token, rel, entries });
        var crumbs = Breadcrumbs(root, rel, token);

        return Shell(Html(folderName), $$"""
            <header>
              <nav class="crumbs">{{crumbs}}</nav>
              <a class="slideshow-link" href="/slideshow?p={{Enc(rel)}}&k={{token}}">▶ Show folder as slideshow</a>
            </header>
            <main id="grid"></main>
            <div id="lightbox" class="lightbox hidden">
              <button class="lb-close" data-act="close">✕</button>
              <button class="lb-nav lb-prev" data-act="prev">‹</button>
              <button class="lb-nav lb-next" data-act="next">›</button>
              <div class="lb-stage"></div>
              <div class="lb-caption"></div>
            </div>
            <script>window.__DATA = {{data}};</script>
            """);
    }

    public static string BuildSlideshowPage(string root, string dir, string rel, IReadOnlyList<string> images, string token)
    {
        var imgData = images.Select(i => new { name = Path.GetFileName(i), p = RelOf(root, i) }).ToList();
        var data = JsonSerializer.Serialize(new { token, rel, images = imgData });
        var folderName = dir.Length <= root.Length ? Path.GetFileName(root) : Path.GetFileName(dir);

        return Shell(Html(folderName) + " - slideshow", $$"""
            <div id="show" class="show">
              <a class="show-back" href="/dir?p={{Enc(rel)}}&k={{token}}">← Back to {{Html(folderName)}}</a>
              <div class="show-stage"><img id="show-img" alt=""></div>
              <div class="show-caption" id="show-caption"></div>
              <button class="lb-nav lb-prev" data-act="prev">‹</button>
              <button class="lb-nav lb-next" data-act="next">›</button>
              <div class="show-strip" id="show-strip"></div>
            </div>
            <script>window.__DATA = {{data}}; window.__MODE = "slideshow";</script>
            """);
    }

    private static string Breadcrumbs(string root, string rel, string token)
    {
        var sb = new StringBuilder();
        sb.Append($"<a href=\"/dir?p=&k={token}\">{Html(Path.GetFileName(root))}</a>");

        var acc = "";
        foreach (var part in rel.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            acc = acc.Length == 0 ? part : acc + "/" + part;
            sb.Append($" <span class=\"sep\">/</span> <a href=\"/dir?p={Enc(acc)}&k={token}\">{Html(part)}</a>");
        }

        return sb.ToString();
    }

    private static bool IsHidden(string path)
    {
        try
        {
            var attr = File.GetAttributes(path);
            return attr.HasFlag(FileAttributes.Hidden) || attr.HasFlag(FileAttributes.System);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static string Shell(string title, string body) => $$"""
        <!DOCTYPE html>
        <html lang="en">
        <head>
          <meta charset="utf-8">
          <meta name="viewport" content="width=device-width, initial-scale=1, viewport-fit=cover">
          <title>{{title}}</title>
          <link rel="stylesheet" href="/assets/app.css">
        </head>
        <body>
        {{body}}
        <script src="/assets/app.js"></script>
        </body>
        </html>
        """;

    // ----- static assets -----

    public const string Css = """
        :root { color-scheme: dark; }
        * { box-sizing: border-box; }
        body { margin: 0; font: 14px/1.4 -apple-system, "Segoe UI", system-ui, sans-serif;
               background: #16161a; color: #e6e6ea; }
        a { color: #7db3ff; text-decoration: none; }
        header { display: flex; flex-wrap: wrap; gap: 12px; align-items: center;
                 justify-content: space-between; padding: 14px 18px; position: sticky; top: 0;
                 background: #1d1d22; border-bottom: 1px solid #2c2c33; z-index: 5; }
        .crumbs { font-size: 15px; }
        .crumbs .sep { opacity: .4; margin: 0 2px; }
        .slideshow-link { padding: 7px 12px; background: #2b2b33; border-radius: 6px; white-space: nowrap; }
        #grid { display: grid; gap: 14px; padding: 18px;
                grid-template-columns: repeat(auto-fill, minmax(150px, 1fr)); }
        .tile { display: block; text-align: center; cursor: pointer; }
        .tile .frame { position: relative; width: 100%; aspect-ratio: 1; border-radius: 8px;
                       overflow: hidden; background: #24242b; display: flex; align-items: center;
                       justify-content: center; }
        .tile img { width: 100%; height: 100%; object-fit: cover; }
        .tile .glyph { font-size: 46px; opacity: .5; }
        .tile .badge { position: absolute; right: 6px; bottom: 6px; background: #000a; color: #fff;
                       font-size: 11px; padding: 1px 6px; border-radius: 4px; }
        .tile .name { margin-top: 6px; font-size: 12px; word-break: break-word;
                      display: -webkit-box; -webkit-line-clamp: 2; -webkit-box-orient: vertical;
                      overflow: hidden; }
        .tile .size { font-size: 11px; opacity: .5; }

        .lightbox, .show { position: fixed; inset: 0; background: #000e; display: flex;
                           align-items: center; justify-content: center; z-index: 20; }
        .lightbox.hidden { display: none; }
        .lb-stage, .show-stage { max-width: calc(100vw - 72px); max-height: calc(100vh - 72px);
                                 display: flex; align-items: center; justify-content: center; }
        .lb-stage img, .lb-stage video, .show-stage img {
            max-width: calc(100vw - 72px); max-height: calc(100vh - 72px); object-fit: contain; }
        .lb-stage audio { width: min(90vw, 480px); }
        .lb-caption, .show-caption { position: fixed; top: 14px; left: 50%; transform: translateX(-50%);
            background: #000a; padding: 5px 12px; border-radius: 5px; font-size: 13px; max-width: 80vw;
            white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
        .lb-close { position: fixed; top: 10px; right: 12px; }
        .lb-nav { position: fixed; top: 50%; transform: translateY(-50%); font-size: 22px;
                  width: 46px; height: 66px; }
        .lb-prev { left: 10px; } .lb-next { right: 10px; }
        button { background: #0009; color: #fff; border: 0; border-radius: 6px; padding: 8px 12px;
                 cursor: pointer; font-size: 15px; }
        button:hover { background: #000c; }

        .show { flex-direction: column; background: #0b0b0d; }
        .show-back { position: fixed; top: 12px; left: 14px; z-index: 3; background: #0009;
                     padding: 6px 10px; border-radius: 6px; }
        .show-stage { flex: 1; padding: 36px; }
        .show-strip { display: flex; gap: 6px; overflow-x: auto; padding: 8px; background: #000b;
                      width: 100%; scrollbar-width: thin; }
        .show-strip img { height: 68px; width: 68px; object-fit: cover; border-radius: 3px;
                          border: 2px solid transparent; cursor: pointer; flex: 0 0 auto; }
        .show-strip img.active { border-color: #4c9eff; }
        """;

    public const string Js = """
        const D = window.__DATA;
        const K = "&k=" + D.token;
        const fileUrl = p => "/file?p=" + encodeURIComponent(p) + K;
        const thumbUrl = p => "/thumb?p=" + encodeURIComponent(p) + K;
        const dirUrl = p => "/dir?p=" + encodeURIComponent(p) + K;

        function fmtSize(n) {
          if (!n) return "";
          const u = ["B", "KB", "MB", "GB", "TB"]; let i = 0;
          while (n >= 1024 && i < u.length - 1) { n /= 1024; i++; }
          return (i ? n.toFixed(1) : n) + " " + u[i];
        }

        if (window.__MODE === "slideshow") initSlideshow(); else initGrid();

        // ---------- directory grid + lightbox ----------
        function initGrid() {
          const grid = document.getElementById("grid");
          const previewable = D.entries.filter(e => e.kind === "image" || e.kind === "video" || e.kind === "audio");

          D.entries.forEach(e => {
            const tile = document.createElement(e.kind === "dir" ? "a" : "div");
            tile.className = "tile";
            if (e.kind === "dir") tile.href = dirUrl(e.p);

            const frame = document.createElement("div");
            frame.className = "frame";
            if (e.kind === "dir" || e.kind === "image" || e.kind === "video") {
              const img = document.createElement("img");
              img.loading = "lazy";
              img.src = thumbUrl(e.p);
              img.onerror = () => { img.remove(); frame.insertAdjacentHTML("afterbegin",
                '<span class="glyph">' + (e.kind === "dir" ? "📁" : "📄") + '</span>'); };
              frame.appendChild(img);
            } else {
              frame.innerHTML = '<span class="glyph">' +
                (e.kind === "audio" ? "🎵" : "📄") + '</span>';
            }
            if (e.kind === "video") frame.insertAdjacentHTML("beforeend", '<span class="badge">video</span>');
            if (e.kind === "audio") frame.insertAdjacentHTML("beforeend", '<span class="badge">audio</span>');
            tile.appendChild(frame);

            tile.insertAdjacentHTML("beforeend",
              '<div class="name">' + escapeHtml(e.name) + '</div>' +
              (e.size ? '<div class="size">' + fmtSize(e.size) + '</div>' : ''));

            if (e.kind !== "dir") {
              tile.onclick = () => {
                if (e.kind === "other") { location.href = fileUrl(e.p); return; }
                openLightbox(previewable.indexOf(e));
              };
            }
            grid.appendChild(tile);
          });

          const lb = document.getElementById("lightbox");
          const stage = lb.querySelector(".lb-stage");
          const caption = lb.querySelector(".lb-caption");
          let idx = 0;

          window.openLightbox = i => { idx = i; renderLb(); lb.classList.remove("hidden"); };
          function closeLb() { stage.innerHTML = ""; lb.classList.add("hidden"); }
          function step(d) { idx = (idx + d + previewable.length) % previewable.length; renderLb(); }
          function renderLb() {
            const e = previewable[idx];
            caption.textContent = e.name + "  (" + (idx + 1) + " / " + previewable.length + ")";
            let el;
            if (e.kind === "image") { el = new Image(); el.src = fileUrl(e.p); }
            else if (e.kind === "video") { el = document.createElement("video"); el.src = fileUrl(e.p); el.controls = true; el.autoplay = true; }
            else { el = document.createElement("audio"); el.src = fileUrl(e.p); el.controls = true; el.autoplay = true; }
            stage.replaceChildren(el);
          }

          lb.addEventListener("click", ev => {
            const act = ev.target.dataset.act;
            if (act === "close" || ev.target === lb) closeLb();
            else if (act === "prev") step(-1);
            else if (act === "next") step(1);
          });
          document.addEventListener("keydown", ev => {
            if (lb.classList.contains("hidden")) return;
            if (ev.key === "Escape") closeLb();
            else if (ev.key === "ArrowLeft" || ev.key === "ArrowUp") step(-1);
            else if (ev.key === "ArrowRight" || ev.key === "ArrowDown" || ev.key === " ") { ev.preventDefault(); step(1); }
          });
        }

        // ---------- slideshow ----------
        function initSlideshow() {
          const imgs = D.images;
          if (!imgs.length) return;
          const main = document.getElementById("show-img");
          const cap = document.getElementById("show-caption");
          const strip = document.getElementById("show-strip");
          let idx = 0;

          imgs.forEach((im, i) => {
            const t = new Image();
            t.src = thumbUrl(im.p);
            t.onclick = () => show(i);
            strip.appendChild(t);
          });

          function show(i) {
            idx = (i + imgs.length) % imgs.length;
            main.src = fileUrl(imgs[idx].p);
            cap.textContent = imgs[idx].name + "  (" + (idx + 1) + " / " + imgs.length + ")";
            [...strip.children].forEach((c, j) => c.classList.toggle("active", j === idx));
            const active = strip.children[idx];
            if (active) strip.scrollTo({ left: active.offsetLeft - strip.clientWidth / 2 + 34, behavior: "smooth" });
          }

          document.querySelector(".lb-prev").onclick = () => show(idx - 1);
          document.querySelector(".lb-next").onclick = () => show(idx + 1);
          document.addEventListener("keydown", ev => {
            if (ev.key === "ArrowLeft" || ev.key === "ArrowUp" || ev.key === "PageUp") show(idx - 1);
            else if (ev.key === "ArrowRight" || ev.key === "ArrowDown" || ev.key === "PageDown" || ev.key === " ") { ev.preventDefault(); show(idx + 1); }
            else if (ev.key === "Home") show(0);
            else if (ev.key === "End") show(imgs.length - 1);
            else if (ev.key === "Escape") location.href = dirUrl(D.rel);
          });

          show(0);
        }

        function escapeHtml(s) {
          const d = document.createElement("div"); d.textContent = s; return d.innerHTML;
        }
        """;
}
