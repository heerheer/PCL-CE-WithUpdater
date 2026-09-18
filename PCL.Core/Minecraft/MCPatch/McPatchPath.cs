using System;
using System.IO;

namespace PCL.Core.Minecraft.MCPatch;

public static class McPatchPath
{
    public const string VersionFileName = "mc-patch-version.txt";

    /// <summary>
    /// 逻辑路径 → 真实路径（蓝图 §4.2）：
    /// 统一分隔符 → 去前导 \ → 去 .minecraft\versions\{版本名}\ 前缀 → 去 .minecraft\ 前缀 → 拼根路径。
    /// 结果必须位于根路径之下，否则视为路径穿越。
    /// </summary>
    public static string MapRealPath(string rootPath, string selectedVersionName, string logicalPath)
    {
        var relative = logicalPath.Replace('/', '\\').TrimStart('\\');
        if (!string.IsNullOrEmpty(selectedVersionName))
        {
            var versionPrefix = $".minecraft\\versions\\{selectedVersionName}\\";
            if (relative.StartsWith(versionPrefix, StringComparison.OrdinalIgnoreCase))
                relative = relative[versionPrefix.Length..];
            else if (relative.StartsWith(".minecraft\\", StringComparison.OrdinalIgnoreCase))
                relative = relative[".minecraft\\".Length..];
        }
        else if (relative.StartsWith(".minecraft\\", StringComparison.OrdinalIgnoreCase))
        {
            relative = relative[".minecraft\\".Length..];
        }

        var fullPath = Path.GetFullPath(Path.Combine(rootPath, relative));
        var fullRoot = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar) +
                       Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            throw new McPatchException("非法路径（可能存在路径穿越）：" + logicalPath);
        return fullPath;
    }

    public static string GetVersionFilePath(string rootPath) => Path.Combine(rootPath, VersionFileName);

    /// <summary>读取当前已应用版本；不存在 / 空 → ""（视为未安装）。</summary>
    public static string ReadCurrentVersion(string rootPath)
    {
        try
        {
            return (File.ReadAllText(GetVersionFilePath(rootPath)) ?? "").Trim();
        }
        catch
        {
            return "";
        }
    }

    public static void WriteCurrentVersion(string rootPath, string version)
    {
        File.WriteAllText(GetVersionFilePath(rootPath), version);
    }
}