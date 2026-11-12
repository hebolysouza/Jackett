using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Jackett.Common.Extensions;
using Jackett.Common.Indexers.Definitions.Abstract;
using Jackett.Common.Models;
using Jackett.Common.Services.Interfaces;
using Jackett.Common.Utils;
using Jackett.Common.Utils.Clients;
using NLog;

namespace Jackett.Common.Indexers.Definitions
{
    public class ApacheTorrent : PublicBrazilianIndexerBase
    {
        public override string Id => "apachetorrent";

        public override string Name => "ApacheTorrent";

        public override string SiteLink { get; protected set; } = "https://apachetorrents.com/";
        public override string[] LegacySiteLinks => new[]
        {
            "https://apachetorrent.com/",
        };

        public ApacheTorrent(IIndexerConfigurationService configService, WebClient wc, Logger l, IProtectionService ps,
                             ICacheService cs) : base(configService, wc, l, ps, cs)
        {
        }

        public override IParseIndexerResponse GetParser() =>
            new ApacheTorrentParser(webclient);

        public override IIndexerRequestGenerator GetRequestGenerator() => new SimpleRequestGenerator(SiteLink, searchQueryParamsKey: "index.php?busca=");
    }

    public class ApacheTorrentParser : PublicBrazilianParser
    {
        private readonly WebClient _webclient;
        public string Tracker { get; }

        public ApacheTorrentParser(WebClient webclient)
        {
            _webclient = webclient;
            Tracker = "ApacheTorrent";
        }

        private static string StripHtml(string html)
        {
            if (string.IsNullOrEmpty(html))
                return string.Empty;

            var text = html;
            if (html.Contains("<") || html.Contains("&"))
            {
                var doc = new HtmlParser().ParseDocument(html);
                text = doc.Body?.TextContent ?? string.Empty;
            }

            return Regex.Replace(text, @"\s+", " ").Trim();
        }

        private static string NormalizeValue(string value)
        {
            return value switch
            {
                var v when v.Contains("Dual Áudio") => v.Replace("Dual Áudio", "Dual"),
                var v when v.Contains("Dual Audio") => v.Replace("Dual Audio", "Dual"),
                var v when v.Contains("Full HD") => v.Replace("Full HD", "1080p"),
                var v when v.Contains("4K") => v.Replace("4K", "2160p"),
                var v when v.Contains("SD") => v.Replace("SD", "480p"),
                // do not touch values that already say WEB-DL (otherwise it becomes WEB-DL-DL)
                var v when Regex.IsMatch(v, @"\bWEB\b(?!-)") => Regex.Replace(v, @"\bWEB\b(?!-)", "WEB-DL"),
                _ => value
            };
        }

        // The base estimate only recognizes lowercase "1080p" and knows nothing about "4K" / "ULTRA HD",
        // so uppercase titles such as "1080P" or "ULTRA HD 4K" all fell back to 512 MB.
        private long EstimateSize(string title)
        {
            var t = (title ?? string.Empty).ToLowerInvariant();
            if (t.Contains("ultra hd") || Regex.IsMatch(t, @"\b4k\b"))
                t += " 2160p";

            return ExtractSizeByResolution(t);
        }

        private Dictionary<string, string> ExtractFileInfo(IDocument detailsDom)
        {
            var fileInfo = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            // The info block is a <p> with lines like "<strong>Tamanho</strong>: 2.81 GB<br>".
            // Keep the legacy #informacoes container as first choice, then look for the <p>
            // that holds the labels, since the current layout does not expose that id.
            var infoSection = detailsDom.QuerySelector("#informacoes p")
                              ?? detailsDom.QuerySelectorAll("p")
                                  .FirstOrDefault(p => p.QuerySelector("strong") != null && p.TextContent.Contains("Tamanho"));
            if (infoSection == null)
                return fileInfo;

            var lines = infoSection.InnerHtml.Split(new[] { "<br>" }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                if (!line.Contains("<strong>") || !line.Contains(":"))
                    continue;

                var parts = line.Split(new[] { ':' }, 2);
                if (parts.Length != 2)
                    continue;

                var key = StripHtml(parts[0]);
                var value = NormalizeValue(StripHtml(parts[1]));

                if (!string.IsNullOrEmpty(key) && !string.IsNullOrEmpty(value))
                {
                    fileInfo[key] = value;
                }
            }

            return fileInfo;
        }

        public override IList<ReleaseInfo> ParseResponse(IndexerResponse indexerResponse)
        {
            var releases = new List<ReleaseInfo>();

            var parser = new HtmlParser();
            var dom = parser.ParseDocument(indexerResponse.Content);
            var rows = dom.QuerySelectorAll("div.capa-item");

            // First collect every card, then download all detail pages together (see BrazilianDetailPages).
            var cards = new List<ReleaseInfo>();
            foreach (var row in rows)
            {
                var detailAnchor = row.QuerySelector("h2.capa-titulo a") ?? row.QuerySelector("a[href^=\"https://\"]");
                if (detailAnchor == null)
                    continue;

                var detailUrlStr = detailAnchor.GetAttribute("href")?.Trim();
                if (string.IsNullOrEmpty(detailUrlStr))
                    continue;

                var detailUrl = BrazilianDetailPages.TryAbsoluteUri(detailUrlStr);
                if (detailUrl == null)
                    continue;

                var title = detailAnchor.GetAttribute("title")?.Trim();
                if (string.IsNullOrEmpty(title))
                    title = detailAnchor.TextContent.Trim();

                var releaseCommonInfo = new ReleaseInfo
                {
                    Title = CleanTitle(title),
                    Details = detailUrl,
                    Guid = detailUrl,
                    Seeders = 1
                };
                cards.Add(releaseCommonInfo);
            }

            var detailPages = BrazilianDetailPages.Fetch(_webclient, cards.Select(c => c.Details).ToList());

            foreach (var releaseCommonInfo in cards)
            {
                if (!detailPages.TryGetValue(releaseCommonInfo.Details.AbsoluteUri, out var detailsHtml))
                    continue;

                var detailsDom = parser.ParseDocument(detailsHtml);

                var fileInfoDict = ExtractFileInfo(detailsDom);
                var fileInfo = PublicBrazilianIndexerBase.FileInfo.FromDictionary(fileInfoDict);

                releaseCommonInfo.PublishDate =
                    !string.IsNullOrEmpty(fileInfo.ReleaseYear) &&
                    DateTime.TryParseExact(fileInfo.ReleaseYear, "yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var releaseYear)
                        ? releaseYear
                        : DateTime.Today;

                var magnetLinks = detailsDom.QuerySelectorAll("a[href^='magnet:?']").ToArray();
                var sizes = BrazilianDetailPages.ParseSizes(fileInfo.Size);
                for (var idx = 0; idx < magnetLinks.Length; idx++)
                {
                    var magnetLink = magnetLinks[idx];
                    var magnet = magnetLink.GetAttribute("href");
                    if (string.IsNullOrEmpty(magnet))
                        continue;

                    var magnetUri = BrazilianDetailPages.TryAbsoluteUri(magnet);
                    if (magnetUri == null)
                        continue;

                    var release = releaseCommonInfo.Clone() as ReleaseInfo;
                    release.Guid = release.MagnetUri = magnetUri;
                    release.DownloadVolumeFactor = 0;
                    release.UploadVolumeFactor = 1;

                    // Extract resolution from file info
                    var resolution = fileInfo.Quality ?? fileInfo.VideoQuality ?? string.Empty;

                    // Format the title
                    release.Title = $"{release.Title} {resolution}".Trim();
                    release.Title = ExtractTitleOrDefault(magnetLink, release.Title);
                    release.Category = magnetLink.ExtractCategory(release.Title);

                    // Additional metadata
                    release.Languages = fileInfo.Audio?.ToList() ?? release.Languages;
                    release.Genres = fileInfo.Genres?.ToList() ?? release.Genres;
                    release.Subs = string.IsNullOrEmpty(fileInfo.Subtitle) ? release.Subs : new[] { fileInfo.Subtitle };
                    // one size per magnet when the counts match, otherwise estimate from the resolution
                    release.Size = sizes.Count == magnetLinks.Length ? sizes[idx] : EstimateSize(release.Title);

                    if (!string.IsNullOrWhiteSpace(release.Title))
                        releases.Add(release);
                }
            }

            return releases;
        }

        protected override INode GetTitleElementOrNull(IElement downloadButton)
        {
            // The description sits in a text node before the magnet button. Skip
            // whitespace-only text nodes (newlines/indentation between <br> and <a>).
            var description = downloadButton.PreviousSibling;
            while (description != null &&
                   (description.NodeType != NodeType.Text || string.IsNullOrWhiteSpace(description.TextContent)))
            {
                description = description.PreviousSibling;
            }

            return description;
        }
    }

    // Shared by ApacheTorrent, RedeTorrent and HDRTorrent (kept in this file so the fork stays three files).
    public static class BrazilianDetailPages
    {
        // Each detail page may take at most PageTimeout; all detail pages of one search together at most
        // TotalBudget, so the whole search answers before the 100 s limit of Jackett, Prowlarr, Radarr and Sonarr.
        // Pages that miss either limit are skipped (only that result is lost, not the whole search).
        public static readonly TimeSpan PageTimeout = TimeSpan.FromSeconds(15);
        public static readonly TimeSpan TotalBudget = TimeSpan.FromSeconds(70);

        // Up to MaxParallel pages in flight, and new requests start at least MinSpacing apart: a fast site sees
        // the same request rate as before (the base indexer uses a 0.5 s delay), a slow site no longer adds up
        // every page one after the other.
        public const int MaxParallel = 3;
        public static readonly TimeSpan MinSpacing = TimeSpan.FromMilliseconds(500);

        public static Uri TryAbsoluteUri(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;

            // only web pages and magnets: on Linux a bare "/path" would otherwise become a file:// URI
            if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri))
                return null;

            return uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == "magnet" ? uri : null;
        }

        // Returns the HTML of each page that answered in time, keyed by Uri.AbsoluteUri.
        public static IDictionary<string, string> Fetch(WebClient webclient, IList<Uri> urls) =>
            Fetch(webclient, urls, PageTimeout, TotalBudget, MaxParallel, MinSpacing);

        // Same as above with explicit limits (used by the tests).
        public static IDictionary<string, string> Fetch(WebClient webclient, IList<Uri> urls, TimeSpan pageTimeout,
                                                        TimeSpan totalBudget, int maxParallel, TimeSpan minSpacing)
        {
            var results = new ConcurrentDictionary<string, string>();
            var distinct = urls.Where(u => u != null).Select(u => u.AbsoluteUri).Distinct().ToList();
            if (distinct.Count == 0)
                return results;

            var cts = new CancellationTokenSource(totalBudget);
            var gate = new SemaphoreSlim(maxParallel);
            var spacingLock = new SemaphoreSlim(1, 1);
            var nextStart = DateTime.UtcNow;

            async Task FetchOne(string url)
            {
                try
                {
                    await gate.WaitAsync(cts.Token).ConfigureAwait(false);
                    try
                    {
                        await spacingLock.WaitAsync(cts.Token).ConfigureAwait(false);
                        try
                        {
                            var wait = nextStart - DateTime.UtcNow;
                            if (wait > TimeSpan.Zero)
                                await Task.Delay(wait, cts.Token).ConfigureAwait(false);
                            nextStart = DateTime.UtcNow + minSpacing;
                        }
                        finally
                        {
                            spacingLock.Release();
                        }

                        var request = webclient.GetResultAsync(new WebRequest(url));
                        var finished = await Task.WhenAny(request, Task.Delay(pageTimeout, cts.Token)).ConfigureAwait(false);
                        if (finished == request && request.Status == TaskStatus.RanToCompletion)
                        {
                            var html = request.Result?.ContentString;
                            if (!string.IsNullOrEmpty(html))
                                results[url] = html;
                        }
                        else
                        {
                            // abandoned request: observe a late failure so it is not reported as unobserved
                            _ = request.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                        }
                    }
                    finally
                    {
                        gate.Release();
                    }
                }
                catch (Exception)
                {
                    // cancelled by the total budget or a failed page: skip only this page
                }
            }

            var tasks = distinct.Select(FetchOne).ToArray();
            try
            {
                Task.WaitAll(tasks, totalBudget + TimeSpan.FromSeconds(2));
            }
            catch (Exception)
            {
                // FetchOne swallows its own errors; nothing to do here
            }

            cts.Cancel();
            return new Dictionary<string, string>(results);
        }

        // A post can list several sizes in one field (one per version). ParseUtil.GetBytes would glue
        // all the digits together and return an absurd number, so read each size separately.
        public static List<long> ParseSizes(string text)
        {
            var sizes = new List<long>();
            if (string.IsNullOrWhiteSpace(text))
                return sizes;

            foreach (Match m in Regex.Matches(text, @"(\d+(?:[.,]\d+)?)\s*(TB|GB|MB|KB)", RegexOptions.IgnoreCase))
            {
                var bytes = RowParsingExtensions.GetBytes(m.Value);
                if (bytes > 0 && bytes < 2L * 1024 * 1024 * 1024 * 1024)
                    sizes.Add(bytes);
            }

            return sizes;
        }
    }
}
