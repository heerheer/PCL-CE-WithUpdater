using System;
using System.Collections.Generic;

namespace PCL.Core.Updater;

public enum McPatchListStatus
{
    FirstFetching,
    Retrying,
    Succeeded,
    Failed
}

public sealed class McPatchEndpointOptions
{
    public const string DefaultVersionListUrl = "http://s13.yxsjmc.cn:20125/versions.txt";
    public const string DefaultPackageUrlTemplate = "http://s13.yxsjmc.cn:20125/{version}.mcpatch.zip";

    public string VersionListUrl { get; set; } = DefaultVersionListUrl;

    public string PackageUrlTemplate { get; set; } = DefaultPackageUrlTemplate;
}

public sealed class McPatchUpdateContext
{
    public string MinecraftRootPath { get; set; } = string.Empty;

    public string SelectedVersionName { get; set; } = string.Empty;

    public string SelectedVersionPath { get; set; } = string.Empty;

    public string VersionStateRootPath { get; set; } = string.Empty;

    public McPatchEndpointOptions Endpoints { get; set; } = new();
}

public sealed class McPatchCheckResult
{
    public string CurrentVersion { get; set; } = string.Empty;

    public string LatestVersion { get; set; } = string.Empty;

    public List<string> AllVersions { get; set; } = new();

    public List<string> PendingVersions { get; set; } = new();

    public bool NeedUpdate => PendingVersions.Count > 0;
}
