using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using SwVault.Core.Auth;
using SwVault.Protocol;

namespace SwVault.Core.Lfs;

public sealed record LfsUploadItem(string Oid, long Size, string FilePath);

public sealed record LfsDownloadItem(string Oid, long Size, string DestinationPath);

public sealed record TransferProgress(long BytesDone, long BytesTotal, int FilesDone, int FilesTotal);

/// <summary>Git LFS batch API client using the "basic" transfer adapter.</summary>
public sealed class LfsClient
{
    private const int BatchSize = 100;
    private const int Parallelism = 4;
    private readonly HttpClient _http;
    private readonly LfsHttp _api;
    private readonly string _refName;

    public LfsClient(HttpClient http, Uri lfsBase, AuthContext auth, string refName = "refs/heads/main")
    {
        _http = http;
        _api = new LfsHttp(http, lfsBase, auth);
        _refName = refName;
    }

    /// <summary>Uploads objects the server does not have yet. Returns the oids actually sent.</summary>
    public async Task<IReadOnlySet<string>> UploadAsync(IReadOnlyList<LfsUploadItem> items, IProgress<TransferProgress>? progress, CancellationToken ct)
    {
        var uploaded = new HashSet<string>(StringComparer.Ordinal);
        var unique = items.GroupBy(i => i.Oid).Select(g => g.First()).ToList();
        var tracker = new ProgressTracker(unique.Sum(i => i.Size), unique.Count, progress);

        foreach (var chunk in unique.Chunk(BatchSize))
        {
            var response = await BatchAsync("upload", chunk.Select(i => (i.Oid, i.Size)), ct).ConfigureAwait(false);
            var byOid = chunk.ToDictionary(i => i.Oid);
            var work = new List<(LfsUploadItem Item, BatchObject Obj)>();
            foreach (var obj in response.Objects)
            {
                if (!byOid.TryGetValue(obj.Oid, out var item)) continue;
                if (obj.Error != null) throw new VaultException(ErrorCodes.Internal, $"The server refused to store {Path.GetFileName(item.FilePath)}: {obj.Error.Message}");
                if (obj.Actions == null || !obj.Actions.ContainsKey("upload"))
                {
                    tracker.Complete(item.Size); // already on the server
                    continue;
                }
                work.Add((item, obj));
            }

            await Parallel.ForEachAsync(work, new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = ct }, async (w, token) =>
            {
                await WithRetriesAsync(() => UploadOneAsync(w.Item, w.Obj, tracker, token), token).ConfigureAwait(false);
                tracker.Complete(0);
                lock (uploaded) uploaded.Add(w.Item.Oid);
            }).ConfigureAwait(false);
        }
        return uploaded;
    }

    public async Task DownloadAsync(IReadOnlyList<LfsDownloadItem> items, IProgress<TransferProgress>? progress, CancellationToken ct)
    {
        var tracker = new ProgressTracker(items.Sum(i => i.Size), items.Count, progress);
        foreach (var chunk in items.Chunk(BatchSize))
        {
            var specs = chunk.GroupBy(i => i.Oid).Select(g => (g.Key, g.First().Size));
            var response = await BatchAsync("download", specs, ct).ConfigureAwait(false);
            var objects = response.Objects.ToDictionary(o => o.Oid, StringComparer.Ordinal);

            await Parallel.ForEachAsync(chunk, new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = ct }, async (item, token) =>
            {
                if (!objects.TryGetValue(item.Oid, out var obj))
                    throw new VaultException(ErrorCodes.NotFound, $"The server did not return file content {item.Oid[..12]}.");
                if (obj.Error != null || obj.Actions == null || !obj.Actions.TryGetValue("download", out var action))
                    throw new VaultException(ErrorCodes.NotFound, $"File content {item.Oid[..12]} is missing on the server: {obj.Error?.Message}");
                await WithRetriesAsync(() => DownloadOneAsync(item, action, tracker, token), token).ConfigureAwait(false);
                tracker.Complete(0);
            }).ConfigureAwait(false);
        }
    }

    private async Task<BatchResponse> BatchAsync(string operation, IEnumerable<(string Oid, long Size)> objects, CancellationToken ct)
    {
        var body = new BatchRequest
        {
            Operation = operation,
            Ref = new LfsRef { Name = _refName },
            Objects = objects.Select(o => new LfsObjectSpec { Oid = o.Oid, Size = o.Size }).ToList(),
        };
        var (status, parsed, raw) = await _api.SendAsync<BatchResponse>(HttpMethod.Post, "objects/batch", body, ct).ConfigureAwait(false);
        if (status != HttpStatusCode.OK || parsed == null) throw LfsHttp.Failure($"start the {operation}", status, raw);
        return parsed;
    }

    private async Task UploadOneAsync(LfsUploadItem item, BatchObject obj, ProgressTracker tracker, CancellationToken ct)
    {
        var action = obj.Actions!["upload"];
        await using (var file = new FileStream(item.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 20, useAsync: true))
        {
            if (file.Length != item.Size) throw new VaultException(ErrorCodes.Conflict, $"{Path.GetFileName(item.FilePath)} changed while it was being checked in.");
            using var request = new HttpRequestMessage(HttpMethod.Put, action.Href)
            {
                Content = new ProgressStreamContent(file, tracker),
            };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            request.Content.Headers.ContentLength = item.Size;
            await _api.ApplyActionHeadersAsync(request, action, ct).ConfigureAwait(false);
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new TransferException(response.StatusCode, await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        }

        if (obj.Actions.TryGetValue("verify", out var verify))
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, verify.Href)
            {
                Content = new StringContent($"{{\"oid\":\"{item.Oid}\",\"size\":{item.Size}}}"),
            };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue(LfsHttp.MediaType);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(LfsHttp.MediaType));
            await _api.ApplyActionHeadersAsync(request, verify, ct).ConfigureAwait(false);
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new TransferException(response.StatusCode, await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        }
    }

    private async Task DownloadOneAsync(LfsDownloadItem item, BatchAction action, ProgressTracker tracker, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(item.DestinationPath)!);
        using var request = new HttpRequestMessage(HttpMethod.Get, action.Href);
        await _api.ApplyActionHeadersAsync(request, action, ct).ConfigureAwait(false);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new TransferException(response.StatusCode, await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long total = 0;
        var buffer = new byte[1 << 20];
        await using (var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var target = new FileStream(item.DestinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true))
        {
            int read;
            while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                hash.AppendData(buffer, 0, read);
                await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                total += read;
                tracker.Add(read);
            }
        }

        var oid = Convert.ToHexStringLower(hash.GetHashAndReset());
        if (total != item.Size || !string.Equals(oid, item.Oid, StringComparison.Ordinal))
        {
            File.Delete(item.DestinationPath);
            throw new IOException($"Downloaded content for {item.Oid[..12]} failed verification (got {total} bytes, sha256 {oid[..12]}).");
        }
    }

    private static async Task WithRetriesAsync(Func<Task> action, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await action().ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (attempt < 3 && IsTransient(ex) && !ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(attempt * 2), ct).ConfigureAwait(false);
            }
        }
    }

    private static bool IsTransient(Exception ex) => ex switch
    {
        TransferException t => (int)t.Status >= 500 || t.Status == HttpStatusCode.RequestTimeout || t.Status == HttpStatusCode.TooManyRequests,
        HttpRequestException => true,
        IOException => true,
        _ => false,
    };

    private sealed class TransferException : VaultException
    {
        public HttpStatusCode Status { get; }

        public TransferException(HttpStatusCode status, string body)
            : base(status == HttpStatusCode.Forbidden ? ErrorCodes.Forbidden : ErrorCodes.Internal, $"File transfer failed (HTTP {(int)status}): {Truncate(body)}")
        {
            Status = status;
        }

        private static string Truncate(string s) => s.Length > 300 ? s[..300] : s;
    }

    internal sealed class ProgressTracker
    {
        private readonly long _totalBytes;
        private readonly int _totalFiles;
        private readonly IProgress<TransferProgress>? _progress;
        private long _bytes;
        private int _files;

        public ProgressTracker(long totalBytes, int totalFiles, IProgress<TransferProgress>? progress)
        {
            _totalBytes = totalBytes;
            _totalFiles = totalFiles;
            _progress = progress;
        }

        public void Add(long bytes)
        {
            var done = Interlocked.Add(ref _bytes, bytes);
            _progress?.Report(new TransferProgress(done, _totalBytes, Volatile.Read(ref _files), _totalFiles));
        }

        public void Complete(long skippedBytes)
        {
            var files = Interlocked.Increment(ref _files);
            var done = Interlocked.Add(ref _bytes, skippedBytes);
            _progress?.Report(new TransferProgress(done, _totalBytes, files, _totalFiles));
        }
    }

    /// <summary>Streams a file as request content while reporting upload progress.</summary>
    private sealed class ProgressStreamContent : HttpContent
    {
        private readonly Stream _source;
        private readonly ProgressTracker _tracker;

        public ProgressStreamContent(Stream source, ProgressTracker tracker)
        {
            _source = source;
            _tracker = tracker;
        }

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            await SerializeToStreamAsync(stream, context, CancellationToken.None).ConfigureAwait(false);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            var buffer = new byte[1 << 20];
            _source.Position = 0;
            int read;
            while ((read = await _source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await stream.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                _tracker.Add(read);
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = _source.Length;
            return true;
        }
    }
}
