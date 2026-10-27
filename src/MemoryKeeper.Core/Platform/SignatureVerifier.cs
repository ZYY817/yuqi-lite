using System.Collections.Concurrent;
using System.Security.Cryptography.X509Certificates;
using MemoryKeeper.Contracts;

namespace MemoryKeeper.Core.Platform;

/// <summary>读取 exe 的 Authenticode 签名。结果按路径缓存。</summary>
public sealed class SignatureVerifier : ISignatureVerifier
{
    private readonly ConcurrentDictionary<string, string?> _cache = new(StringComparer.OrdinalIgnoreCase);

    public string? GetVerifiedPublisher(string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return null;
        return _cache.GetOrAdd(path, ReadPublisher);
    }

    public bool IsMicrosoftSigned(string path)
    {
        var publisher = GetVerifiedPublisher(path);
        return publisher?.Contains("Microsoft Corporation", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static string? ReadPublisher(string path)
    {
        try
        {
            var cert = X509Certificate.CreateFromSignedFile(path);
            var subject = cert.Subject;
            var idx = subject.IndexOf("CN=", StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return null;
            var rest = subject[(idx + 3)..].TrimStart();
            if (rest.StartsWith('"'))
            {
                var endQ = rest.IndexOf('"', 1);
                return endQ > 1 ? rest[1..endQ].Trim() : rest.Trim('"');
            }
            var comma = rest.IndexOf(',');
            return (comma > 0 ? rest[..comma] : rest).Trim('"', ' ');
        }
        catch
        {
            return null;
        }
    }
}
