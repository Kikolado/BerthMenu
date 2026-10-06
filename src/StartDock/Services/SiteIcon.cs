using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace StartDock.Services
{
    /// <summary>
    /// The icon for a website tile (Add → Add a website…), fetched from the site
    /// itself — never through a third-party icon service, so only that site learns
    /// you pinned it.
    ///
    /// Tries, best first: the icons the page lists in its &lt;link rel="icon"&gt; /
    /// "apple-touch-icon" tags (largest first; SVG skipped, WPF can't draw it), then
    /// /apple-touch-icon.png (usually 180px), then /favicon.ico. Until one arrives —
    /// or if none does — the tile shows a globe.
    /// </summary>
    public static class SiteIcon
    {
        private static readonly HttpClient Http = CreateClient();
        private const int MaxBytes = 2 * 1024 * 1024;

        /// <summary>Downloads the best icon into a temporary file (.png, .ico, .jpg…
        /// by its actual content) and returns its path, or null. The caller converts
        /// and deletes it.</summary>
        public static async Task<string?> DownloadAsync(string siteUrl)
        {
            if (!Uri.TryCreate(siteUrl, UriKind.Absolute, out var site))
                return null;

            var candidates = new List<Uri>();
            try
            {
                string html = await GetPageStartAsync(site);
                candidates.AddRange(IconLinks(html, site));
            }
            catch
            {
                // Page unreachable — the well-known locations below may still work.
            }
            var root = new Uri(site.GetLeftPart(UriPartial.Authority) + "/");
            candidates.Add(new Uri(root, "apple-touch-icon.png"));
            candidates.Add(new Uri(root, "favicon.ico"));

            foreach (var candidate in candidates.Distinct())
            {
                try
                {
                    using var response = await Http.GetAsync(candidate, HttpCompletionOption.ResponseHeadersRead);
                    if (!response.IsSuccessStatusCode)
                        continue;
                    if (response.Content.Headers.ContentLength is long length && length > MaxBytes)
                        continue;
                    byte[] bytes = await response.Content.ReadAsByteArrayAsync();
                    if (bytes.Length < 16 || bytes.Length > MaxBytes)
                        continue;
                    string? ext = ExtensionFor(bytes);
                    if (ext == null)
                        continue; // HTML error page, SVG, …
                    string path = Path.Combine(Path.GetTempPath(), $"startdock-site-{Guid.NewGuid():N}{ext}");
                    await File.WriteAllBytesAsync(path, bytes);
                    return path;
                }
                catch
                {
                    // Try the next one.
                }
            }
            return null;
        }

        /// <summary>The first 256 KB of the page — the &lt;head&gt; with its icon links
        /// is always near the top.</summary>
        private static async Task<string> GetPageStartAsync(Uri site)
        {
            using var response = await Http.GetAsync(site, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync();
            var buffer = new char[256 * 1024];
            using var reader = new StreamReader(stream);
            int read = await reader.ReadBlockAsync(buffer, 0, buffer.Length);
            return new string(buffer, 0, read);
        }

        private static readonly Regex LinkTag = new(@"<link\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex Attribute = new(@"([a-zA-Z\-]+)\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s>]+))", RegexOptions.Compiled);

        private static IEnumerable<Uri> IconLinks(string html, Uri baseUri)
        {
            var found = new List<(Uri Uri, int Size)>();
            foreach (Match tag in LinkTag.Matches(html))
            {
                var attrs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (Match a in Attribute.Matches(tag.Value))
                    attrs[a.Groups[1].Value] = a.Groups[2].Success ? a.Groups[2].Value : a.Groups[3].Success ? a.Groups[3].Value : a.Groups[4].Value;

                if (!attrs.TryGetValue("rel", out string? rel) || !attrs.TryGetValue("href", out string? href))
                    continue;
                rel = rel.ToLowerInvariant();
                bool touch = rel.Contains("apple-touch-icon");
                if (!touch && !rel.Split(' ').Contains("icon"))
                    continue;
                if (href.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) || href.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                    || (attrs.TryGetValue("type", out string? type) && type.Contains("svg", StringComparison.OrdinalIgnoreCase)))
                    continue;
                if (!Uri.TryCreate(baseUri, System.Net.WebUtility.HtmlDecode(href), out var uri) || (uri.Scheme != "https" && uri.Scheme != "http"))
                    continue;

                int size = touch ? 180 : 32;
                if (attrs.TryGetValue("sizes", out string? sizes))
                {
                    var m = Regex.Match(sizes, @"(\d+)\s*[xX]");
                    if (m.Success && int.TryParse(m.Groups[1].Value, out int s))
                        size = s;
                }
                found.Add((uri, size));
            }
            // Largest first, but nothing huge (a 1024px image is a lot to download
            // for a tile) ahead of a reasonable one.
            return found.OrderBy(f => f.Size > 512 ? 1 : 0).ThenByDescending(f => f.Size).Select(f => f.Uri);
        }

        private static string? ExtensionFor(byte[] b)
        {
            if (b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) return ".png";
            if (b[0] == 0x00 && b[1] == 0x00 && b[2] == 0x01 && b[3] == 0x00) return ".ico";
            if (b[0] == 0xFF && b[1] == 0xD8) return ".jpg";
            if (b[0] == 0x47 && b[1] == 0x49 && b[2] == 0x46) return ".gif";
            if (b[0] == 0x42 && b[1] == 0x4D) return ".bmp";
            return null;
        }

        private static HttpClient CreateClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) StartDock");
            return client;
        }
    }
}
