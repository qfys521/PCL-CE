using System.Diagnostics;

namespace PCL.Portable;

public sealed record ProcessResult(int ExitCode, string Output);

public static class PortableProcess
{
    public static async Task<ProcessResult> RunAsync(ProcessStartInfo start, CancellationToken token)
    {
        start.UseShellExecute = false;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        token.ThrowIfCancellationRequested();
        using var process = Process.Start(start) ?? throw new IOException($"无法启动 {start.FileName}");
        using var stop = token.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(true); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        });
        var lines = new Queue<string>();
        async Task ReadAsync(StreamReader reader)
        {
            while (await reader.ReadLineAsync() is { } line)
                lock (lines)
                {
                    lines.Enqueue(line.Length > 2000 ? line[..2000] : line);
                    while (lines.Count > 150) lines.Dequeue();
                }
        }
        await Task.WhenAll(ReadAsync(process.StandardOutput), ReadAsync(process.StandardError), process.WaitForExitAsync());
        token.ThrowIfCancellationRequested();
        return new(process.ExitCode, string.Join(Environment.NewLine, lines));
    }
}
