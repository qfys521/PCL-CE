using System.Text.Json;
using PCL.Core.Minecraft.Profile.Models;

namespace PCL.Linux;

public sealed class LinuxSettings
{
    public string GameDirectory { get; set; } = Path.Combine(Xdg("XDG_DATA_HOME", ".local/share"), "pcl-ce", "minecraft");
    public string JavaPath { get; set; } = "/usr/bin/java";
    public int MemoryMb { get; set; } = 2048;
    public string OfflineName { get; set; } = "Player";
    public string? SelectedVersion { get; set; }
    public string MicrosoftClientId { get; set; } = Environment.GetEnvironmentVariable("PCL_MS_CLIENT_ID") ?? "";
    public McProfile? MicrosoftAccount { get; set; }
    public bool UseMicrosoftAccount { get; set; }
    public bool UseAuthlibAccount { get; set; }
    public string AuthlibServer { get; set; } = "";
    public string AuthlibUsername { get; set; } = "";
    public McProfile? AuthlibAccount { get; set; }
    public string JvmArguments { get; set; } = "";
    public string GameArguments { get; set; } = "";
    public string CurseForgeKey { get; set; } = Environment.GetEnvironmentVariable("PCL_CURSEFORGE_API_KEY") ?? "";
    public string HomepagePath { get; set; } = "";
    public string BackgroundPath { get; set; } = "";
    public string AccentColor { get; set; } = "#147AD6";
    public string Theme { get; set; } = "light";
    public bool EnableAnimations { get; set; } = true;
    public string MachineId { get; set; } = Guid.NewGuid().ToString("N");
    public string RelayNodes { get; set; } = "tcp://public.easytier.top:11010;tcp://public2.easytier.cn:54321";

    private static string SettingsDirectory => Path.Combine(Xdg("XDG_CONFIG_HOME", ".config"), "pcl-ce");
    private static string SettingsPath => Path.Combine(SettingsDirectory, "linux.json");

    private static string Xdg(string variable, string fallback) =>
        Environment.GetEnvironmentVariable(variable) is { Length: > 0 } value && Path.IsPathFullyQualified(value)
            ? value
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), fallback);

    public static LinuxSettings Load() => File.Exists(SettingsPath)
        ? JsonSerializer.Deserialize<LinuxSettings>(File.ReadAllText(SettingsPath)) ?? new()
        : new();

    public void Save()
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("此设置存储仅支持 Linux。");
        Directory.CreateDirectory(SettingsDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var temporary = Path.Combine(SettingsDirectory, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, new FileStreamOptions
                   {
                       Mode = FileMode.CreateNew, Access = FileAccess.Write,
                       UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
                   }))
                JsonSerializer.Serialize(stream, this, new JsonSerializerOptions { WriteIndented = true });
            File.Move(temporary, SettingsPath, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
