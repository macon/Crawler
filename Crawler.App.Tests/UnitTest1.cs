using Crawler.CLI;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Shouldly;
using System.Linq;

namespace Crawler.App.Tests
{
    public class UnitTest1
    {
        [Fact]
        public async Task Test1Async()
        {
            var crawler = new CrawlManager(new Uri("http://somesite.com"), new CrawlerMock());
            await crawler.CrawlAsync();
            var results = crawler.Results;
            results.Count().ShouldBeGreaterThan(1);
        }
    }

    public class CrawlerMock : ICrawlPage
    {
        public async Task<IEnumerable<string>> CrawlPageAsync(string uri, CancellationToken token)
        {
            var result = new List<string>();
            for (var i = 1; i <= 3; i++)
            {
                result.Add(uri + $"/{i}");
            }
            return result;
        }
    }
}
