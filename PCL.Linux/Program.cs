using System.Net;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Threading;
using PCL.Core.IO.Net;
using PCL.Core.Minecraft.Java.Parser;
using PCL.Core.Minecraft.Java.Scanner;
using PCL.Portable;

[assembly: SupportedOSPlatform("linux")]

namespace PCL.Linux;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            Console.Error.WriteLine("此预览构建仅支持 Linux x86_64。");
            return 1;
        }
        try
        {
            using var networkClients = new LinuxHttpClientFactory();
            NetworkService.ConfigureClientFactory(networkClients);
            if (args.Contains("--help"))
            {
                Console.WriteLine("PCL CE Linux 0.2.1-preview\n直接运行：打开图形界面\n--diagnose：检测 Java 和本地版本\n--list：列出 Mojang 正式版\n--smoke-test：检查全部界面页面和动画后自动退出（不联网、不启动游戏）");
                return 0;
            }
            if (args.Contains("--diagnose"))
            {
                Console.WriteLine($"PCL CE Linux | {RuntimeInformation.OSDescription} | {RuntimeInformation.ProcessArchitecture}");
                var paths = new List<string>();
                new LinuxJavaScanner().Scan(paths);
                foreach (var path in paths.Distinct(StringComparer.Ordinal))
                {
                    var result = new CommandJavaParser().Parse(path);
                    Console.WriteLine(result.IsSuccess ? result.Value.ToDetailedString() : $"{path}: {result.Error.Message}");
                }
                var settings = LinuxSettings.Load();
                Console.WriteLine($"游戏目录：{settings.GameDirectory}");
                using var client = LinuxHttpClientFactory.Create();
                foreach (var version in new LinuxGameLauncher(settings.GameDirectory, client).GetInstalledVersions()) Console.WriteLine(version);
                return 0;
            }
            if (args.Contains("--list"))
            {
                using var client = LinuxHttpClientFactory.Create();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
                var releases = new LinuxGameLauncher(LinuxSettings.Load().GameDirectory, client).GetReleasesAsync(timeout.Token).GetAwaiter().GetResult();
                foreach (var release in releases) Console.WriteLine(release.Id);
                return 0;
            }
            return AppBuilder.Configure<LinuxApp>().UsePlatformDetect().LogToTrace().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"PCL CE Linux 启动失败：{exception.Message}");
            return 1;
        }
    }
}

internal sealed class LinuxApp : Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        Styles.Add(new StyleInclude(new Uri("avares://PCL-CE/")) { Source = new Uri("avares://PCL-CE/Themes/PclTheme.axaml") });
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var smoke = desktop.Args?.Contains("--smoke-test") == true;
            var window = new MainWindow(smoke);
            desktop.MainWindow = window;
            if (smoke)
            {
                window.Opened += async (_, _) =>
                {
                    try { await window.VerifyPagesAsync(); desktop.Shutdown(0); }
                    catch (Exception ex) { Console.Error.WriteLine("界面检查失败：" + ex); desktop.Shutdown(1); }
                };
            }
        }
        base.OnFrameworkInitializationCompleted();
    }
}

internal sealed class LinuxHttpClientFactory : IHttpClientFactory, IDisposable
{
    private readonly HttpClient _client = Create();
    public HttpClient CreateClient(string name) => _client;
    public void Dispose() => _client.Dispose();
    public static HttpClient Create()
    {
        var client = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(20)
        }) { Timeout = TimeSpan.FromMinutes(3) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("PCL-CE-Linux/0.2.1");
        return client;
    }
}
