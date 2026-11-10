using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace TealLauncher;

public sealed record TransferProgress(string File, long Received, long Total, string Activity);

/// <summary>Verified, resumable downloads with an inactivity deadline (also while reading the body).</summary>
public sealed class ContentDownloader
{
    private static readonly HttpClient Client = new(new SocketsHttpHandler
    {
        ConnectTimeout = TimeSpan.FromSeconds(15), MaxConnectionsPerServer = 8,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    }) { Timeout = Timeout.InfiniteTimeSpan };
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks = new();
    private readonly HttpClient _client;
    private readonly TimeSpan _inactivity;
    private readonly string _cache;

    public ContentDownloader(string cache, HttpClient? client = null, TimeSpan? inactivity = null)
    {
        _cache = cache;
        _client = client ?? Client;
        _inactivity = inactivity ?? TimeSpan.FromSeconds(20);
    }

    public async Task DownloadAsync(IEnumerable<string> urls, string target, Dictionary<string, string> hashes,
        IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string algorithm = hashes.ContainsKey("sha512") ? "sha512" : "sha1";
        if (!hashes.TryGetValue(algorithm, out string? hash) || hash.Length != (algorithm == "sha512" ? 128 : 40)
            || !hash.All(Uri.IsHexDigit)) throw new InvalidDataException("У файла нет корректной контрольной суммы.");
        string name = Path.GetFileName(target);
        Directory.CreateDirectory(_cache);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        string cached = Path.Combine(_cache, hash.ToLowerInvariant());
        var gate = Locks.GetOrAdd(cached, _ => new SemaphoreSlim(1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (await IsValidAsync(target, hashes, cancellationToken))
            {
                long size = new FileInfo(target).Length;
                progress?.Report(new(name, size, size, "Уже скачан"));
                return;
            }
            if (!await IsValidAsync(cached, hashes, cancellationToken))
            {
                string partial = cached + ".part";
                Exception? last = null;
                bool downloaded = false;
                foreach (string url in urls.Distinct())
                {
                    if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https")
                        throw new InvalidDataException("Разрешены только HTTPS-загрузки.");
                    for (int attempt = 1; attempt <= 3 && !downloaded; attempt++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        try
                        {
                            while (!downloaded)
                            {
                            long offset = File.Exists(partial) ? new FileInfo(partial).Length : 0;
                            long requestOffset = offset;
                            progress?.Report(new(name, offset, 0, attempt == 1 ? "Подключение" : $"Повтор {attempt}/3"));
                            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                            deadline.CancelAfter(_inactivity);
                            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                            request.Headers.UserAgent.ParseAdd("TealLauncher/1.0");
                            // Bounded ranges avoid one long, throttled transfer for very large mods.
                            request.Headers.Range = new RangeHeaderValue(offset, offset + 4 * 1024 * 1024 - 1);
                            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                            if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
                            {
                                if (await IsValidAsync(partial, hashes, cancellationToken))
                                { File.Move(partial, cached, true); downloaded = true; break; }
                                File.Delete(partial);
                                throw new IOException("Сервер отклонил продолжение загрузки; повтор с начала.");
                            }
                            response.EnsureSuccessStatusCode();
                            bool resume = response.StatusCode == HttpStatusCode.PartialContent
                                && response.Content.Headers.ContentRange?.From == offset;
                            if (!resume) offset = 0;
                            long total = response.Content.Headers.ContentRange?.Length
                                ?? response.Content.Headers.ContentLength.GetValueOrDefault() + offset;
                            await using (var input = await response.Content.ReadAsStreamAsync(deadline.Token))
                            await using (var output = new FileStream(partial, resume ? FileMode.Append : FileMode.Create,
                                FileAccess.Write, FileShare.Read, 131072, true))
                            {
                                byte[] buffer = new byte[131072];
                                var throttle = Stopwatch.StartNew();
                                while (true)
                                {
                                    deadline.CancelAfter(_inactivity);
                                    int read = await input.ReadAsync(buffer, deadline.Token);
                                    if (read == 0) break;
                                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                                    offset += read;
                                    if (throttle.ElapsedMilliseconds >= 150)
                                    { progress?.Report(new(name, offset, total, "Скачивание")); throttle.Restart(); }
                                }
                            }
                            if (resume && offset < total)
                            {
                                if (offset <= requestOffset) throw new IOException("Сервер не передал данные запрошенного фрагмента.");
                                continue;
                            }
                            if (!await IsValidAsync(partial, hashes, cancellationToken))
                            { File.Delete(partial); throw new InvalidDataException("Контрольная сумма не совпадает."); }
                            File.Move(partial, cached, true);
                            downloaded = true;
                            }
                        }
                        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or OperationCanceledException)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            last = ex;
                            progress?.Report(new(name, 0, 0, $"Нет данных или ошибка сети. Повтор {attempt}/3"));
                            if (attempt < 3) await Task.Delay(TimeSpan.FromMilliseconds(500 * attempt), cancellationToken);
                        }
                    }
                    if (downloaded) break;
                }
                if (!downloaded) throw new IOException($"Не удалось скачать {name}. {last?.Message}", last);
            }
            cancellationToken.ThrowIfCancellationRequested();
            string copy = target + "." + Guid.NewGuid().ToString("N") + ".part";
            try
            {
                File.Copy(cached, copy, true);
                File.Move(copy, target, true);
            }
            finally { if (File.Exists(copy)) File.Delete(copy); }
            long length = new FileInfo(cached).Length;
            progress?.Report(new(name, length, length, "Готово"));
        }
        finally { gate.Release(); }
    }

    private static async Task<bool> IsValidAsync(string path, Dictionary<string, string> hashes, CancellationToken token)
    {
        if (!File.Exists(path)) return false;
        foreach (var (algorithm, expected) in hashes.Where(h => h.Key is "sha1" or "sha512"))
        {
            await using var stream = File.OpenRead(path);
            byte[] actual = algorithm == "sha512" ? await SHA512.HashDataAsync(stream, token) : await SHA1.HashDataAsync(stream, token);
            if (!Convert.ToHexString(actual).Equals(expected, StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }
}
