using System.Security.Cryptography;
using System.Text;

namespace ImaginedNT.Core.Runtime;

/// <summary>来宾的模拟包身份；路径使用来宾可见的 Windows 路径。</summary>
public sealed class PackageIdentity
{
    public string Name { get; init; } = "ImaginedNT.Application";
    public string Publisher { get; init; } = "CN=ImaginedNT";
    public string? PublisherId { get; init; }
    public Version Version { get; init; } = new(1, 0, 0, 0);
    public string Architecture { get; init; } = "x64";
    public string ResourceId { get; init; } = "";
    public string InstallPath { get; init; } = @"C:\Program Files\WindowsApps\ImaginedNT.Application";
    public string ApplicationId { get; init; } = "App";

    public string EffectivePublisherId
    {
        get
        {
            if (PublisherId is not null) return PublisherId;
            var hash = SHA256.HashData(Encoding.Unicode.GetBytes(Publisher));
            const string alphabet = "0123456789abcdefghjkmnpqrstvwxyz";
            var result = new StringBuilder(13);
            for (var i = 0; i < 13; i++)
            {
                var value = 0;
                for (var j = 0; j < 5; j++)
                {
                    var bit = i * 5 + j;
                    value = (value << 1) | (bit < 64 ? (hash[bit / 8] >> (7 - bit % 8)) & 1 : 0);
                }
                result.Append(alphabet[value]);
            }
            return result.ToString();
        }
    }

    public string FullName => $"{Name}_{Version}_{Architecture.ToLowerInvariant()}_{ResourceId}_{EffectivePublisherId}";
    public string FamilyName => $"{Name}_{EffectivePublisherId}";
    public string ApplicationUserModelId => $"{FamilyName}!{ApplicationId}";
    public uint ProcessorArchitecture => Architecture.ToLowerInvariant() switch
    {
        "x86" => 0, "arm" => 5, "x64" => 9, "neutral" => 11, "arm64" => 12,
        _ => throw new ArgumentException($"unsupported package architecture: {Architecture}")
    };

    internal void Validate()
    {
        if (Name is null || Name.Length is < 3 or > 50 || Name.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '.' && c != '-') ||
            string.IsNullOrWhiteSpace(Publisher) || Publisher.Contains('\0') ||
            string.IsNullOrWhiteSpace(InstallPath) || InstallPath.Contains('\0') ||
            string.IsNullOrWhiteSpace(Architecture) ||
            string.IsNullOrWhiteSpace(ApplicationId) || ApplicationId.Length > 64 ||
            ApplicationId.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '.') ||
            ResourceId is null || ResourceId.Length > 30 || ResourceId.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '.' && c != '-') ||
            EffectivePublisherId.Length != 13 ||
            EffectivePublisherId.Any(c => !"0123456789abcdefghjkmnpqrstvwxyz".Contains(c)) ||
            Version is null ||
            Version.Major > ushort.MaxValue || Version.Minor > ushort.MaxValue ||
            Version.Build is < 0 or > ushort.MaxValue || Version.Revision is < 0 or > ushort.MaxValue)
            throw new ArgumentException("invalid simulated package identity");
        _ = ProcessorArchitecture;
    }
}
