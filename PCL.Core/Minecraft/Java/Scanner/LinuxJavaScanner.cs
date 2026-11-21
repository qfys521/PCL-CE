using System;
using System.Collections.Generic;
using System.IO;

namespace PCL.Core.Minecraft.Java.Scanner;

public sealed class LinuxJavaScanner : IJavaScanner
{
    public void Scan(ICollection<string> results)
    {
        if (!OperatingSystem.IsLinux()) return;
        new PathEnvironmentScanner().Scan(results);
        if (Environment.GetEnvironmentVariable("JAVA_HOME") is { } home) Add(Path.Combine(home, "bin", "java"));
        foreach (var root in new[] { "/usr/lib/jvm", "/usr/java", "/opt/java", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".jdks") })
        {
            if (!Directory.Exists(root)) continue;
            try
            {
                foreach (var directory in Directory.EnumerateDirectories(root)) Add(Path.Combine(directory, "bin", "java"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        void Add(string path) { if (File.Exists(path)) results.Add(path); }
    }
}
