using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using StorageBench.Core.Interfaces;
using StorageBench.Core.Models;
using StorageBench.Core.Services;
using StorageBench.Infrastructure;

namespace StorageBench.Engines;

public class LocalReadEngine : IReadEngine
{
    public Task<BenchmarkResult> RunAsync(
        BenchmarkSettings settings,
        IProgress<ReadProgress>? progress,
        CancellationToken cancellationToken)
    {
        return Task.Run(
            () => RunInternal(settings, progress, cancellationToken),
            cancellationToken);
    }

    private BenchmarkResult RunInternal(
        BenchmarkSettings settings,
        IProgress<ReadProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(settings.SourcePath))
            throw new ArgumentException("File path is empty.");

        string path = Path.GetFullPath(settings.SourcePath);

        if (path.StartsWith(@"\\"))
            throw new NotSupportedException(
                "Use NetworkReadEngine for network paths.");

        if (!File.Exists(path))
            throw new FileNotFoundException(
                "Source file not found.", path);

        if (settings.BufferSize <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(settings.BufferSize));

        // Determine volume sector size.
        var volume = new System.Text.StringBuilder(260);

        if (!NativeMethods.GetVolumePathNameW(
                path, volume, (uint)volume.Capacity))
            throw new Win32Exception(Marshal.GetLastWin32Error());

        if (!NativeMethods.GetDiskFreeSpaceW(
                volume.ToString(),
                out _,
                out uint sectorSize,
                out _,
                out _))
            throw new Win32Exception(Marshal.GetLastWin32Error());

        if (sectorSize == 0)
            throw new InvalidOperationException(
                "Unable to determine sector size.");

        // Align buffer size to sector boundaries.
        long requestedSize = checked((long)settings.BufferSize * 1024);

        long alignedSize = ((requestedSize + sectorSize - 1)
                            / sectorSize) * sectorSize;

        if (alignedSize > int.MaxValue)
            throw new ArgumentOutOfRangeException(
                nameof(settings.BufferSize));

        uint bufferSize = (uint)alignedSize;

        using var handle = NativeMethods.CreateFileW(
            path,
            NativeMethods.GENERIC_READ,
            NativeMethods.FILE_SHARE_READ |
            NativeMethods.FILE_SHARE_WRITE |
            NativeMethods.FILE_SHARE_DELETE,
            IntPtr.Zero,
            NativeMethods.OPEN_EXISTING,
            NativeMethods.FILE_FLAG_NO_BUFFERING |
            NativeMethods.FILE_FLAG_SEQUENTIAL_SCAN,
            IntPtr.Zero);

        if (handle.IsInvalid)
            throw new Win32Exception(Marshal.GetLastWin32Error());

        if (!NativeMethods.GetFileSizeEx(handle, out long fileSize))
            throw new Win32Exception(Marshal.GetLastWin32Error());

        // Only complete sectors can be read with unbuffered I/O.
        long readableSize = fileSize - (fileSize % sectorSize);

        if (readableSize == 0)
            throw new InvalidOperationException(
                "File is too small for unbuffered sector-aligned reading.");

        IntPtr buffer = NativeMethods.VirtualAlloc(
            IntPtr.Zero,
            (UIntPtr)bufferSize,
            NativeMethods.MEM_COMMIT | NativeMethods.MEM_RESERVE,
            NativeMethods.PAGE_READWRITE);

        if (buffer == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error());

        try
        {
            BenchmarkResult result = ReadFileSequentially(
                handle,
                buffer,
                bufferSize,
                readableSize,
                settings,
                progress,
                cancellationToken);

            result.Source = settings.SourcePath;
            result.SourceKind = nameof(SourceType.Local);
            result.TestType = "Последовательное чтение (без кэша Windows)";
            result.FileSizeBytes = fileSize;
            result.BufferSizeKB = (int)(bufferSize / 1024);

            return result;
        }
        finally
        {
            NativeMethods.VirtualFree(
                buffer,
                UIntPtr.Zero,
                NativeMethods.MEM_RELEASE);
        }
    }

    private static BenchmarkResult ReadFileSequentially(
        SafeFileHandle handle,
        IntPtr buffer,
        uint bufferSize,
        long readableSize,
        BenchmarkSettings settings,
        IProgress<ReadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var sampler = new SpeedSampler(progress);

        while (sampler.TotalBytes < readableSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            settings.PauseController?
                .WaitIfPausedAsync(cancellationToken)
                .GetAwaiter()
                .GetResult();

            long remaining = readableSize - sampler.TotalBytes;

            uint bytesToRead = (uint)Math.Min(
                bufferSize,
                remaining);

            if (!NativeMethods.ReadFile(
                    handle,
                    buffer,
                    bytesToRead,
                    out uint bytesRead,
                    IntPtr.Zero))
            {
                int error = Marshal.GetLastWin32Error();

                if (error == 38) // ERROR_HANDLE_EOF
                    break;

                throw new Win32Exception(error);
            }

            if (bytesRead == 0)
                break;

            sampler.AddBytes(bytesRead);
        }

        BenchmarkResult result = sampler.Finish();

        result.ReachedEnd = result.TotalBytesRead >= readableSize;
        return result;
    }
}
