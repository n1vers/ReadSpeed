
using System;
using System.IO;

namespace StorageBench.Core.Services;

public enum SourceType
{
    Local,
    Network,
    Internet,
    Unknown
}

public static class SourceDetector
{
    public static SourceType Detect(string source)
    {
        if (string.IsNullOrWhiteSpace(source))
            return SourceType.Unknown;

        source = source.Trim();

        if (Uri.TryCreate(source, UriKind.Absolute, out Uri? uri))
        {
            if (uri.Scheme == Uri.UriSchemeHttp ||
                uri.Scheme == Uri.UriSchemeHttps)
            {
                return SourceType.Internet;
            }
        }

        if (source.StartsWith(@"\\", StringComparison.Ordinal))
            return SourceType.Network;

        if (Path.IsPathRooted(source))
        {
            string? root = Path.GetPathRoot(source);

            if (root is not null)
            {
                try
                {
                    if (new DriveInfo(root).DriveType == DriveType.Network)
                        return SourceType.Network;
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }

            if (File.Exists(source))
                return SourceType.Local;
        }

        return SourceType.Unknown;
    }
}
