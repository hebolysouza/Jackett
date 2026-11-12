using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jackett.Common.Indexers;
using Jackett.Common.Indexers.Definitions;
using Jackett.Common.Models.Config;
using Jackett.Common.Utils.Clients;
using NUnit.Framework;
using Assert = NUnit.Framework.Assert;
using WebClient = Jackett.Common.Utils.Clients.WebClient;
using WebRequest = Jackett.Common.Utils.Clients.WebRequest;

namespace Jackett.Test.Common.Indexers
{
    [TestFixture]
    public class BrazilianDetailPagesTests
    {
        // Fake client: each URL answers with fixed HTML after a delay, or throws. Tracks how many requests run at once.
        private class DelayedWebClient : WebClient
        {
            private readonly Dictionary<string, (TimeSpan delay, string html)> _pages = new Dictionary<string, (TimeSpan, string)>();
            private int _inFlight;
            public int MaxInFlight;
            public readonly List<string> Requested = new List<string>();

            public DelayedWebClient() : base(null, null, null, new ServerConfig(null))
            {
            }

            public void Add(string url, TimeSpan delay, string html) => _pages[url] = (delay, html);

            public override async Task<WebResult> GetResultAsync(WebRequest request)
            {
                lock (Requested)
                    Requested.Add(request.Url);
                var now = Interlocked.Increment(ref _inFlight);
                lock (Requested)
                    MaxInFlight = Math.Max(MaxInFlight, now);
                try
                {
                    if (!_pages.TryGetValue(request.Url, out var page))
                        throw new Exception("unknown url " + request.Url);
                    await Task.Delay(page.delay);
                    if (page.html == null)
                        throw new Exception("site error");
                    return new WebResult { ContentBytes = Encoding.UTF8.GetBytes(page.html), Status = HttpStatusCode.OK, Request = request };
                }
                finally
                {
                    Interlocked.Decrement(ref _inFlight);
                }
            }
        }

        private static List<Uri> Urls(params string[] urls) => urls.Select(u => new Uri(u)).ToList();

        [Test]
        public void Fetch_RunsPagesInParallelWithLimit()
        {
            var wc = new DelayedWebClient();
            var urls = Enumerable.Range(1, 6).Select(i => "https://site.test/p" + i).ToArray();
            foreach (var u in urls)
                wc.Add(u, TimeSpan.FromMilliseconds(600), "<html>" + u + "</html>");

            var sw = Stopwatch.StartNew();
            var result = BrazilianDetailPages.Fetch(wc, Urls(urls), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(20), 3, TimeSpan.FromMilliseconds(50));
            sw.Stop();

            Assert.AreEqual(6, result.Count);
            Assert.AreEqual("<html>https://site.test/p4</html>", result["https://site.test/p4"]);
            Assert.LessOrEqual(wc.MaxInFlight, 3);
            Assert.GreaterOrEqual(wc.MaxInFlight, 2);
            // one after the other would take 3.6 s
            Assert.Less(sw.Elapsed.TotalSeconds, 2.5);
        }

        [Test]
        public void Fetch_SkipsFailedAndSlowPages()
        {
            var wc = new DelayedWebClient();
            wc.Add("https://site.test/ok", TimeSpan.FromMilliseconds(50), "ok");
            wc.Add("https://site.test/error", TimeSpan.FromMilliseconds(50), null);
            wc.Add("https://site.test/slow", TimeSpan.FromSeconds(10), "slow");

            var sw = Stopwatch.StartNew();
            var result = BrazilianDetailPages.Fetch(wc, Urls("https://site.test/ok", "https://site.test/error", "https://site.test/slow"),
                                                    TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(20), 3, TimeSpan.Zero);
            sw.Stop();

            Assert.AreEqual(1, result.Count);
            Assert.IsTrue(result.ContainsKey("https://site.test/ok"));
            Assert.Less(sw.Elapsed.TotalSeconds, 3);
        }

        [Test]
        public void Fetch_StopsAtTotalBudget()
        {
            var wc = new DelayedWebClient();
            var urls = Enumerable.Range(1, 10).Select(i => "https://site.test/p" + i).ToArray();
            foreach (var u in urls)
                wc.Add(u, TimeSpan.FromMilliseconds(800), "x");

            var sw = Stopwatch.StartNew();
            var result = BrazilianDetailPages.Fetch(wc, Urls(urls), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(2), 2, TimeSpan.Zero);
            sw.Stop();

            Assert.Greater(result.Count, 0);
            Assert.Less(result.Count, 10);
            Assert.Less(sw.Elapsed.TotalSeconds, 4.5);
        }

        [Test]
        public void Fetch_RequestsEachUrlOnce()
        {
            var wc = new DelayedWebClient();
            wc.Add("https://site.test/a", TimeSpan.Zero, "a");

            var result = BrazilianDetailPages.Fetch(wc, Urls("https://site.test/a", "https://site.test/a"),
                                                    TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), 3, TimeSpan.Zero);

            Assert.AreEqual(1, result.Count);
            Assert.AreEqual(1, wc.Requested.Count);
        }

        [Test]
        public void ParseSizes_ReadsEachSizeSeparately()
        {
            var sizes = BrazilianDetailPages.ParseSizes("1.5 GB | 3,2 GB | 700 MB");
            Assert.AreEqual(3, sizes.Count);
            Assert.That(sizes[0], Is.InRange(1_500_000_000L, 1_700_000_000L));
            Assert.That(sizes[1], Is.InRange(3_300_000_000L, 3_500_000_000L));
            Assert.That(sizes[2], Is.InRange(700_000_000L, 750_000_000L));
            Assert.AreEqual(0, BrazilianDetailPages.ParseSizes(null).Count);
        }

        [Test]
        public void TryAbsoluteUri_AcceptsOnlyWebAndMagnet()
        {
            Assert.IsNotNull(BrazilianDetailPages.TryAbsoluteUri("https://site.test/x"));
            Assert.IsNotNull(BrazilianDetailPages.TryAbsoluteUri("magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567&dn=x"));
            Assert.IsNull(BrazilianDetailPages.TryAbsoluteUri("/relative/path"));
            Assert.IsNull(BrazilianDetailPages.TryAbsoluteUri(""));
            Assert.IsNull(BrazilianDetailPages.TryAbsoluteUri(null));
        }

        [Test]
        public void ApacheParser_KeepsOrderAndSkipsBrokenDetailPage()
        {
            const string search = @"<html><body>
<div class='capa-item'><h2 class='capa-titulo'><a href='https://apache.test/um' title='Filme Um (2020)'>Filme Um</a></h2></div>
<div class='capa-item'><h2 class='capa-titulo'><a href='https://apache.test/quebrado' title='Quebrado (2021)'>Quebrado</a></h2></div>
<div class='capa-item'><h2 class='capa-titulo'><a href='/relativo' title='Relativo'>Relativo</a></h2></div>
<div class='capa-item'><h2 class='capa-titulo'><a href='https://apache.test/dois' title='Filme Dois (2022)'>Filme Dois</a></h2></div>
</body></html>";
            const string detail = @"<html><body>
<p><strong>Tamanho</strong>: 1.5 GB | 3 GB<br><strong>Qualidade</strong>: 1080p<br></p>
<p>VERSAO 720P<br><a href='magnet:?xt=urn:btih:1111111111111111111111111111111111111111&dn=a'>Baixar</a></p>
<p>VERSAO 1080P<br><a href='magnet:?xt=urn:btih:2222222222222222222222222222222222222222&dn=b'>Baixar</a></p>
</body></html>";

            var wc = new DelayedWebClient();
            wc.Add("https://apache.test/um", TimeSpan.FromMilliseconds(300), detail);
            wc.Add("https://apache.test/quebrado", TimeSpan.FromMilliseconds(10), null);
            wc.Add("https://apache.test/dois", TimeSpan.Zero, detail.Replace("1111", "3333").Replace("2222", "4444"));

            var response = new IndexerResponse(null, new WebResult { ContentBytes = Encoding.UTF8.GetBytes(search), Status = HttpStatusCode.OK, Request = new WebRequest("https://apache.test/index.php?busca=x") });
            var releases = new ApacheTorrentParser(wc).ParseResponse(response);

            Assert.AreEqual(4, releases.Count);
            Assert.AreEqual(new[] { "https://apache.test/um", "https://apache.test/um", "https://apache.test/dois", "https://apache.test/dois" },
                            releases.Select(r => r.Details.AbsoluteUri).ToArray());
            Assert.IsFalse(wc.Requested.Any(u => u.Contains("relativo")));
            Assert.That(releases[0].Size, Is.InRange(1_500_000_000L, 1_700_000_000L));
            Assert.That(releases[1].Size, Is.InRange(3_000_000_000L, 3_300_000_000L));
        }
    }
}
