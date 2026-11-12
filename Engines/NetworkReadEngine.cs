using System.IO;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
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
        long position = Math.Max(0, settings.StartOffset);
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
            long nextTopologyCheck = 0;

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
                long startOffset = position;
                if (startOffset > stream.Length)
                    throw new ArgumentOutOfRangeException(nameof(settings.StartOffset));

                stream.Position = startOffset;
                long remaining = Math.Min(
                    stream.Length - position,
                    Math.Max(0, (settings.MaxBytes ?? long.MaxValue) -
                        (position - settings.StartOffset)));

                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (settings.PauseController is { } pauseController)
                        await pauseController.WaitIfPausedAsync(cancellationToken);

                    if (Environment.TickCount64 >= nextTopologyCheck)
                    {
                        nextTopologyCheck = Environment.TickCount64 + 250;
                        NetworkInterfaceState state = GetNetworkInterfaceState();

                        if (state != previousState)
                        {
                            previousState = state;
                            RequestReconnect();
                        }
                    }

                    if (remaining <= 0)
                    {
                        reachedEnd = true;
                        break;
                    }

                    int readSize = (int)Math.Min(buffer.Length, remaining);

                    int bytesRead = await stream.ReadAsync(
                        buffer.AsMemory(0, readSize),
                        reconnectCancellation.Token);

                    if (bytesRead == 0)
                    {
                        reachedEnd = true;
                        break;
                    }

                    position += bytesRead;
                    sampler.AddBytes(bytesRead);

                    remaining -= bytesRead;
                    if (remaining <= 0)
                    {
                        reachedEnd = true;
                        break;
                    }
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
                DisconnectNetworkConnection(settings.SourcePath);
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

    private static void DisconnectNetworkConnection(string sourcePath)
    {
        string? remoteName = GetRemoteName(sourcePath);
        if (string.IsNullOrWhiteSpace(remoteName))
            return;

        _ = WNetCancelConnection2(remoteName, 0, false);
    }

    private static string? GetRemoteName(string sourcePath)
    {
        string path = sourcePath.Trim();

        if (path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            string[] segments = path.Split('\\', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length >= 2)
                return $@"\\{segments[0]}\{segments[1]}";

            return null;
        }

        if (path.Length >= 2 && path[1] == ':')
        {
            string localName = path[..2];
            var remoteName = new StringBuilder(260);
            int remoteNameLength = remoteName.Capacity;
            int result = WNetGetConnection(localName, remoteName, ref remoteNameLength);

            if (result == 0 && remoteNameLength > 0)
                return remoteName.ToString();
        }

        return null;
    }

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetCancelConnection2(
        string lpName,
        int dwFlags,
        bool fForce);

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetGetConnection(
        string lpLocalName,
        StringBuilder lpRemoteName,
        ref int lpnLength);

    private static NetworkInterfaceState GetNetworkInterfaceState()
    {
        bool ethernetAvailable = false;
        bool wirelessAvailable = false;
        var topology = new List<string>();

        foreach (NetworkInterface networkInterface in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (networkInterface.OperationalStatus != OperationalStatus.Up)
                continue;

            if (networkInterface.NetworkInterfaceType == NetworkInterfaceType.Ethernet)
                ethernetAvailable = true;
            else if (networkInterface.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
                wirelessAvailable = true;

            if (networkInterface.NetworkInterfaceType is
                NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211)
            {
                try
                {
                    IPInterfaceProperties properties = networkInterface.GetIPProperties();
                    string addresses = string.Join(",", properties.UnicastAddresses
                        .Select(address => address.Address.ToString())
                        .OrderBy(address => address));
                    string gateways = string.Join(",", properties.GatewayAddresses
                        .Select(gateway => gateway.Address.ToString())
                        .OrderBy(address => address));

                    topology.Add($"{networkInterface.Id}:{addresses}:{gateways}");
                }
                catch (NetworkInformationException)
                {
                    topology.Add(networkInterface.Id);
                }
            }
        }

        return new NetworkInterfaceState(
            ethernetAvailable,
            wirelessAvailable,
            string.Join("|", topology.OrderBy(value => value)));
    }

    private readonly record struct NetworkInterfaceState(
        bool EthernetAvailable,
        bool WirelessAvailable,
        string Topology);

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
