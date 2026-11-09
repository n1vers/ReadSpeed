using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using StorageBench.Core.Interfaces;
using StorageBench.Core.Models;
using StorageBench.Core.Services;

namespace StorageBench.Engines;

public sealed class InternetReadEngine : IReadEngine
{
    private static readonly HttpClient HttpClient = CreateHttpClient();

    private static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.None,
            AllowAutoRedirect = true,
            UseCookies = false
        };

        var client = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };

        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "StorageBench/1.0");

        return client;
    }

    public async Task<BenchmarkResult> RunAsync(
        BenchmarkSettings settings,
        IProgress<ReadProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (!Uri.TryCreate(
                settings.SourcePath,
                UriKind.Absolute,
                out Uri? uri) ||
            (uri.Scheme != Uri.UriSchemeHttp &&
             uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException(
                "Укажи корректную HTTP или HTTPS ссылку.");
        }

        if (settings.BufferSize <= 0)
            throw new ArgumentException("Размер буфера должен быть больше 0.");

        int bufferSize = checked(settings.BufferSize * 1024);
        byte[] buffer = new byte[bufferSize];

        var sampler = new SpeedSampler(progress);
        long position = 0;
        long? contentLength = null;
        bool reachedEnd = false;
        bool reconnectRequested = false;

        using var networkChanged = new NetworkChangeMonitor(() => reconnectRequested = true);

        while (!reachedEnd)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var reconnectCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);

            void RequestReconnect()
            {
                reconnectRequested = true;
                reconnectCancellation.Cancel();
            }

            networkChanged.Changed += RequestReconnect;

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);

                if (position > 0)
                {
                    request.Headers.Range = new RangeHeaderValue(position, null);
                }

                using HttpResponseMessage response = await HttpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    reconnectCancellation.Token);

                response.EnsureSuccessStatusCode();

                if (response.Content.Headers.ContentRange?.Length is long rangeLength)
                    contentLength = rangeLength;
                else if (position == 0)
                    contentLength = response.Content.Headers.ContentLength;

                await using Stream stream = await response.Content.ReadAsStreamAsync(
                    reconnectCancellation.Token);

                long bytesToSkip = position > 0 &&
                                   response.StatusCode != HttpStatusCode.PartialContent
                    ? position
                    : 0;

                while (true)
                {
                    if (settings.PauseController is { } pauseController)
                        await pauseController.WaitIfPausedAsync(cancellationToken);

                    int bytesRead = await stream.ReadAsync(
                        buffer.AsMemory(),
                        reconnectCancellation.Token);

                    if (bytesRead == 0)
                    {
                        reachedEnd = true;
                        break;
                    }

                    if (bytesToSkip > 0)
                    {
                        if (bytesRead <= bytesToSkip)
                        {
                            bytesToSkip -= bytesRead;
                            continue;
                        }

                        int usableBytes = checked((int)(bytesRead - bytesToSkip));
                        bytesToSkip = 0;
                        position += usableBytes;
                        sampler.AddBytes(usableBytes);
                        continue;
                    }

                    position += bytesRead;
                    sampler.AddBytes(bytesRead);
                }
            }
            catch (OperationCanceledException) when (
                reconnectRequested && !cancellationToken.IsCancellationRequested)
            {
                reconnectRequested = false;
            }
            catch (Exception exception) when (
                !cancellationToken.IsCancellationRequested &&
                IsReconnectableNetworkError(exception))
            {
                reconnectRequested = true;
            }
            finally
            {
                networkChanged.Changed -= RequestReconnect;
            }

            if (!reachedEnd && reconnectRequested)
            {
                reconnectRequested = false;
                await Task.Delay(500, cancellationToken);
            }
        }

        BenchmarkResult result = sampler.Finish();

        result.Source = uri.ToString();
        result.SourceKind = nameof(SourceType.Internet);
        result.TestType = "Последовательное чтение из интернета";
        result.FileSizeBytes = contentLength;
        result.BufferSizeKB = settings.BufferSize;
        result.ReachedEnd = reachedEnd;

        return result;
    }

    private static bool IsReconnectableNetworkError(Exception exception) =>
        exception is IOException or
            HttpRequestException or
            SocketException or
            ObjectDisposedException ||
        exception.InnerException is not null &&
        IsReconnectableNetworkError(exception.InnerException);

    private sealed class NetworkChangeMonitor : IDisposable
    {
        public event Action? Changed;

        public NetworkChangeMonitor(Action changed)
        {
            Changed += changed;
            NetworkChange.NetworkAddressChanged += OnNetworkChanged;
            NetworkChange.NetworkAvailabilityChanged += OnNetworkChanged;
        }

        public void Dispose()
        {
            NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
            NetworkChange.NetworkAvailabilityChanged -= OnNetworkChanged;
            Changed = null;
        }

        private void OnNetworkChanged(object? sender, EventArgs e) => Changed?.Invoke();
    }
}
