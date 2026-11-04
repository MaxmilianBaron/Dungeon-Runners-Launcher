using System.Net;
using System.Net.Http.Headers;

namespace DungeonRunners.Client;

public sealed class Downloads : IDisposable
{
    private readonly HttpClient client;

    public Downloads(HttpMessageHandler? handler = null)
    {
        client = new HttpClient(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.None, ConnectTimeout = TimeSpan.FromSeconds(20) });
        client.Timeout = Timeout.InfiniteTimeSpan;
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Dungeon-Runners-Launcher/1.0.3");
    }

    private async Task<HttpResponseMessage> OpenAsync(string url, long offset, CancellationToken token, Func<string, Uri>? validate = null)
    {
        validate ??= value => Catalog.ValidateDownloadUrl(value);
        var uri = validate(url);
        for (var redirect = 0; redirect < 6; redirect++)
        {
            var response = await RequestAsync(uri, offset, token);
            if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                var location = response.Headers.Location;
                response.Dispose();
                if (location is null) throw new IOException("Download redirect is missing its destination.");
                uri = validate(new Uri(uri, location).AbsoluteUri);
                continue;
            }
            return response;
        }
        throw new IOException("Too many download redirects.");
    }

    private async Task<HttpResponseMessage> RequestAsync(Uri uri, long offset, CancellationToken token)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            if (offset > 0) request.Headers.Range = new RangeHeaderValue(offset, null);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(45));
            try
            {
                var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                if (attempt < 2 && (response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500))
                {
                    response.Dispose();
                    await Task.Delay(TimeSpan.FromSeconds(attempt + 1), token);
                    continue;
                }
                return response;
            }
            catch (Exception e) when (attempt < 2 && !token.IsCancellationRequested && e is HttpRequestException or OperationCanceledException)
            {
                await Task.Delay(TimeSpan.FromSeconds(attempt + 1), token);
            }
        }
    }

    public async Task<byte[]> ReadManifestAsync(CancellationToken token)
        => await ReadAsync(Catalog.Feed, 128 * 1024, value => Catalog.ValidateDownloadUrl(value), token);

    public async Task<byte[]> ReadAsync(string url, int limit, Func<string, Uri> validate, CancellationToken token)
    {
        using var response = await OpenAsync(url, 0, token, validate);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > limit) throw new InvalidDataException("Response is too large.");
        using var data = new MemoryStream();
        await CopyLimitedAsync(await response.Content.ReadAsStreamAsync(token), data, limit, null, token);
        return data.ToArray();
    }

    public Task<string> PackageAsync(ClientPackage package, string cache, IProgress<ProgressInfo>? progress, CancellationToken token)
        => VerifiedFileAsync(package, cache, progress, token, value => Catalog.ValidateDownloadUrl(value));

    public async Task<string> VerifiedFileAsync(ClientPackage package, string cache, IProgress<ProgressInfo>? progress, CancellationToken token, Func<string, Uri> validate)
    {
        validate(package.Url);
        if (!Catalog.IsHash(package.Sha256) || package.Size <= 0 || package.Size > Catalog.MaximumPackage) throw new InvalidDataException("Invalid download identity.");
        SafeFiles.NoLinks(cache);
        Directory.CreateDirectory(cache);
        var target = SafeFiles.Under(cache, package.Sha256 + ".zip");
        if (File.Exists(target))
        {
            if (new FileInfo(target).Length == package.Size && await Catalog.HashAsync(target, token) == package.Sha256) return target;
            File.Delete(target);
        }
        var partial = SafeFiles.Under(cache, package.Sha256 + ".part");
        var start = File.Exists(partial) ? new FileInfo(partial).Length : 0;
        if (start >= package.Size)
        {
            if (start == package.Size && await Catalog.HashAsync(partial, token) == package.Sha256)
            {
                File.Move(partial, target);
                return target;
            }
            File.Delete(partial);
            start = 0;
        }
        using var response = await OpenAsync(package.Url, start, token, validate);
        response.EnsureSuccessStatusCode();
        if (response.StatusCode == HttpStatusCode.PartialContent)
        {
            var range = response.Content.Headers.ContentRange;
            if (range?.From != start || range.Length != package.Size || range.To != package.Size - 1 || range.Unit != "bytes")
                throw new InvalidDataException("The server returned an invalid download range.");
        }
        else if (response.StatusCode == HttpStatusCode.OK) start = 0;
        else throw new IOException("Unexpected download response.");
        if (response.Content.Headers.ContentLength is long count && count != package.Size - start)
            throw new InvalidDataException("The download size does not match the manifest.");
        SafeFiles.NoLinks(partial);
        using (var output = new FileStream(partial, start == 0 ? FileMode.Create : FileMode.Append, FileAccess.Write, FileShare.None, 128 * 1024, true))
        {
            var last = Environment.TickCount64;
            await CopyLimitedAsync(await response.Content.ReadAsStreamAsync(token), output, package.Size - start, bytes =>
            {
                if (Environment.TickCount64 - last >= 80 || start + bytes == package.Size)
                {
                    last = Environment.TickCount64;
                    progress?.Report(new("Downloading", package.Name, start + bytes, package.Size));
                }
            }, token);
            await output.FlushAsync(token);
        }
        progress?.Report(new("Verifying download", package.Name));
        if (new FileInfo(partial).Length != package.Size || await Catalog.HashAsync(partial, token) != package.Sha256)
        {
            File.Delete(partial);
            throw new InvalidDataException("Download verification failed. Try again to download a fresh copy.");
        }
        File.Move(partial, target);
        return target;
    }

    public static async Task CopyLimitedAsync(Stream input, Stream output, long limit, Action<long>? progress, CancellationToken token)
    {
        using (input)
        {
            var buffer = new byte[128 * 1024];
            long written = 0;
            while (true)
            {
                using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                readTimeout.CancelAfter(TimeSpan.FromSeconds(45));
                var read = await input.ReadAsync(buffer, readTimeout.Token);
                if (read == 0) break;
                written += read;
                if (written > limit) throw new InvalidDataException("Download or archive exceeds its declared size.");
                await output.WriteAsync(buffer.AsMemory(0, read), token);
                progress?.Invoke(written);
            }
        }
    }

    public void Dispose() => client.Dispose();
}
