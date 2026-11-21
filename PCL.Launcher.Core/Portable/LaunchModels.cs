using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace PCL.Portable;

public sealed record GameRelease(string Id, string Type, string Url, string Sha1)
{
    public override string ToString() => Id;
}

public sealed record LaunchAccount(string Name, string Uuid, string AccessToken, string Type = "msa")
{
    public static LaunchAccount Offline(string name)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(name, "^[A-Za-z0-9_]{3,16}$"))
            throw new ArgumentException("离线用户名须为 3–16 位字母、数字或下划线。");
        var hash = MD5.HashData(Encoding.UTF8.GetBytes("OfflinePlayer:" + name));
        hash[6] = (byte)((hash[6] & 0x0f) | 0x30);
        hash[8] = (byte)((hash[8] & 0x3f) | 0x80);
        return new(name, Convert.ToHexString(hash).ToLowerInvariant(), "0", "legacy");
    }
}

public sealed record LaunchSettings(string JavaPath, int MemoryMb, LaunchAccount Account,
    string ExtraJvmArguments = "", string ExtraGameArguments = "", string? InstanceDirectory = null);

public sealed record PreparedGame(ProcessStartInfo StartInfo, string LogPath, int JavaMajorVersion, string NativesDirectory);
