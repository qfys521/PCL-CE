using System;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using PCL.Core.Utils;

namespace PCL.Core.Minecraft.Java.Parser;

/// <summary>Reads Java's own properties instead of depending on Windows PE version resources.</summary>
public sealed class CommandJavaParser : IJavaParser
{
    public Result<JavaInstallation, Exception> Parse(string javaExePath)
    {
        try
        {
            var file = new FileInfo(Path.GetFullPath(javaExePath));
            if (!file.Exists) throw new FileNotFoundException("Java 可执行文件不存在。", javaExePath);
            var resolved = file.ResolveLinkTarget(true)?.FullName ?? file.FullName;
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo(resolved)
                {
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    ArgumentList = { "-XshowSettings:properties", "-version" }
                }
            };
            process.Start();
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(10000))
            {
                process.Kill(true);
                throw new TimeoutException("Java 版本检测超时。");
            }
            Task.WaitAll([output, error], TimeSpan.FromSeconds(2));
            if (!output.IsCompletedSuccessfully || !error.IsCompletedSuccessfully)
                throw new TimeoutException("Java 版本信息读取超时。");
            if (process.ExitCode != 0) throw new InvalidOperationException("Java 无法正常运行。");
            var text = output.Result + error.Result;
            string Property(string name) => Regex.Match(text, @"(?m)^\s*" + Regex.Escape(name) + @"\s*=\s*(.+)$").Groups[1].Value.Trim();
            var numeric = Regex.Match(Property("java.version"), @"^\d+(?:\.\d+){0,2}(?:_\d+)?").Value.Replace('_', '.');
            if (!numeric.Contains('.')) numeric += ".0";
            var version = Version.Parse(numeric);
            var arch = Property("os.arch") switch
            {
                "amd64" or "x86_64" => MachineType.AMD64,
                "aarch64" or "arm64" => MachineType.ARM64,
                "x86" or "i386" or "i686" => MachineType.I386,
                _ => throw new PlatformNotSupportedException("无法识别 Java 的 CPU 架构。")
            };
            var bin = Path.GetDirectoryName(resolved)!;
            return Result<JavaInstallation, Exception>.Ok(new(bin, version, JavaBrandType.Unknown, arch,
                arch is MachineType.AMD64 or MachineType.ARM64, !File.Exists(Path.Combine(bin, "javac"))));
        }
        catch (Exception exception)
        {
            return Result<JavaInstallation, Exception>.Err(exception);
        }
    }
}
