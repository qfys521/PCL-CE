using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace PCL.Core.App.Region;

public enum RegionClassification
{
    Unknown,
    Other,
    Mainland
}

public sealed record RegionSignals(
    string? GeoName,
    bool GeoNameAvailable,
    string? TimeZoneId,
    bool TimeZoneAvailable);

public sealed record RegionAssessment(RegionClassification Classification, string Reason)
{
    public bool IsRestricted => Classification == RegionClassification.Mainland;
}

public static partial class RegionPolicy
{
    private static readonly HashSet<string> ChinaTimeZones = new(StringComparer.OrdinalIgnoreCase)
    {
        "China Standard Time",
        "Asia/Shanghai",
        "PRC"
    };

    /// <summary>
    /// 区域限制是否在当前平台生效。区域限制是 Windows 客户端的历史行为；
    /// Linux（PCL-CE-Linux）核心永远不启用区域限制，Minecraft 下载/补全/整合包安装保持可用。
    /// </summary>
    public static bool IsRestrictionSupported => OperatingSystem.IsWindows();

    /// <summary>
    /// 当前运行环境的区域判定。非 Windows 平台直接返回不受限，且不读取任何 Windows 区域 API。
    /// </summary>
    public static RegionAssessment AssessCurrent() => OperatingSystem.IsWindows()
        ? Evaluate(ReadWindowsSignals())
        : new RegionAssessment(RegionClassification.Other, "non-windows-unrestricted");

    public static RegionAssessment Evaluate(RegionSignals signals)
    {
        ArgumentNullException.ThrowIfNull(signals);

        // 仅在“时区为中国大陆”且“设备地区为中国大陆”时启用限制。
        // 设备地区取系统“国家或地区”设置，而不是 OOBE 时写入的设备安装地区。
        // 任一项无法确认或不为中国大陆，均不限制。
        var chinaTimeZone = signals.TimeZoneAvailable &&
                            signals.TimeZoneId is not null &&
                            ChinaTimeZones.Contains(signals.TimeZoneId);
        var chinaRegion = signals.GeoNameAvailable && IsChinaGeoName(signals.GeoName);

        if (chinaTimeZone && chinaRegion)
            return new RegionAssessment(RegionClassification.Mainland, "china-time-zone-and-region");

        if (!signals.TimeZoneAvailable || !signals.GeoNameAvailable)
            return new RegionAssessment(RegionClassification.Unknown, "signals-unavailable");

        return new RegionAssessment(RegionClassification.Other, "not-china-time-zone-and-region");
    }

    public static RegionSignals ReadWindowsSignals()
    {
        if (!OperatingSystem.IsWindows())
        {
            // Linux 等非 Windows 平台没有 GetUserDefaultGeoName / GetUserGeoID：
            // 明确返回“地理信息不可用”，Evaluate 因此永远不会判定为受限区域。
            try
            {
                var localTimeZone = TimeZoneInfo.Local.Id;
                return new RegionSignals(null, false, localTimeZone, !string.IsNullOrWhiteSpace(localTimeZone));
            }
            catch (Exception)
            {
                return new RegionSignals(null, false, null, false);
            }
        }

        var geoName = TryReadGeoName(out var geoAvailable);
        string? timeZone = null;
        var timeZoneAvailable = false;
        try
        {
            timeZone = TimeZoneInfo.Local.Id;
            timeZoneAvailable = !string.IsNullOrWhiteSpace(timeZone);
        }
        catch
        {
        }

        return new RegionSignals(geoName, geoAvailable, timeZone, timeZoneAvailable);
    }

    private static bool IsChinaGeoName(string? name) =>
        string.Equals(name, "CN", StringComparison.OrdinalIgnoreCase) || name == "156";

    private static unsafe string? TryReadGeoName(out bool available)
    {
        available = false;
        try
        {
            var buffer = new char[16];
            int length;
            fixed (char* bufferPointer = buffer)
                length = GetUserDefaultGeoName(bufferPointer, buffer.Length);
            if (length > 1 && length <= buffer.Length)
            {
                available = true;
                return new string(buffer, 0, length - 1);
            }
            return TryReadLegacyGeoName(out available);
        }
        catch
        {
            return TryReadLegacyGeoName(out available);
        }
    }

    private static unsafe string? TryReadLegacyGeoName(out bool available)
    {
        available = false;
        try
        {
            var geoId = GetUserGeoID(16); // GEOCLASS_NATION
            if (geoId <= 0) return null;
            var buffer = new char[16];
            int length;
            fixed (char* bufferPointer = buffer)
                length = GetGeoInfoW(geoId, 4, bufferPointer, buffer.Length, 0); // GEO_ISO2
            if (length <= 1 || length > buffer.Length) return null;
            available = true;
            return new string(buffer, 0, length - 1);
        }
        catch
        {
            return null;
        }
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetUserDefaultGeoName")]
    private static unsafe partial int GetUserDefaultGeoName(char* geoName, int geoNameCount);

    [LibraryImport("kernel32.dll", EntryPoint = "GetUserGeoID")]
    private static partial int GetUserGeoID(int geoClass);

    [LibraryImport("kernel32.dll", EntryPoint = "GetGeoInfoW")]
    private static unsafe partial int GetGeoInfoW(int location, int geoType, char* geoData, int dataSize, int flags);
}
