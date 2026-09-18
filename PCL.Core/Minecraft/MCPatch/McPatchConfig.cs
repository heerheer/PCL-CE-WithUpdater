using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace PCL.Core.Minecraft.MCPatch;

public static class McPatchConfig
{
    public const string ConfigFileName = "mcpatch.config.json";

    /// <summary>
    /// 读取端点配置（蓝图 §2.1）。
    /// 返回是否找到配置文件（UI 据此决定卡片显隐）；endpoints 永不为空。
    /// 定位：优先实例路径；若实例路径与根路径不同且根路径存在该文件，则改用根路径的。
    /// 解析失败 → 回退一个默认端点（仍返回 true）。
    /// </summary>
    public static bool TryLoadEndpoints(string instancePath, string rootPath, out List<McPatchEndpoint> endpoints)
    {
        var instanceFile = Path.Combine(instancePath, ConfigFileName);
        var rootFile = Path.Combine(rootPath, ConfigFileName);
        var configPath =
            !string.Equals(instancePath, rootPath, StringComparison.OrdinalIgnoreCase) && File.Exists(rootFile)
                ? rootFile
                : instanceFile;
        if (!File.Exists(configPath))
        {
            endpoints = [new McPatchEndpoint()];
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(configPath));
            endpoints = [];
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in doc.RootElement.EnumerateArray())
                    if (item.ValueKind == JsonValueKind.Object)
                        endpoints.Add(ParseEndpoint(item));
            }
            else if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                endpoints.Add(ParseEndpoint(doc.RootElement));
            }
            if (endpoints.Count == 0) endpoints.Add(new McPatchEndpoint());
            return true;
        }
        catch
        {
            endpoints = [new McPatchEndpoint()];
            return true;
        }
    }

    private static McPatchEndpoint ParseEndpoint(JsonElement element)
    {
        string Get(string key) =>
            element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? ""
                : "";
        var versionListUrl = Get("versionListUrl");
        var packageUrlTemplate = Get("packageUrlTemplate");
        return new McPatchEndpoint
        {
            Name = Get("name"),
            VersionListUrl = versionListUrl.Length > 0 ? versionListUrl : McPatchEndpoint.DefaultVersionListUrl,
            PackageUrlTemplate = packageUrlTemplate.Length > 0
                ? packageUrlTemplate
                : McPatchEndpoint.DefaultPackageUrlTemplate
        };
    }

    /// <summary>按行清洗版本列表：去 \r、去空白、去空行、按序去重（蓝图 §2.1）。</summary>
    public static List<string> ParseVersionList(string content)
    {
        var result = new List<string>();
        foreach (var raw in content.Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || result.Contains(line)) continue;
            result.Add(line);
        }
        return result;
    }

    /// <summary>
    /// 待更新链（蓝图 §3.6）：当前为空 → 全部；命中末项 → 空；命中中间 → 其后缀；未命中 → 全部。
    /// </summary>
    public static List<string> ComputeUpdateChain(string currentVersion, IReadOnlyList<string> allVersions)
    {
        if (allVersions.Count == 0) return [];
        if (string.IsNullOrEmpty(currentVersion)) return [.. allVersions];
        var index = -1;
        for (var i = 0; i < allVersions.Count; i++)
            if (string.Equals(allVersions[i], currentVersion, StringComparison.Ordinal))
            {
                index = i;
                break;
            }
        if (index < 0) return [.. allVersions];
        if (index == allVersions.Count - 1) return [];
        return allVersions.Skip(index + 1).ToList();
    }
}