using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace PCL.Core.Minecraft.MCPatch;

/// <summary>MCPatch 更新源端点（蓝图 §2）。</summary>
public sealed class McPatchEndpoint
{
    public const string DefaultVersionListUrl = "http://s13.yxsjmc.cn:20125/versions.txt";
    public const string DefaultPackageUrlTemplate = "http://s13.yxsjmc.cn:20125/{version}.mcpatch.zip";

    public string Name { get; init; } = "";
    public string VersionListUrl { get; init; } = DefaultVersionListUrl;
    public string PackageUrlTemplate { get; init; } = DefaultPackageUrlTemplate;

    /// <summary>UI 展示名：name → URL host(+path) → 「默认更新源」。</summary>
    public string DisplayName
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Name)) return Name;
            if (Uri.TryCreate(VersionListUrl, UriKind.Absolute, out var uri))
            {
                var path = uri.AbsolutePath.TrimEnd('/');
                return uri.Host + (path.Length > 0 ? path : "");
            }
            return "默认更新源";
        }
    }

    /// <summary>实例键 / 端点签名用。</summary>
    public string Key => VersionListUrl + "|" + PackageUrlTemplate;
}

/// <summary>一次检查 / 更新的全部输入（蓝图 §2）。</summary>
public sealed class McPatchContext
{
    /// <summary>逻辑根路径（= PathIndie，mods 等落盘位置），以 \ 结尾。</summary>
    public required string RootPath { get; init; }

    /// <summary>实例文件夹（= PathInstance），用于定位 mcpatch.config.json。</summary>
    public required string InstancePath { get; init; }

    public required string SelectedVersionName { get; init; }
    public required IReadOnlyList<McPatchEndpoint> Endpoints { get; init; }
    public int SelectedEndpointIndex { get; init; }
    public string TempDirectory { get; init; } = "";

    public McPatchEndpoint SelectedEndpoint =>
        Endpoints[Math.Clamp(SelectedEndpointIndex, 0, Endpoints.Count - 1)];
}

/// <summary>检查结果（蓝图 §2 / §3）。</summary>
public sealed class McPatchCheckResult
{
    /// <summary>当前版本，空串 = 未安装。</summary>
    public required string CurrentVersion { get; init; }
    public required string LatestVersion { get; init; }
    public required IReadOnlyList<string> AllVersions { get; init; }
    /// <summary>有序待更新版本链。</summary>
    public required IReadOnlyList<string> UpdateChain { get; init; }
    public bool NeedUpdate => UpdateChain.Count > 0;
}

/// <summary>进度模型：总进度 + 当前补丁包进度 + 消息（蓝图 §2）。</summary>
public sealed class McPatchProgress
{
    /// <summary>总进度 0~1。</summary>
    public double Overall { get; init; }
    /// <summary>包内进度百分比，null = 未知（无 Content-Length）。</summary>
    public int? PackagePercent { get; init; }
    public long DownloadedBytes { get; init; }
    /// <summary>总字节，-1 = 未知。</summary>
    public long TotalBytes { get; init; }
    public required string Message { get; init; }
}

/// <summary>版本列表获取状态（蓝图 §3.2）。</summary>
public enum McPatchListStatus
{
    FirstFetch,
    Retrying,
    Success,
    Failed
}

/// <summary>.mcpatch-meta.json（蓝图 附录 A）。</summary>
public sealed class McPatchMeta
{
    [JsonPropertyName("new-folders")] public string[] NewFolders { get; set; } = [];
    [JsonPropertyName("move-files")] public string[] MoveFiles { get; set; } = [];
    [JsonPropertyName("change-logs")] public string[] ChangeLogs { get; set; } = [];
    [JsonPropertyName("old-files")] public string[] OldFiles { get; set; } = [];
    [JsonPropertyName("old-folders")] public string[] OldFolders { get; set; } = [];
    [JsonPropertyName("new-files")] public McPatchNewFile[] NewFiles { get; set; } = [];
}

public sealed class McPatchNewFile
{
    [JsonPropertyName("mode")] public required string Mode { get; set; }
    [JsonPropertyName("path")] public required string Path { get; set; }
    [JsonPropertyName("old-hash")] public string? OldHash { get; set; }
    [JsonPropertyName("new-hash")] public string? NewHash { get; set; }
    [JsonPropertyName("bzipped-hash")] public string? BzippedHash { get; set; }
    [JsonPropertyName("raw-hash")] public string? RawHash { get; set; }
    [JsonPropertyName("raw-length")] public long RawLength { get; set; }
}

/// <summary>面向用户的可读错误。</summary>
public sealed class McPatchException(string message, Exception? innerException = null)
    : Exception(message, innerException);