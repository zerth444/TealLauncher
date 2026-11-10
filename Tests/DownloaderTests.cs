using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using TealLauncher;

internal static class DownloaderTests
{
    public static async Task Run(string root)
    {
        string folder = Path.Combine(root, "download-fixtures-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        byte[] bytes = Enumerable.Range(0, 10000).Select(i => (byte)i).ToArray();
        var hashes = new Dictionary<string, string> { ["sha512"] = Convert.ToHexString(SHA512.HashData(bytes)) };
        int calls = 0;
        bool rangeSeen = false;
        using var client = new HttpClient(new Handler(request =>
        {
            calls++;
            if (calls == 1) return new(HttpStatusCode.OK) { Content = new StreamContent(new FailingStream(bytes[..5000])) };
            long offset = request.Headers.Range!.Ranges.First().From!.Value;
            rangeSeen = offset == 5000;
            var content = new ByteArrayContent(bytes[(int)offset..]);
            content.Headers.ContentRange = new ContentRangeHeaderValue(offset, bytes.Length - 1, bytes.Length);
            return new(HttpStatusCode.PartialContent) { Content = content };
        }));
        var downloader = new ContentDownloader(Path.Combine(folder, "cache"), client, TimeSpan.FromSeconds(1));
        string target = Path.Combine(folder, "test.jar");
        await downloader.DownloadAsync(new[] { "https://fixture.invalid/mod" }, target, hashes);
        Require(rangeSeen && calls == 2 && File.ReadAllBytes(target).SequenceEqual(bytes), "interrupted download resumes with Range and verifies hash");
        await downloader.DownloadAsync(new[] { "https://fixture.invalid/mod" }, Path.Combine(folder, "copy.jar"), hashes);
        Require(calls == 2, "verified cache avoids repeated network download");
        byte[] large = new byte[9 * 1024 * 1024];
        Random.Shared.NextBytes(large);
        int chunks = 0;
        using var ranges = new HttpClient(new Handler(request =>
        {
            chunks++;
            var range = request.Headers.Range!.Ranges.First();
            long start = range.From!.Value, end = Math.Min(range.To!.Value, large.Length - 1);
            var content = new ByteArrayContent(large[(int)start..((int)end + 1)]);
            content.Headers.ContentRange = new ContentRangeHeaderValue(start, end, large.Length);
            return new(HttpStatusCode.PartialContent) { Content = content };
        }));
        var largeDownload = new ContentDownloader(Path.Combine(folder, "ranges"), ranges);
        string largeTarget = Path.Combine(folder, "large.jar");
        await largeDownload.DownloadAsync(new[] { "https://fixture.invalid/large" }, largeTarget,
            new() { ["sha512"] = Convert.ToHexString(SHA512.HashData(large)) });
        Require(chunks == 3 && File.ReadAllBytes(largeTarget).SequenceEqual(large), "large files assembled from bounded ranges without truncation");
        using var stalledClient = new HttpClient(new Handler(_ => new(HttpStatusCode.OK) { Content = new StreamContent(new StalledStream()) }));
        var stalled = new ContentDownloader(Path.Combine(folder, "stall"), stalledClient, TimeSpan.FromMilliseconds(20));
        bool timedOut = false;
        try { await stalled.DownloadAsync(new[] { "https://fixture.invalid/stall" }, Path.Combine(folder, "stall.jar"), hashes); }
        catch (IOException) { timedOut = true; }
        Require(timedOut && !File.Exists(Path.Combine(folder, "stall.jar")), "stalled stream times out without publishing partial file");
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(40));
        bool cancelled = false;
        try { await stalled.DownloadAsync(new[] { "https://fixture.invalid/stall" }, Path.Combine(folder, "cancel.jar"), hashes, cancellationToken: cancel.Token); }
        catch (OperationCanceledException) { cancelled = true; }
        Require(cancelled, "user cancellation interrupts pending reads and retries");
    }

    static void Require(bool value, string label)
    { if (!value) throw new Exception(label); Console.WriteLine("PASS " + label); }

    sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(send(request));
    }
    sealed class FailingStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => Position >= Length ? ValueTask.FromException<int>(new IOException("simulated disconnect")) : base.ReadAsync(buffer, cancellationToken);
    }
    sealed class StalledStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { await Task.Delay(Timeout.Infinite, cancellationToken); return 0; }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
