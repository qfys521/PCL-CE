namespace PCL.Portable;

public sealed record CrashDiagnosis(string Cause, string Advice, IReadOnlyList<string> Evidence);

/// <summary>Shares the original Windows crash rules with native hosts.</summary>
public static class PortableCrashAnalysis
{
    public static IReadOnlyList<CrashDiagnosis> Analyze(string text)
    {
        var logs = new CrashLogSet
        {
            Game = new CrashLogText { Kind = CrashLogKind.Game, Text = text },
            CrashReport = text.Contains("Minecraft Crash Report", StringComparison.Ordinal) ? new() { Kind = CrashLogKind.CrashReport, Text = text } : null,
            HsErr = text.Contains("A fatal error has been detected by the Java Runtime", StringComparison.Ordinal) ? new() { Kind = CrashLogKind.HsErr, Text = text } : null,
            All = text
        };
        return new CrashDetector().Analyze(logs, null).Findings.Select(finding => new CrashDiagnosis(finding.Cause.ToString(), finding.Cause switch
        {
            CrashCause.OutOfMemory or CrashCause.ResourcePackTooLarge => "游戏内存不足，请增加分配内存或减少模组、资源包。",
            CrashCause.JavaTooNew or CrashCause.JavaIncompatible or CrashCause.OldForgeNewJavaIncompatible or CrashCause.ModRequiresJava11 => "Java 与游戏或模组不兼容，请选择此版本要求的 Java。",
            CrashCause.UnsupportedOpenGl or CrashCause.PixelFormatNotSupported or CrashCause.OpenGl1282 => "请检查显卡驱动与 OpenGL 支持，Linux 可更新 Mesa 或显卡厂商驱动。",
            CrashCause.DuplicateMods => "检测到重复模组，请在模组管理中禁用重复文件。",
            CrashCause.MissingDependencyOrWrongMcVersion or CrashCause.IncompatibleMods => "请检查模组的 Minecraft、加载器版本和必要前置依赖。",
            CrashCause.IncompleteForgeInstallation => "加载器安装不完整，请重新安装 Forge。",
            CrashCause.ModConfigCrash => "模组配置文件损坏，请备份后重置对应配置。",
            CrashCause.NoAnalyzableFile or CrashCause.VeryShortOutput => "日志信息不足，请同时检查 latest.log 与 crash-reports。",
            _ => "请根据下面的证据检查对应模组、配置和游戏日志。"
        }, finding.Details)).ToArray();
    }
}
