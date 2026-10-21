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
using WebClient = Jackett.Common.Utils.Clients.WebClient;

namespace Jackett.Common.Indexers.Definitions
{
    public class RedeTorrent : PublicBrazilianIndexerBase
    {
        public override string Id => "redetorrent";

        public override string Name => "RedeTorrent";

        public override string SiteLink { get; protected set; } = "https://redestorrents.com/";
        public override string[] LegacySiteLinks => new[]
        {
            "https://redetorrent.com/",
        };
        public RedeTorrent(IIndexerConfigurationService configService, WebClient wc, Logger l, IProtectionService ps, ICacheService cs)
            : base(configService, wc, l, ps, cs)
        {
        }

        public override IParseIndexerResponse GetParser() => new RedeTorrentParser(webclient);

        public override IIndexerRequestGenerator GetRequestGenerator() => new SimpleRequestGenerator(SiteLink, searchQueryParamsKey: "index.php?busca=");
    }

    public class RedeTorrentParser : PublicBrazilianParser
    {
        private readonly WebClient _webclient;
        protected string Tracker;

        public RedeTorrentParser(WebClient webclient)
        {
            _webclient = webclient;
            Tracker = "RedeTorrent";
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

        // The detail page shows the file info as "spec cards":
        //   <div class="spec-text"><small>Tamanho do Arquivo</small><strong>3.44 GB</strong></div>
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

            foreach (var spec in detailsDom.QuerySelectorAll(".spec-text"))
            {
                var key = spec.QuerySelector("small")?.TextContent?.Trim();
                var value = spec.QuerySelector("strong")?.TextContent?.Trim();
                if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(value))
                    continue;

                value = NormalizeValue(Regex.Replace(value, @"\s+", " "));
                fileInfo[key] = value;

                // "Tamanho do Arquivo" -> "Tamanho", the key that FileInfo.FromDictionary understands
                if (key.StartsWith("Tamanho", StringComparison.OrdinalIgnoreCase))
                    fileInfo["Tamanho"] = value;
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

            // Each card is <div class="col"><a class="cover-link" href="..." title="...">...</a></div>.
            // Other ".col" elements exist in the page (menu, footer) but have no cover-link, so require it.
            var rows = dom.QuerySelectorAll("div.col");

            foreach (var row in rows)
            {
                var detailAnchor = row.QuerySelector("a.cover-link[href^=\"https://\"]");
                if (detailAnchor == null)
                    continue;

                var detailUrl = new Uri(detailAnchor.GetAttribute("href") ?? string.Empty);

                var title = row.QuerySelector("article.custom-card")?.GetAttribute("data-title")?.Trim();
                if (string.IsNullOrEmpty(title))
                    title = detailAnchor.GetAttribute("title")?.Trim();
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

                // <time itemprop="dateCreated" datetime="2026-10-12T07:13:33-03:00">
                var createdAttr = detailsDom.QuerySelector("time[itemprop='dateCreated']")?.GetAttribute("datetime");
                if (!string.IsNullOrEmpty(createdAttr) &&
                    DateTimeOffset.TryParse(createdAttr, CultureInfo.InvariantCulture, DateTimeStyles.None, out var created))
                {
                    releaseCommonInfo.PublishDate = created.UtcDateTime;
                }
                else if (!string.IsNullOrEmpty(fileInfo.ReleaseYear) &&
                         DateTime.TryParseExact(fileInfo.ReleaseYear, "yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var releaseYear))
                {
                    releaseCommonInfo.PublishDate = releaseYear;
                }
                else
                {
                    releaseCommonInfo.PublishDate = DateTime.Today;
                }

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

                    // Each magnet sits in <div class="download-row"> next to
                    // <div class="download-name">DOWNLOAD TORRENT DUBLADO DUAL AUDIO 5.1 MKV WEB-DL 1080P</div>
                    var versionText = GetTitleElementOrNull(magnetLink)?.TextContent?.Trim();
                    if (!string.IsNullOrEmpty(versionText))
                    {
                        versionText = Regex.Replace(versionText, "DOWNLOAD TORRENT", "", RegexOptions.IgnoreCase)
                                           .Replace("DUAL ÁUDIO", "Dual")
                                           .Replace("DUAL AUDIO", "Dual")
                                           .Replace("ÁUDIO", "Audio")
                                           .Replace("AUDIO", "Audio")
                                           .Replace("DUBLADO", "Dubbed")
                                           .Replace("LEGENDADO", "Subbed")
                                           .Replace("MKV", "")
                                           .Replace("MP4", "")
                                           .Replace("MAGNET", "")
                                           .Replace("TORRENT", "")
                                           .Replace("LINK", "");
                        versionText = Regex.Replace(versionText, @"\s+", " ").Trim();

                        if (!string.IsNullOrEmpty(versionText))
                            release.Title = $"{release.Title} {versionText}".Trim();
                    }

                    // The download-name already carries the resolution. Only add the quality card when the
                    // title has none, and never a rating such as "10 / 10".
                    var quality = fileInfo.Quality ?? string.Empty;
                    if (!Regex.IsMatch(release.Title, @"\b(480|576|720|1080|2160)p\b|\b4K\b|UHD", RegexOptions.IgnoreCase) &&
                        !string.IsNullOrEmpty(quality) &&
                        !Regex.IsMatch(quality, @"^\d+\s*/\s*\d+$"))
                    {
                        release.Title = $"{release.Title} {quality}".Trim();
                    }

                    release.Category = magnetLink.ExtractCategory(release.Title);

                    // one size per magnet when the counts match, otherwise estimate from the resolution
                    release.Size = sizes.Count == magnetLinks.Length ? sizes[idx] : EstimateSize(release.Title);

                    release.Languages = fileInfo.Audio?.ToList() ?? release.Languages;
                    release.Genres = fileInfo.Genres?.ToList() ?? release.Genres;
                    release.Subs = string.IsNullOrEmpty(fileInfo.Subtitle) ? release.Subs : new[] { fileInfo.Subtitle };

                    if (release.Title.IsNotNullOrWhiteSpace())
                        releases.Add(release);
                }
            }

            return releases;
        }

        protected override INode GetTitleElementOrNull(IElement downloadButton)
        {
            // Walk up to the enclosing download-row and return its download-name element.
            var container = downloadButton.ParentElement;
            while (container != null)
            {
                var name = container.QuerySelector(".download-name");
                if (name != null)
                    return name;

                if (container.ClassList.Contains("download-list"))
                    break;

                container = container.ParentElement;
            }

            return null;
        }
    }
}
