using System.IO;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using StorageBench.Core.Interfaces;
using StorageBench.Core.Models;
using StorageBench.Core.Services;

namespace StorageBench.Engines;

public class NetworkReadEngine : IReadEngine
{
    public async Task<BenchmarkResult> RunAsync(
        BenchmarkSettings settings,
        IProgress<ReadProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(settings.SourcePath))
            throw new ArgumentException("Network file path is empty.");

        if (settings.BufferSize <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(settings.BufferSize));

        int bufferSize = checked(settings.BufferSize * 1024);

        byte[] buffer = new byte[bufferSize];

        long fileSize = 0;
        bool reachedEnd = false;

        var sampler = new SpeedSampler(progress);
        long position = 0;
        bool reconnectRequested = false;
        NetworkInterfaceState previousState = GetNetworkInterfaceState();

        using var networkChanged = new NetworkChangeMonitor(() => reconnectRequested = true);

        while (!reachedEnd)
        {
            cancellationToken.ThrowIfCancellationRequested();

            NetworkInterfaceState currentState = GetNetworkInterfaceState();
            if (currentState != previousState)
            {
                reconnectRequested = true;
                previousState = currentState;
            }

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
                using var stream = new FileStream(
                    settings.SourcePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize: 1,
                    options: FileOptions.Asynchronous |
                             FileOptions.SequentialScan);

                fileSize = stream.Length;
                stream.Position = position;

                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (settings.PauseController is { } pauseController)
                        await pauseController.WaitIfPausedAsync(cancellationToken);

                    int bytesRead = await stream.ReadAsync(
                        buffer.AsMemory(0, buffer.Length),
                        reconnectCancellation.Token);

                    if (bytesRead == 0)
                    {
                        reachedEnd = true;
                        break;
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
                await Task.Delay(
                    GetNetworkInterfaceState().EthernetAvailable ? 100 : 500,
                    cancellationToken);
            }
        }

        BenchmarkResult result = sampler.Finish();

        result.Source = settings.SourcePath;
        result.SourceKind = nameof(SourceType.Network);
        result.TestType = "Последовательное чтение по сети";
        result.FileSizeBytes = fileSize;
        result.BufferSizeKB = settings.BufferSize;
        result.ReachedEnd = reachedEnd;

        return result;
    }

    private static bool IsReconnectableNetworkError(Exception exception) =>
        exception is IOException or
            SocketException or
            ObjectDisposedException ||
        exception.InnerException is not null &&
        IsReconnectableNetworkError(exception.InnerException);

    private static NetworkInterfaceState GetNetworkInterfaceState()
    {
        bool ethernetAvailable = false;
        bool wirelessAvailable = false;

        foreach (NetworkInterface networkInterface in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (networkInterface.OperationalStatus != OperationalStatus.Up)
                continue;

            if (networkInterface.NetworkInterfaceType == NetworkInterfaceType.Ethernet)
                ethernetAvailable = true;
            else if (networkInterface.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
                wirelessAvailable = true;
        }

        return new NetworkInterfaceState(ethernetAvailable, wirelessAvailable);
    }

    private readonly record struct NetworkInterfaceState(
        bool EthernetAvailable,
        bool WirelessAvailable);

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
