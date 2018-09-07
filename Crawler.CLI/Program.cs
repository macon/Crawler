using HtmlAgilityPack;
using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using ScrapySharp.Extensions;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks.Dataflow;

namespace Crawler.CLI
{
    class Program
    {
        static void Main(string[] args)
        {
            var c = new CrawlManager(new Uri("https://www.monzo.com"), new SomeWebCrawler());
            try
            {
                var t = c.CrawlAsync();
                t.Wait();
                var x = c.Results;
                Console.WriteLine($"crawled {x.Count()}");
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
            }
            Console.ReadLine();
        }
    }

    public class CrawlManager
    {
        private const int DEFAULT_FETCHERS = 8;
        private static ConcurrentQueue<string> _q =  new ConcurrentQueue<string>();
        private static new ConcurrentDictionary<string, bool> _pagesVisited = new ConcurrentDictionary<string, bool>();
        private static HashSet<string> _visitedPages = new HashSet<string>();
        private static IList<Task> _tasks = new List<Task>();
        private Uri BaseUri;
        private int _pagesLeftToCrawl = 0;

        public IEnumerable<string> Results => _visitedPages;

        public CrawlManager(Uri uri, ICrawlPage crawler)
        {
            BaseUri = uri;
            Crawler = crawler;
        }

        private ICrawlPage Crawler { get; }

        TransformManyBlock<string, string> _crawlerBlock;
        TransformBlock<string, string> _incrBlock;
        CancellationTokenSource _cts;
        CancellationToken _ct;
        private TransformManyBlock<string, string> _urlFilterBlock;
        private ActionBlock<string> _queueBlock;

        public async Task CrawlAsync()
        {
            _cts = new CancellationTokenSource();
            _ct = _cts.Token;
            _ct = CancellationToken.None;

            _urlFilterBlock = new TransformManyBlock<string, string>(uris => filterUri(uris));
            _queueBlock = new ActionBlock<string>(x => queueJob(x));

            _crawlerBlock = new TransformManyBlock<string, string>(
                uri => CrawlPage(uri, _ct),
                new ExecutionDataflowBlockOptions() { MaxDegreeOfParallelism = DEFAULT_FETCHERS, BoundedCapacity = 10 });
             
            _incrBlock = new TransformBlock<string, string>(s => 
            {
                Interlocked.Increment(ref _pagesLeftToCrawl);
                return s;
            });

            _incrBlock.LinkTo(_crawlerBlock, new DataflowLinkOptions() { PropagateCompletion = true });
            _crawlerBlock.LinkTo(_urlFilterBlock, new DataflowLinkOptions() { PropagateCompletion = true });
            _urlFilterBlock.LinkTo(_queueBlock, new DataflowLinkOptions() { PropagateCompletion = true });

            //var qTask = Task.Run(async () =>
            //{
            //    Console.WriteLine("in qTask");
            //    while (_q.IsEmpty)
            //    {
            //        Console.WriteLine("_q empty");
            //        await Task.Delay(10, _ct);
            //    }
            //    while (true && !_ct.IsCancellationRequested)
            //    {
            //        while (_q.TryDequeue(out string item))
            //        {
            //            var sent = await _incrBlock.SendAsync(item, _ct);
            //            //Console.WriteLine($"_incrBlock.SendAsync -> {sent}");
            //        }
            //    }
            //    Console.WriteLine("exited runner");
            //}, _ct);

            _incrBlock.Post(BaseUri.ToString());

            //await ProcessingIsComplete();
            await _queueBlock.Completion;
            Console.WriteLine("after ProcessingIsComplete");
            //_incrBlock.Complete();
            //_cts.Cancel();
            Console.WriteLine("after inputBuffer.Complete()");
            await Task.WhenAll(_incrBlock.Completion, _crawlerBlock.Completion, _urlFilterBlock.Completion, _queueBlock.Completion);

            Console.WriteLine("done");

        }

        private void queueJob(string x)
        {
            _incrBlock.Post(x);
            if (_incrBlock.InputCount == 0 && _crawlerBlock.InputCount == 0 && _urlFilterBlock.InputCount == 0 && _queueBlock.InputCount == 0) { _incrBlock.Complete(); }
        }

        private IEnumerable<string> filterUri(string uri)
        {
            var resultUri = new Uri(uri);
            if (resultUri.Host.ToLower() == BaseUri.Host.ToLower())
            {
                yield return uri;
            }
        }

        private async Task WaitForPipelineToStart()
        {
            while (!_visitedPages.Any())
            {
                await Task.Delay(500);
            } 
        }

        private async Task ProcessingIsComplete()
        {
            do
            {
                await Task.Delay(500);
                Console.WriteLine($"_crawlerBlock.InputCount={_crawlerBlock.InputCount}, _q.IsEmpty={_q.IsEmpty}, _incrBlock.InputCount={_incrBlock.InputCount}, _incrBlock.OutputCount={_incrBlock.OutputCount}, _pagesLeftToCrawl={_pagesLeftToCrawl}, _q.Count={_q.Count}");

            } while (!_ct.IsCancellationRequested && !PagesBeingCrawledIsIdle());

            if (_ct.IsCancellationRequested)
            {
                Console.WriteLine("Cancellation requested");
            }
            else
            {
                Console.WriteLine("Completed");
            }
        }


        private bool PagesBeingCrawledIsIdle()
        {
            return _crawlerBlock.InputCount == 0 &&
                _incrBlock.InputCount == 0 &&
                _incrBlock.OutputCount == 0 &&
                _q.IsEmpty &&
                _pagesLeftToCrawl <= 0;
        }

        private IEnumerable<string> CrawlPage(string uri, CancellationToken token)
        {
            try
            {
                Console.WriteLine($"processing {uri}");
                _visitedPages.Add(uri);
                token.ThrowIfCancellationRequested();
                Console.WriteLine($"[{Thread.CurrentThread.ManagedThreadId}] _visitedPages={_visitedPages.Count}");

                var tsk = Crawler.CrawlPageAsync(uri, token);
                tsk.Wait(token);
                var results = tsk.Result;
                var finalRresults = new List<string>();

                foreach (var result in results)
                {
                    if (_visitedPages.Contains(result)) continue;
                    finalRresults.Add(result);
                }

                Console.WriteLine($"[{Thread.CurrentThread.ManagedThreadId}] finalRresults={finalRresults.Count()}");


                return finalRresults;
            }
            finally
            {
                Interlocked.Decrement(ref _pagesLeftToCrawl);
            }
        }
    }

    public interface ICrawlPage
    {
        Task<IEnumerable<string>> CrawlPageAsync(string uri, CancellationToken token);
    }

    public class SomeWebCrawler : ICrawlPage
    {
        private static Uri BaseUri = new Uri("https://www.monzo.com");

        public async Task<IEnumerable<string>> CrawlPageAsync(string uri, CancellationToken token)
        {
            var webGet = new HtmlWeb();
            var childPages = new List<string>();
            try
            {
                var htmlDoc = await webGet.LoadFromWebAsync(uri, token);
                var anchors = htmlDoc.DocumentNode.CssSelect("a[rel!=nofollow]");

                foreach (var anchor in anchors)
                {
                    var href = anchor.GetAttributeValue("href");

                    if (href.StartsWith('#')) { continue; }
                    var indexOfBookmark = href.IndexOf('#');
                    if (indexOfBookmark >= 0)
                    {
                        href = href.Remove(indexOfBookmark);
                    }

                    if (!Uri.IsWellFormedUriString(href, UriKind.Absolute))
                    {
                        if (Uri.IsWellFormedUriString(href, UriKind.Relative))
                        {
                            href = BaseUri.Combine(href).ToString();
                        }
                        else
                        {
                            if (Uri.TryCreate(href, UriKind.Absolute, out Uri childUri))
                            {
                                href = childUri.AbsoluteUri;
                            }
                            else
                            {
                                // invalid
                                Console.WriteLine($"Invalid URL: {href}");
                            }
                        }
                    }

                    if (!childPages.Contains(href)) { childPages.Add(href); }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"failed to get page {uri}, {ex.GetType()}");
            }
            return childPages;
        }
    }
}
