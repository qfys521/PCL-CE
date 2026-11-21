using System;
#if WINDOWS
using System.Drawing;
#endif
using System.IO;
using PCL.Core.App;

namespace PCL.Core.Utils;

public static class IconHelper
{
    public static string GetIconPath()
    {
        var paths = Path.Combine(Paths.Temp, "icon.png");
        if (!File.Exists(paths))
        {
            CreateIcon();
        }

        return paths;
    }

    private static void CreateIcon()
    {
#if WINDOWS
        using var icon = Icon.ExtractAssociatedIcon(Basics.ExecutablePath) ??
                         throw new InvalidOperationException("无法提取程序图标。");
        using var bitmap = icon.ToBitmap();
        bitmap.Save(Path.Combine(Paths.Temp, "icon.png"));
#else
        using var source = Basics.GetResourceStream("Images/icon.png") ??
                           throw new FileNotFoundException("应用未提供 Images/icon.png 图标资源。");
        using var target = File.Create(Path.Combine(Paths.Temp, "icon.png"));
        source.CopyTo(target);
#endif
    }
}
