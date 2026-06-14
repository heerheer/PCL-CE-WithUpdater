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

    public string Name { get; set; } = string.Empty;

    public string VersionListUrl { get; set; } = DefaultVersionListUrl;

    public string PackageUrlTemplate { get; set; } = DefaultPackageUrlTemplate;

    public string DisplayName
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Name))
            {
                return Name.Trim();
            }

            if (Uri.TryCreate(VersionListUrl, UriKind.Absolute, out var uri))
            {
                return string.IsNullOrWhiteSpace(uri.PathAndQuery) ? uri.Host : uri.Host + uri.PathAndQuery;
            }

            return string.IsNullOrWhiteSpace(VersionListUrl) ? "默认更新源" : VersionListUrl.Trim();
        }
    }
}

public sealed class McPatchUpdateContext
{
    public string RootPath { get; set; } = string.Empty;

    public string SelectedVersionName { get; set; } = string.Empty;

    public IReadOnlyList<McPatchEndpointOptions> Endpoints { get; set; } = Array.Empty<McPatchEndpointOptions>();

    public int SelectedEndpointIndex { get; set; }

    public McPatchEndpointOptions SelectedEndpoint
    {
        get
        {
            if (Endpoints.Count == 0)
            {
                return new McPatchEndpointOptions();
            }

            if (SelectedEndpointIndex < 0 || SelectedEndpointIndex >= Endpoints.Count)
            {
                return Endpoints[0];
            }

            return Endpoints[SelectedEndpointIndex];
        }
    }
}

public sealed class McPatchCheckResult
{
    public string CurrentVersion { get; set; } = string.Empty;

    public string LatestVersion { get; set; } = string.Empty;

    public List<string> AllVersions { get; set; } = new();

    public List<string> PendingVersions { get; set; } = new();

    public bool NeedUpdate => PendingVersions.Count > 0;
}

public sealed class McPatchUpdateProgress
{
    public double OverallProgress { get; set; }

    public double? PackageProgress { get; set; }

    public long? PackageDownloadedBytes { get; set; }

    public long? PackageTotalBytes { get; set; }

    public string Message { get; set; } = string.Empty;
}
