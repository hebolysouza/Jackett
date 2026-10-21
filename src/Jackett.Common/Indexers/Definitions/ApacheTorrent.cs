using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
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

        // A post can list several sizes in one field (one per version). ParseUtil.GetBytes would glue
        // all the digits together and return an absurd number, so read each size separately.
        private static List<long> ParseSizes(string text)
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

        // A single slow or broken detail page must not kill the whole search.
        // Wait at most DetailTimeout per page and skip that row if it fails or times out.
        private static readonly TimeSpan DetailTimeout = TimeSpan.FromSeconds(15);

        private string GetDetailPageOrNull(Uri detailUrl)
        {
            try
            {
                var task = _webclient.GetResultAsync(new WebRequest(detailUrl.ToString()));
                if (!task.Wait(DetailTimeout))
                    return null;

                return task.Result?.ContentString;
            }
            catch (Exception)
            {
                return null;
            }
        }

        public override IList<ReleaseInfo> ParseResponse(IndexerResponse indexerResponse)
        {
            var releases = new List<ReleaseInfo>();

            var parser = new HtmlParser();
            var dom = parser.ParseDocument(indexerResponse.Content);
            var rows = dom.QuerySelectorAll("div.capa-item");

            foreach (var row in rows)
            {
                var detailAnchor = row.QuerySelector("h2.capa-titulo a") ?? row.QuerySelector("a[href^=\"https://\"]");
                if (detailAnchor == null)
                    continue;

                var detailUrlStr = detailAnchor.GetAttribute("href")?.Trim();
                if (string.IsNullOrEmpty(detailUrlStr))
                    continue;

                var detailUrl = new Uri(detailUrlStr);
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

                var detailsHtml = GetDetailPageOrNull(detailUrl);
                if (string.IsNullOrEmpty(detailsHtml))
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
                var sizes = ParseSizes(fileInfo.Size);
                for (var idx = 0; idx < magnetLinks.Length; idx++)
                {
                    var magnetLink = magnetLinks[idx];
                    var magnet = magnetLink.GetAttribute("href");
                    if (string.IsNullOrEmpty(magnet))
                        continue;

                    var release = releaseCommonInfo.Clone() as ReleaseInfo;
                    release.Guid = release.MagnetUri = new Uri(magnet);
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
}
