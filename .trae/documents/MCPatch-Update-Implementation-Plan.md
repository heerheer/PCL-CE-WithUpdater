# MCPatch 更新功能 · 代码级实施计划（可执行版）

> 依据 `docs/MCPatch-Implementation-Flow.md`（下称「蓝图」）。本计划面向**直接执行的模型**：所有新文件给出完整代码，所有修改给出精确 diff，按 Step 顺序执行即可，无需再做设计决策。
> 分层：核心服务（PCL.Core）→ 页面层（Plain Craft Launcher 2，下称 UI 项目）→ 测试（PCL.Core.Test）。

---

## 0. 执行顺序总览

| Step | 内容 | 产出 | 自检 |
|---|---|---|---|
| 1 | 核心模型 + 配置 + 路径 | 3 个新文件 | `dotnet build PCL.Core` |
| 2 | 核心服务（检查/应用） | 1 个新文件 | 同上 |
| 3 | 单元测试 | 1 个新文件 | `dotnet test --filter McPatch` |
| 4 | 本地化键 | 改 zh-CN.xaml / en-US.xaml | 构建通过 |
| 5 | UI 卡片 + 挂载 | 2 个新文件 + 1 处 XAML 修改 | 构建通过 |
| 6 | 启动门禁联动 | PageLaunchLeft.xaml.cs 3 处修改 | 构建通过 |
| 7 | 非核心改动 ×3 | 3 个文件修改 | 构建通过 + 行为确认 |
| 8 | 总体验证 | — | 见 §10 |

---

## 1. 执行者需知的代码库约定（已核实）

- **PCL.Core**：`Nullable` enable、**ImplicitUsings 未开启**（需写全 using）、文件级命名空间；csproj 用 `Compile Include="**\*.cs"` 通配 → **新 .cs 文件免 csproj 改动**。
- **UI 项目**（namespace `PCL`）：`ImplicitUsings` enable；SDK 默认项 → **新 .cs/.xaml 免 csproj 改动**。
- **测试项目**：MSTest 4.3.0（`[TestClass]`/`[TestMethod]`，参考 [ProfileManagementTest.cs](file:///workspace/PCL.Core.Test/Minecraft/ProfileManagementTest.cs)）。
- `MyButton.Click` 事件参数是 **`MouseButtonEventArgs`**（见 [PageLaunchLeft.xaml](file:///workspace/Plain%20Craft%20Launcher%202/Pages/PageLaunch/PageLaunchLeft.xaml) L27 + code-behind L202）。
- `Lang` 在 `PCL.Core.App.Localization` 命名空间，`Lang.Text("Key", args...)` 支持 `{0}` 格式化。
- `MyComboBoxItem : ComboBoxItem`（[MyComboBoxItem.cs](file:///workspace/Plain%20Craft%20Launcher%202/Controls/MyComboBoxItem.cs)），可直接 `new MyComboBoxItem { Content = ... }`。
- `MyCard` 可含多个子元素（参考 PageLaunchRight.xaml `PanHint` 卡片：MyIconButton + StackPanel）。
- 线程：`ModBase.RunInNewThread(Action, string name)` / `ModBase.RunInUi(Action)`；`ModBase.GetUuid()`；`ModBase.pathTemp`。
- 日志/提示：`ModBase.Log(ex, "中文描述")`；`HintService.Hint(text, HintType.Success/Error)`。
- 选中实例：`ModInstanceList.McMcInstanceSelected`（null = 未选中），`PathInstance`（`versions\<名>\`）、`PathIndie`（隔离后逻辑根，**以 `\` 结尾**）、`Name`（见 [McInstance.cs](file:///workspace/Plain%20Craft%20Launcher%202/Modules/Minecraft/McInstance.cs) L57-128）。
- 游戏运行：`ModWatcher.hasRunningMinecraft`；启动中：`ModLaunch.mcLaunchLoader.State == ModBase.LoadState.Loading`。
- SharpZipLib 已引用（`ICSharpCode.SharpZipLib.BZip2`，[Files.cs](file:///workspace/PCL.Core/IO/Files.cs) L465 已有 `BZip2InputStream` 用法）；zip 读取用 `System.IO.Compression.ZipFile`（McInstance.cs L562 已有先例）。

---

## 2. Step 1 — 核心模型 / 配置 / 路径

新建目录 `PCL.Core/Minecraft/MCPatch/`，命名空间 `PCL.Core.Minecraft.MCPatch`。

### 2.1 新建 `PCL.Core/Minecraft/MCPatch/McPatchModels.cs`

```csharp
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
```

### 2.2 新建 `PCL.Core/Minecraft/MCPatch/McPatchConfig.cs`

```csharp
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
```

### 2.3 新建 `PCL.Core/Minecraft/MCPatch/McPatchPath.cs`

```csharp
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
```

---

## 3. Step 2 — 核心服务

### 3.1 新建 `PCL.Core/Minecraft/MCPatch/McPatchService.cs`

```csharp
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ICSharpCode.SharpZipLib.BZip2;

namespace PCL.Core.Minecraft.MCPatch;

/// <summary>MCPatch 检查与应用（同步语义；UI 负责放后台线程，蓝图 §3 / §4 / §5）。</summary>
public static class McPatchService
{
    private const int RetryCount = 3;          // 网络：3 次
    private const int IoRetryCount = 6;        // 文件操作：6 次（蓝图 §5）
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan IoRetryBaseDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(15);

    /// <summary>共享 HttpClient；总超时交由各请求自行控制。</summary>
    private static readonly Lazy<HttpClient> _httpClient = new(() => new HttpClient
    {
        Timeout = Timeout.InfiniteTimeSpan
    });

    // ---------------- 检查（蓝图 §3） ----------------

    public static McPatchCheckResult Check(McPatchContext context,
        Action<McPatchListStatus, int>? statusCallback = null, CancellationToken cancellationToken = default)
    {
        if (context is null || string.IsNullOrWhiteSpace(context.RootPath) ||
            context.Endpoints is not { Count: > 0 })
            throw new McPatchException("MCPatch 更新上下文无效");

        statusCallback?.Invoke(McPatchListStatus.FirstFetch, 0);
        var content = _FetchTextWithRetry(context.SelectedEndpoint.VersionListUrl, statusCallback, cancellationToken);

        var allVersions = McPatchConfig.ParseVersionList(content);
        var current = McPatchPath.ReadCurrentVersion(context.RootPath);
        return new McPatchCheckResult
        {
            CurrentVersion = current,
            LatestVersion = allVersions.Count > 0 ? allVersions[^1] : "",
            AllVersions = allVersions,
            UpdateChain = McPatchConfig.ComputeUpdateChain(current, allVersions)
        };
    }

    private static string _FetchTextWithRetry(string url, Action<McPatchListStatus, int>? statusCallback,
        CancellationToken token)
    {
        Exception? last = null;
        for (var attempt = 1; attempt <= RetryCount; attempt++)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
                cts.CancelAfter(RequestTimeout);
                using var response = _httpClient.Value
                    .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                    .GetAwaiter().GetResult();
                response.EnsureSuccessStatusCode();
                using var stream = response.Content.ReadAsStreamAsync(cts.Token).GetAwaiter().GetResult();
                using var reader = new StreamReader(stream, Encoding.UTF8);
                var text = reader.ReadToEnd();
                statusCallback?.Invoke(McPatchListStatus.Success, 0);
                return text;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                last = ex;
                if (attempt < RetryCount)
                {
                    statusCallback?.Invoke(McPatchListStatus.Retrying, attempt);
                    _Wait(RetryDelay, token);
                }
            }
        }
        statusCallback?.Invoke(McPatchListStatus.Failed, 0);
        throw new McPatchException($"获取版本列表失败（已重试 {RetryCount} 次）：{url}", last);
    }

    // ---------------- 应用（蓝图 §4） ----------------

    public static void Apply(McPatchContext context, IReadOnlyList<string> versions,
        Action<McPatchProgress>? progressCallback = null, CancellationToken cancellationToken = default)
    {
        if (context is null || string.IsNullOrWhiteSpace(context.RootPath) ||
            context.Endpoints is not { Count: > 0 })
            throw new McPatchException("MCPatch 更新上下文无效");
        if (versions is not { Count: > 0 }) throw new McPatchException("没有需要更新的版本");

        var tempDir = string.IsNullOrEmpty(context.TempDirectory) ? context.RootPath : context.TempDirectory;
        _RetryIo(() => Directory.CreateDirectory(tempDir), cancellationToken);

        var n = (double)versions.Count;
        for (var i = 0; i < versions.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var version = versions[i];
            var stageStart = i / n;      // i/n
            var stageSpan = 1 / n;
            var zipPath = Path.Combine(tempDir, $"mcpatch-{version}-{Guid.NewGuid():N}.zip");
            try
            {
                // 1. 下载（占本阶段前 85%）
                var url = context.SelectedEndpoint.PackageUrlTemplate.Replace("{version}", version);
                _DownloadWithRetry(url, zipPath, (read, total, percent) => progressCallback?.Invoke(
                    new McPatchProgress
                    {
                        Overall = stageStart + 0.85 * (percent ?? 0) / 100 * stageSpan,
                        PackagePercent = percent,
                        DownloadedBytes = read,
                        TotalBytes = total,
                        Message = $"正在下载 {version}"
                    }), cancellationToken);
                progressCallback?.Invoke(new McPatchProgress
                {
                    Overall = stageStart + 0.85 * stageSpan,
                    PackagePercent = 100,
                    Message = $"下载完成：{version}"
                });

                // 2. 应用补丁包（占本阶段后 15%）
                ApplyPackage(context, version, zipPath, stageStart, stageSpan, progressCallback, cancellationToken);

                // 3. 每个版本应用成功即写版本号（蓝图 §4）
                _RetryIo(() => McPatchPath.WriteCurrentVersion(context.RootPath, version), cancellationToken);
                progressCallback?.Invoke(new McPatchProgress
                {
                    Overall = (i + 1) / n,
                    PackagePercent = 100,
                    Message = $"已更新到 {version}"
                });
            }
            finally
            {
                try { File.Delete(zipPath); } catch { /* 临时文件清理失败忽略 */ }
            }
        }
    }

    /// <summary>应用单个补丁包。internal：供单元测试直接传入本地 zip。</summary>
    internal static void ApplyPackage(McPatchContext context, string version, string zipPath,
        double stageStart, double stageSpan, Action<McPatchProgress>? progressCallback,
        CancellationToken cancellationToken = default)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName.Replace('\\', '/').TrimStart('/');
            if (name.Length > 0 && !entries.ContainsKey(name)) entries[name] = entry;
        }

        if (!entries.TryGetValue(".mcpatch-meta.json", out var metaEntry))
            throw new McPatchException($"补丁包 {version} 缺少 .mcpatch-meta.json");
        McPatchMeta meta;
        try
        {
            using var metaStream = metaEntry.Open();
            meta = JsonSerializer.Deserialize<McPatchMeta>(metaStream) ??
                   throw new McPatchException($"补丁包 {version} 的元数据为空");
        }
        catch (Exception ex) when (ex is not McPatchException)
        {
            throw new McPatchException($"补丁包 {version} 的 .mcpatch-meta.json 解析失败", ex);
        }

        // 删旧（蓝图 §4.1.4）
        foreach (var oldFile in meta.OldFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = McPatchPath.MapRealPath(context.RootPath, context.SelectedVersionName, oldFile);
            if (File.Exists(target)) _RetryIo(() => _DeleteFileSafe(target), cancellationToken);
        }
        foreach (var oldFolder in meta.OldFolders)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = McPatchPath.MapRealPath(context.RootPath, context.SelectedVersionName, oldFolder);
            if (Directory.Exists(target)) _RetryIo(() => _DeleteDirectorySafe(target), cancellationToken);
        }

        // 建新目录（蓝图 §4.1.5）
        foreach (var newFolder in meta.NewFolders)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = McPatchPath.MapRealPath(context.RootPath, context.SelectedVersionName, newFolder);
            _RetryIo(() => Directory.CreateDirectory(target), cancellationToken);
        }

        // 写新文件：仅 mode ∈ {f, m, fill, modify}（蓝图 §4.1.6）
        var dataFiles = meta.NewFiles
            .Where(f => f.Mode is "f" or "m" or "fill" or "modify" && !string.IsNullOrWhiteSpace(f.Path))
            .ToList();
        var count = (double)dataFiles.Count;
        for (var j = 0; j < dataFiles.Count; j++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = dataFiles[j];
            var entryName = file.Path.Replace('\\', '/').TrimStart('/');
            if (!entries.TryGetValue(entryName, out var dataEntry))
                throw new McPatchException($"补丁包 {version} 中找不到数据条目：{file.Path}");
            var target = McPatchPath.MapRealPath(context.RootPath, context.SelectedVersionName, file.Path);
            var temp = target + ".mcpatchtmp";
            var backup = target + ".mcpatchbak";

            _RetryIo(() =>
            {
                // a+b. 读条目并校验存储字节 SHA1（bzipped-hash，小写）
                byte[] stored;
                using (var ms = new MemoryStream())
                {
                    using var entryStream = dataEntry.Open();
                    entryStream.CopyTo(ms);
                    stored = ms.ToArray();
                }
                var bzippedHash = Convert.ToHexString(SHA1.HashData(stored)).ToLowerInvariant();
                if (!string.IsNullOrEmpty(file.BzippedHash) &&
                    !string.Equals(bzippedHash, file.BzippedHash, StringComparison.OrdinalIgnoreCase))
                    throw new McPatchException($"补丁数据哈希不匹配（bzipped）：{file.Path}");

                // c+d. 补 BZ 头（0x42 0x5A）后 bzip2 解码，边解码边累计 raw 长度与 SHA1
                long rawLength = 0;
                using var rawHash = IncrementalHash.CreateSHA1();
                using (var input = new MemoryStream())
                {
                    input.WriteByte(0x42);
                    input.WriteByte(0x5A);
                    input.Write(stored, 0, stored.Length);
                    input.Position = 0;
                    using var bzip2 = new BZip2InputStream(input);
                    using var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None);
                    var buffer = new byte[65536];
                    int read;
                    while ((read = bzip2.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        output.Write(buffer, 0, read);
                        rawHash.AppendData(buffer, 0, read);
                        rawLength += read;
                    }
                }

                // e. 校验 raw-length（>0 时）与 raw-hash（为空用 new-hash 兜底）
                if (file.RawLength > 0 && file.RawLength != rawLength)
                    throw new McPatchException(
                        $"补丁数据长度不匹配：{file.Path}（期望 {file.RawLength}，实际 {rawLength}）");
                var expectedRawHash = string.IsNullOrEmpty(file.RawHash) ? file.NewHash : file.RawHash;
                if (!string.IsNullOrEmpty(expectedRawHash) &&
                    !string.Equals(Convert.ToHexString(rawHash.GetHashAndReset()).ToLowerInvariant(),
                        expectedRawHash, StringComparison.OrdinalIgnoreCase))
                    throw new McPatchException($"补丁数据哈希不匹配（raw）：{file.Path}");

                // f. 原子替换：目标存在 → Replace + 清理 backup；不存在 → Move
                if (File.Exists(target))
                {
                    File.Replace(temp, target, backup);
                    try { File.Delete(backup); } catch { /* 备份清理失败不影响 */ }
                }
                else
                {
                    File.Move(temp, target);
                }
            }, cancellationToken);

            progressCallback?.Invoke(new McPatchProgress
            {
                Overall = stageStart + (0.85 + 0.15 * (j + 1) / count) * stageSpan,
                PackagePercent = 100,
                Message = $"正在应用 {version}（{j + 1}/{dataFiles.Count}）"
            });
        }
    }

    // ---------------- 下载与重试（蓝图 §5） ----------------

    private static void _DownloadWithRetry(string url, string destPath,
        Action<long, long, int?>? progress, CancellationToken token)
    {
        Exception? last = null;
        for (var attempt = 1; attempt <= RetryCount; attempt++)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
                cts.CancelAfter(DownloadTimeout);
                using var response = _httpClient.Value
                    .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                    .GetAwaiter().GetResult();
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength ?? -1;
                using var source = response.Content.ReadAsStreamAsync(cts.Token).GetAwaiter().GetResult();
                using var target = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None);
                var buffer = new byte[65536];
                long downloaded = 0;
                int chunk;
                while ((chunk = source.Read(buffer, 0, buffer.Length)) > 0)
                {
                    token.ThrowIfCancellationRequested();
                    target.Write(buffer, 0, chunk);
                    downloaded += chunk;
                    progress?.Invoke(downloaded, total, total > 0 ? (int)(downloaded * 100 / total) : null);
                }
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                last = ex;
                try { File.Delete(destPath); } catch { }
                if (attempt < RetryCount) _Wait(RetryDelay, token);
            }
        }
        throw new McPatchException($"下载补丁包失败（已重试 {RetryCount} 次）：{url}", last);
    }

    /// <summary>文件操作重试（6 次）：处理只读属性与占用；最终转可读错误（蓝图 §5）。</summary>
    private static void _RetryIo(Action action, CancellationToken token)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < IoRetryCount)
            {
                _Wait(IoRetryBaseDelay * attempt, token);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new McPatchException(
                    "文件操作失败（文件可能被占用，请先关闭正在运行的 Minecraft / Java / 文件预览）：" + ex.Message, ex);
            }
        }
    }

    private static void _Wait(TimeSpan delay, CancellationToken token)
    {
        try
        {
            Task.Delay(delay, token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            token.ThrowIfCancellationRequested();
        }
    }

    private static void _DeleteFileSafe(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (UnauthorizedAccessException)
        {
            File.SetAttributes(path, FileAttributes.Normal);
            File.Delete(path);
        }
    }

    private static void _DeleteDirectorySafe(string path)
    {
        try
        {
            Directory.Delete(path, true);
        }
        catch (UnauthorizedAccessException)
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(path, true);
        }
    }
}
```

> 注意：此文件需要 `using System;` 等基础 using（`System.Threading.Tasks` 用于 Task.Delay）——UI 项目隐式 using 在 PCL.Core **不生效**，请补全：`System`、`System.Collections.Generic`、`System.IO`、`System.Linq`、`System.Threading`、`System.Threading.Tasks`。

---

## 4. Step 3 — 单元测试

### 4.1 新建 `PCL.Core.Test/Minecraft/McPatchTest.cs`

```csharp
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ICSharpCode.SharpZipLib.BZip2;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Minecraft.MCPatch;

namespace PCL.Core.Test.Minecraft;

[TestClass]
public sealed class McPatchTest
{
    // ---------- TryLoadEndpoints（蓝图 §8） ----------

    [TestMethod]
    public void LoadEndpoints_MissingFile_ReturnsFalseWithDefaultEndpoint()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mcpatch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var found = McPatchConfig.TryLoadEndpoints(dir, dir, out var endpoints);
            Assert.IsFalse(found);
            Assert.AreEqual(1, endpoints.Count);
            Assert.AreEqual(McPatchEndpoint.DefaultVersionListUrl, endpoints[0].VersionListUrl);
        }
        finally { Directory.Delete(dir, true); }
    }

    [TestMethod]
    public void LoadEndpoints_SingleObject_FillsDefaults()
    {
        using var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Path, McPatchConfig.ConfigFileName),
            """{ "versionListUrl": "http://a.example/versions.txt" }""");
        var found = McPatchConfig.TryLoadEndpoints(dir.Path, dir.Path, out var endpoints);
        Assert.IsTrue(found);
        Assert.AreEqual(1, endpoints.Count);
        Assert.AreEqual("", endpoints[0].Name);
        Assert.AreEqual("http://a.example/versions.txt", endpoints[0].VersionListUrl);
        Assert.AreEqual(McPatchEndpoint.DefaultPackageUrlTemplate, endpoints[0].PackageUrlTemplate);
        Assert.AreEqual("a.example", endpoints[0].DisplayName); // name 空 → host
    }

    [TestMethod]
    public void LoadEndpoints_Array_LoadsMultiple()
    {
        using var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Path, McPatchConfig.ConfigFileName),
            """[{ "name": "S1", "versionListUrl": "http://s1/v.txt", "packageUrlTemplate": "http://s1/{version}.zip" },
                 { "name": "S2", "versionListUrl": "http://s2/v.txt", "packageUrlTemplate": "http://s2/{version}.zip" }]""");
        var found = McPatchConfig.TryLoadEndpoints(dir.Path, dir.Path, out var endpoints);
        Assert.IsTrue(found);
        Assert.AreEqual(2, endpoints.Count);
        Assert.AreEqual("S1", endpoints[0].DisplayName);
        Assert.AreEqual("S2", endpoints[1].DisplayName);
    }

    [TestMethod]
    public void LoadEndpoints_InvalidJson_FallsBackToDefault()
    {
        using var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Path, McPatchConfig.ConfigFileName), "{ not valid json !!!");
        var found = McPatchConfig.TryLoadEndpoints(dir.Path, dir.Path, out var endpoints);
        Assert.IsTrue(found);
        Assert.AreEqual(1, endpoints.Count);
        Assert.AreEqual(McPatchEndpoint.DefaultVersionListUrl, endpoints[0].VersionListUrl);
    }

    [TestMethod]
    public void LoadEndpoints_RootFileWinsWhenPathsDiffer()
    {
        using var instance = new TempDir();
        using var root = new TempDir();
        File.WriteAllText(Path.Combine(instance.Path, McPatchConfig.ConfigFileName),
            """{ "name": "INSTANCE" }""");
        File.WriteAllText(Path.Combine(root.Path, McPatchConfig.ConfigFileName),
            """{ "name": "ROOT" }""");
        McPatchConfig.TryLoadEndpoints(instance.Path, root.Path, out var endpoints);
        Assert.AreEqual("ROOT", endpoints[0].Name); // 路径不同且根路径存在 → 根路径胜出
    }

    // ---------- ParseVersionList / ComputeUpdateChain ----------

    [TestMethod]
    public void ParseVersionList_TrimsAndDedupsInOrder()
    {
        var list = McPatchConfig.ParseVersionList("1.0\r\n\r\n 1.1 \n1.0\n1.2\r\n");
        CollectionAssert.AreEqual(new[] { "1.0", "1.1", "1.2" }, list);
    }

    [TestMethod]
    public void UpdateChain_EmptyCurrent_ReturnsAll() =>
        CollectionAssert.AreEqual(new[] { "1.0", "1.1" },
            McPatchConfig.ComputeUpdateChain("", new[] { "1.0", "1.1" }));

    [TestMethod]
    public void UpdateChain_LastCurrent_ReturnsEmpty() =>
        Assert.AreEqual(0, McPatchConfig.ComputeUpdateChain("1.1", new[] { "1.0", "1.1" }).Count);

    [TestMethod]
    public void UpdateChain_MiddleCurrent_ReturnsSuffix() =>
        CollectionAssert.AreEqual(new[] { "1.1", "1.2" },
            McPatchConfig.ComputeUpdateChain("1.0", new[] { "1.0", "1.1", "1.2" }));

    [TestMethod]
    public void UpdateChain_UnknownCurrent_ReturnsAll() =>
        CollectionAssert.AreEqual(new[] { "1.0", "1.1" },
            McPatchConfig.ComputeUpdateChain("0.9", new[] { "1.0", "1.1" }));

    // ---------- MapRealPath（蓝图 §4.2） ----------

    [TestMethod]
    public void MapRealPath_StripsVersionIsolationPrefix()
    {
        var actual = McPatchPath.MapRealPath(@"C:\mc\", "1.20.1", @".minecraft\versions\1.20.1\mods\A.jar");
        Assert.IsTrue(actual.EndsWith(@"mods\A.jar", StringComparison.OrdinalIgnoreCase) &&
                      actual.StartsWith(@"C:\mc\", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void MapRealPath_StripsMinecraftPrefix() =>
        Assert.IsTrue(McPatchPath.MapRealPath(@"C:\mc\", "1.20.1", @".minecraft/mods/B.jar")
            .EndsWith(@"mods\B.jar", StringComparison.OrdinalIgnoreCase));

    [TestMethod]
    public void MapRealPath_DirectPath() =>
        Assert.IsTrue(McPatchPath.MapRealPath(@"C:\mc\", "1.20.1", @"mods\C.jar")
            .EndsWith(@"mods\C.jar", StringComparison.OrdinalIgnoreCase));

    [TestMethod]
    public void MapRealPath_RejectsTraversal()
    {
        Assert.ThrowsException<McPatchException>(() =>
            McPatchPath.MapRealPath(@"C:\mc\", "1.20.1", @"..\..\evil.jar"));
    }

    // ---------- ApplyPackage（蓝图 §8 补充） ----------

    [TestMethod]
    public void ApplyPackage_WritesFileWithPrefixStripAndHashVerify()
    {
        using var root = new TempDir();
        using var zip = TempFile(".zip");
        var raw = "hello mcpatch"u8.ToArray();
        CreatePatchZip(zip, ".minecraft/mods/Mod.jar", raw, oldFiles: [], oldFolders: [],
            newFolders: ["minecraft/config"], correctHashes: true);

        var context = NewContext(root.Path, "1.20.1");
        McPatchService.ApplyPackage(context, "1.0", zip, 0, 1, null);

        var written = Path.Combine(root.Path, "mods", "Mod.jar");
        Assert.IsTrue(File.Exists(written));
        CollectionAssert.AreEqual(raw, File.ReadAllBytes(written));
        Assert.IsTrue(Directory.Exists(Path.Combine(root.Path, "minecraft", "config")));
    }

    [TestMethod]
    public void ApplyPackage_DeletesOldFilesAndFolders()
    {
        using var root = new TempDir();
        using var zip = TempFile(".zip");
        File.WriteAllText(Path.Combine(root.Path, "old.txt"), "x");
        Directory.CreateDirectory(Path.Combine(root.Path, "olddir", "sub"));
        CreatePatchZip(zip, ".minecraft/new.txt", "n"u8.ToArray(),
            oldFiles: [".minecraft/old.txt"], oldFolders: [".minecraft/olddir"],
            newFolders: [], correctHashes: true);

        McPatchService.ApplyPackage(NewContext(root.Path, "1.20.1"), "1.0", zip, 0, 1, null);
        Assert.IsFalse(File.Exists(Path.Combine(root.Path, "old.txt")));
        Assert.IsFalse(Directory.Exists(Path.Combine(root.Path, "olddir")));
    }

    [TestMethod]
    public void ApplyPackage_WrongBzippedHash_Throws()
    {
        using var root = new TempDir();
        using var zip = TempFile(".zip");
        CreatePatchZip(zip, ".minecraft/mods/Mod.jar", "data"u8.ToArray(), [], [], [], correctHashes: false);
        Assert.ThrowsException<McPatchException>(() =>
            McPatchService.ApplyPackage(NewContext(root.Path, "1.20.1"), "1.0", zip, 0, 1, null));
    }

    [TestMethod]
    public void ApplyPackage_WrongRawLength_Throws()
    {
        using var root = new TempDir();
        var zip = TempFile(".zip");
        var raw = "0123456789"u8.ToArray();
        // 构造一个 raw-length 故意错误的包
        CreatePatchZip(zip, ".minecraft/mods/Mod.jar", raw, [], [], [],
            correctHashes: true, wrongRawLength: true);
        Assert.ThrowsException<McPatchException>(() =>
            McPatchService.ApplyPackage(NewContext(root.Path, "1.20.1"), "1.0", zip, 0, 1, null));
    }

    [TestMethod]
    public void VersionFile_RoundTrip()
    {
        using var root = new TempDir();
        McPatchPath.WriteCurrentVersion(root.Path, "1.2.3");
        Assert.AreEqual("1.2.3", McPatchPath.ReadCurrentVersion(root.Path));
    }

    // ---------- 辅助 ----------

    private static McPatchContext NewContext(string root, string version) => new()
    {
        RootPath = root,
        InstancePath = root,
        SelectedVersionName = version,
        Endpoints = [new McPatchEndpoint()]
    };

    /// <summary>构造补丁包 zip：bzip2 压缩后去掉头两字节（BZ），生成 meta 与数据条目。</summary>
    private static void CreatePatchZip(string zipPath, string logicalPath, byte[] raw,
        string[] oldFiles, string[] oldFolders, string[] newFolders,
        bool correctHashes, bool wrongRawLength = false)
    {
        using var compressed = new MemoryStream();
        using (var bzip2 = new BZip2OutputStream(compressed, 1, true)) // true = 流结束后不关闭底层流
            bzip2.Write(raw);
        var full = compressed.ToArray();
        var stored = new byte[full.Length - 2]; // 去 BZ 头（0x42 0x5A）
        Buffer.BlockCopy(full, 2, stored, 0, stored.Length);

        var bzippedHash = Convert.ToHexString(SHA1.HashData(stored)).ToLowerInvariant();
        var rawHash = Convert.ToHexString(SHA1.HashData(raw)).ToLowerInvariant();
        if (!correctHashes) bzippedHash = new string('0', 40);

        var meta = new McPatchMeta
        {
            NewFolders = newFolders,
            OldFiles = oldFiles,
            OldFolders = oldFolders,
            NewFiles =
            [
                new McPatchNewFile
                {
                    Mode = "f",
                    Path = logicalPath,
                    NewHash = rawHash,
                    BzippedHash = bzippedHash,
                    RawHash = rawHash,
                    RawLength = wrongRawLength ? raw.Length + 999 : raw.Length
                }
            ]
        };

        using var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        var metaEntry = archive.CreateEntry(".mcpatch-meta.json");
        using (var s = metaEntry.Open())
            s.Write(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(meta)));
        var dataEntry = archive.CreateEntry(logicalPath.Replace('\\', '/'));
        using (var s = dataEntry.Open())
            s.Write(stored);
    }

    private static string TempFile(string ext)
    {
        return Path.Combine(Path.GetTempPath(), "mcpatch-" + Guid.NewGuid().ToString("N") + ext);
    }

    private sealed class TempDir : IDisposable
    {
        public readonly string Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "mcpatch-" + Guid.NewGuid().ToString("N"));
        public TempDir() => Directory.CreateDirectory(Path);
        public void Dispose()
        {
            try { Directory.Delete(Path, true); } catch { }
        }
    }
}
```

> 若个别断言因路径前缀剥离细节（如 `DisplayName` 的 path 拼接格式）失败，**以被测方法实际行为对照蓝图语义修正断言**，不要反向修改实现来迁就错误断言。

---

## 5. Step 4 — 本地化键

在 `PCL.Core/App/Localization/Languages/zh-CN.xaml` 中，定位 `Launch.Home.Button.Loading`（约 L1620），在其后追加；`en-US.xaml` 同位置追加英文版：

```xml
<!-- MCPatch 更新卡片 -->
<sys:String x:Key="Launch.MCPatch.Title">MCPatch 更新</sys:String>
<sys:String x:Key="Launch.MCPatch.CurrentVersion">当前版本：{0}</sys:String>
<sys:String x:Key="Launch.MCPatch.LatestVersion">最新版本：{0}</sys:String>
<sys:String x:Key="Launch.MCPatch.NeedUpdate">需要更新</sys:String>
<sys:String x:Key="Launch.MCPatch.UpToDate">已是最新</sys:String>
<sys:String x:Key="Launch.MCPatch.Status.FirstFetch">版本列表：首次获取中</sys:String>
<sys:String x:Key="Launch.MCPatch.Status.Retrying">版本列表：重试中（{0}）</sys:String>
<sys:String x:Key="Launch.MCPatch.Status.Success">版本列表：已获得</sys:String>
<sys:String x:Key="Launch.MCPatch.Status.Failed">版本列表：失败</sys:String>
<sys:String x:Key="Launch.MCPatch.Action.Update">立刻更新</sys:String>
<sys:String x:Key="Launch.MCPatch.Action.Retry">手动重试</sys:String>
<sys:String x:Key="Launch.MCPatch.GameRunning">请先关闭正在运行的 Minecraft，再进行更新</sys:String>
<sys:String x:Key="Launch.MCPatch.Done">MCPatch 更新完成</sys:String>
<sys:String x:Key="Launch.MCPatch.FileOccupied">文件被占用，请先关闭正在运行的 Minecraft / Java / 文件预览后重试</sys:String>
<sys:String x:Key="Launch.Home.Button.VersionMismatch">版本不一致请更新</sys:String>
```

en-US 值：`MCPatch Update` / `Current version: {0}` / `Latest version: {0}` / `Update required` / `Up to date` / `Version list: fetching` / `Version list: retrying ({0})` / `Version list: loaded` / `Version list: failed` / `Update Now` / `Retry` / `Please close the running Minecraft before updating` / `MCPatch update completed` / `Files are in use. Close Minecraft / Java / file preview and retry` / `Version mismatch — update required`。

---

## 6. Step 5 — UI 卡片

### 6.1 新建 `Plain Craft Launcher 2/Pages/PageLaunch/CardMCPatch.xaml`

```xml
<UserControl x:Class="PCL.CardMCPatch"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:local="clr-namespace:PCL"
             Visibility="Collapsed">
    <local:MyCard Title="{DynamicResource Launch.MCPatch.Title}" Margin="0,0,0,15">
        <local:MyComboBox x:Name="ComboServer" Width="150" Margin="0,9,12,0"
                          HorizontalAlignment="Right" VerticalAlignment="Top"
                          Visibility="Collapsed"
                          SelectionChanged="ComboServer_SelectionChanged" />
        <StackPanel Margin="25,38,23,15">
            <TextBlock x:Name="LabCurrent" FontSize="13" Margin="0,0,0,3"
                       Foreground="{DynamicResource ColorBrush1}" />
            <TextBlock x:Name="LabLatest" FontSize="13" Margin="0,0,0,3" TextWrapping="Wrap"
                       Foreground="{DynamicResource ColorBrush1}" />
            <TextBlock x:Name="LabStatus" FontSize="12.5" Margin="0,0,0,8"
                       Foreground="{DynamicResource ColorBrushGray3}" />
            <ProgressBar x:Name="BarProgress" Height="6" Minimum="0" Maximum="100"
                         Foreground="{DynamicResource ColorBrush3}"
                         Background="{DynamicResource ColorBrush6}" BorderThickness="0" />
            <TextBlock x:Name="LabMain" FontSize="12.5" Margin="0,6,0,0" TextTrimming="CharacterEllipsis"
                       Foreground="{DynamicResource ColorBrush1}" />
            <TextBlock x:Name="LabPackage" FontSize="12.5" Margin="0,2,0,0" TextTrimming="CharacterEllipsis"
                       Foreground="{DynamicResource ColorBrushGray3}" />
            <StackPanel Orientation="Horizontal" Margin="0,10,0,0">
                <local:MyButton x:Name="BtnUpdate" Height="32" ColorType="Highlight" TextPadding="24,0,24,0"
                                Text="{DynamicResource Launch.MCPatch.Action.Update}" IsEnabled="False"
                                Click="BtnUpdate_Click" />
                <local:MyButton x:Name="BtnRetry" Height="32" Margin="10,0,0,0" TextPadding="24,0,24,0"
                                Visibility="Collapsed"
                                Text="{DynamicResource Launch.MCPatch.Action.Retry}"
                                Click="BtnRetry_Click" />
            </StackPanel>
        </StackPanel>
    </local:MyCard>
</UserControl>
```

> 执行提示：若 `MyButton` 的 `TextPadding`/`ColorType` 属性签名与上面不符，以 [MyButton.xaml.cs](file:///workspace/Plain%20Craft%20Launcher%202/Controls/MyButton.xaml.cs) 与 PageLaunchLeft.xaml 的既有用法为准调整（BtnMore 用 `TextPadding="36"`，BtnLaunch 用 `ColorType="Highlight"`）。

### 6.2 新建 `Plain Craft Launcher 2/Pages/PageLaunch/CardMCPatch.xaml.cs`

```csharp
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using PCL.Core.App.Localization;
using PCL.Core.Minecraft.MCPatch;

namespace PCL;

/// <summary>启动页右侧的「MCPatch 更新」卡片（蓝图 §6）。</summary>
public partial class CardMCPatch : UserControl
{
    /// <summary>
    /// 启动门禁（蓝图 §6.2）：卡片可见且（正在检查 / 正在更新 / 无结果 / 需要更新）时为 true。
    /// PageLaunchLeft.RefreshButtonsUI 消费。
    /// </summary>
    public static bool IsLaunchBlocked { get; private set; }

    private DispatcherTimer? _timer;
    private string? _lastKey;                 // 实例键 "root|version|endpointKey"
    private string _endpointSignature = "";
    private volatile bool _isChecking;
    private volatile bool _isUpdating;
    private bool _rebuildingCombo;
    private McPatchCheckResult? _result;
    private McPatchListStatus _listStatus;
    private int _statusAttempt;
    private CancellationTokenSource? _checkCts;
    private CancellationTokenSource? _updateCts;
    private readonly List<McPatchEndpoint> _endpoints = [];
    private int _selectedEndpoint;
    private string _root = "";
    private string _instancePath = "";
    private string _versionName = "";

    public CardMCPatch()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (_timer is null)
            {
                _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) }; // 蓝图 §6.2 自动刷新
                _timer.Tick += (_, _) => Evaluate(false);
            }
            _timer.Start();
        };
        Unloaded += (_, _) =>
        {
            _timer?.Stop();
            _checkCts?.Cancel(); // 只取消检查，不中断进行中的更新
        };
    }

    // ---------- 轮询入口（2s 定时器 / 手动重试 / 下拉切换） ----------

    private void Evaluate(bool force)
    {
        if (_isUpdating) return; // 更新进行中跳过（蓝图 §6.2）

        var instance = ModInstanceList.McMcInstanceSelected;
        if (instance is null)
        {
            Hide();
            return;
        }

        _root = instance.PathIndie;
        _instancePath = instance.PathInstance;
        _versionName = instance.Name;

        // 仅当前选中实例存在配置文件时显示（蓝图 §6）
        if (!McPatchConfig.TryLoadEndpoints(_instancePath, _root, out var endpoints))
        {
            Hide();
            return;
        }
        if (Visibility != Visibility.Visible)
        {
            Visibility = Visibility.Visible;
            _lastKey = null; // 重新显示时强制重查
        }

        // 端点列表变化 → 重建下拉（签名比较，尽量按 key 保持选中，蓝图 §6.2）
        var signature = string.Join(";", endpoints.Select(e => e.Key));
        if (signature != _endpointSignature)
        {
            _endpointSignature = signature;
            _endpoints.Clear();
            _endpoints.AddRange(endpoints);
            _selectedEndpoint = Math.Clamp(_selectedEndpoint, 0, _endpoints.Count - 1);
            RebuildCombo();
        }

        // 实例键去重（蓝图 §6.2）
        var key = $"{_root}|{_versionName}|{_endpoints[_selectedEndpoint].Key}";
        if (!force && key == _lastKey) return;
        StartCheck(key);
    }

    private void Hide()
    {
        if (Visibility == Visibility.Collapsed && _lastKey is null) return;
        Visibility = Visibility.Collapsed;
        _lastKey = null;
        _result = null;
        _endpointSignature = "";
        _endpoints.Clear();
        _checkCts?.Cancel();
        UpdateGate();
    }

    // ---------- 检查 ----------

    private void StartCheck(string key)
    {
        _lastKey = key;
        _checkCts?.Cancel();
        _checkCts = new CancellationTokenSource();
        var token = _checkCts.Token;
        _isChecking = true;
        _listStatus = McPatchListStatus.FirstFetch;
        _statusAttempt = 0;
        Render();
        UpdateGate();

        var ctx = NewContext();
        ModBase.RunInNewThread(() =>
        {
            try
            {
                var result = McPatchService.Check(ctx, (status, attempt) => ModBase.RunInUi(() =>
                {
                    if (_lastKey != key) return; // 忽略过期回调（蓝图 §6.2）
                    _listStatus = status;
                    _statusAttempt = attempt;
                    RenderStatus();
                }), token);
                ModBase.RunInUi(() =>
                {
                    if (_lastKey != key) return;
                    _result = result;
                    _listStatus = McPatchListStatus.Success;
                    Render();
                });
            }
            catch (Exception ex)
            {
                ModBase.RunInUi(() =>
                {
                    if (_lastKey != key) return;
                    _listStatus = McPatchListStatus.Failed; // 失败 → 手动重试按钮
                    ModBase.Log(ex, "[MCPatch] 检查更新失败");
                    Render();
                });
            }
            finally
            {
                ModBase.RunInUi(() =>
                {
                    if (_lastKey != key) return;
                    _isChecking = false;
                    Render();
                    UpdateGate();
                });
            }
        }, "MCPatch Check #" + ModBase.GetUuid());
    }

    // ---------- 更新 ----------

    private void BtnUpdate_Click(object sender, MouseButtonEventArgs e)
    {
        if (_isUpdating || _isChecking) return;
        // 游戏正在运行 / 启动 → 拦截（蓝图 §6.2）
        if (ModWatcher.hasRunningMinecraft || ModLaunch.mcLaunchLoader.State == ModBase.LoadState.Loading)
        {
            HintService.Hint(Lang.Text("Launch.MCPatch.GameRunning"), HintType.Error);
            return;
        }
        var chain = _result?.UpdateChain;
        if (chain is not { Count: > 0 }) return;

        _updateCts = new CancellationTokenSource();
        var token = _updateCts.Token;
        var key = _lastKey;
        _isUpdating = true;
        BarProgress.Value = 0;
        LabMain.Text = "";
        LabPackage.Text = "";
        Render();
        UpdateGate();

        var ctx = NewContext();
        ModBase.RunInNewThread(() =>
        {
            try
            {
                McPatchService.Apply(ctx, chain, progress => ModBase.RunInUi(() =>
                {
                    if (_lastKey != key) return;
                    BarProgress.Value = Math.Clamp(progress.Overall * 100, 0, 100);
                    LabMain.Text = progress.Message;
                    LabPackage.Text = progress.PackagePercent is { } percent
                        ? $"{percent}%"
                        : progress.TotalBytes > 0 ? $"{progress.DownloadedBytes}/{progress.TotalBytes} 字节" : "";
                }), token);
                ModBase.RunInUi(() => HintService.Hint(Lang.Text("Launch.MCPatch.Done"), HintType.Success));
            }
            catch (Exception ex)
            {
                ModBase.RunInUi(() =>
                {
                    ModBase.Log(ex, "[MCPatch] 应用更新失败");
                    // IOException 组给占用文件专属提示，其余给错误信息（蓝图 §6.2）
                    var message = ex is IOException or UnauthorizedAccessException
                        ? Lang.Text("Launch.MCPatch.FileOccupied")
                        : ex.Message;
                    HintService.Hint(message, HintType.Error);
                });
            }
            finally
            {
                ModBase.RunInUi(() =>
                {
                    _isUpdating = false;
                    UpdateGate();
                    Evaluate(true); // 恢复按钮 + 重新检查（成功后解除门禁）
                });
            }
        }, "MCPatch Apply #" + ModBase.GetUuid());
    }

    private void BtnRetry_Click(object sender, MouseButtonEventArgs e)
    {
        Evaluate(true);
    }

    // ---------- 服务器下拉 ----------

    private void RebuildCombo()
    {
        _rebuildingCombo = true;
        try
        {
            var selectedKey = _selectedEndpoint < _endpoints.Count ? _endpoints[_selectedEndpoint].Key : "";
            ComboServer.Items.Clear();
            foreach (var endpoint in _endpoints)
                ComboServer.Items.Add(new MyComboBoxItem { Content = endpoint.DisplayName });
            var index = _endpoints.FindIndex(x => x.Key == selectedKey);
            _selectedEndpoint = index >= 0 ? index : 0;
            ComboServer.SelectedIndex = _selectedEndpoint;
            ComboServer.Visibility = _endpoints.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        }
        finally
        {
            _rebuildingCombo = false;
        }
    }

    private void ComboServer_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_rebuildingCombo) return;
        if (ComboServer.SelectedIndex >= 0 && ComboServer.SelectedIndex < _endpoints.Count &&
            ComboServer.SelectedIndex != _selectedEndpoint)
        {
            _selectedEndpoint = ComboServer.SelectedIndex;
            Evaluate(true); // 切换触发重新检查（蓝图 §6.2）
        }
    }

    // ---------- 渲染 ----------

    private void Render()
    {
        var needUpdate = _result?.NeedUpdate == true;
        var current = _result is null || string.IsNullOrEmpty(_result.CurrentVersion)
            ? "-"
            : _result.CurrentVersion;
        var latest = _result is null || string.IsNullOrEmpty(_result.LatestVersion)
            ? "-"
            : _result.LatestVersion;
        LabCurrent.Text = Lang.Text("Launch.MCPatch.CurrentVersion", current);
        LabLatest.Text = Lang.Text("Launch.MCPatch.LatestVersion", latest) + "（" +
                         Lang.Text(needUpdate ? "Launch.MCPatch.NeedUpdate" : "Launch.MCPatch.UpToDate") + "）";
        RenderStatus();
        // 更新按钮仅 NeedUpdate 且未在更新中可点（蓝图 §6.2）
        BtnUpdate.IsEnabled = needUpdate && !_isUpdating && !_isChecking;
        BtnRetry.Visibility = _listStatus == McPatchListStatus.Failed ? Visibility.Visible : Visibility.Collapsed;
        // 无需更新时进度条满（蓝图 §6.2）
        BarProgress.Value = _result is not null && !needUpdate ? 100 : 0;
    }

    private void RenderStatus()
    {
        LabStatus.Text = _listStatus switch
        {
            McPatchListStatus.FirstFetch => Lang.Text("Launch.MCPatch.Status.FirstFetch"),
            McPatchListStatus.Retrying => Lang.Text("Launch.MCPatch.Status.Retrying", _statusAttempt),
            McPatchListStatus.Success => Lang.Text("Launch.MCPatch.Status.Success"),
            _ => Lang.Text("Launch.MCPatch.Status.Failed")
        };
    }

    // ---------- 启动门禁（蓝图 §6.2） ----------

    private void UpdateGate()
    {
        var blocked = Visibility == Visibility.Visible &&
                      (_isChecking || _isUpdating || _result is null || _result.NeedUpdate);
        if (IsLaunchBlocked == blocked) return;
        IsLaunchBlocked = blocked;
        // MCPatch 状态切换后必须刷新启动按钮（蓝图 §6.2）
        ModBase.RunInUi(() => ModMain.frmLaunchLeft?.RefreshButtonsUI());
    }

    private McPatchContext NewContext() => new()
    {
        RootPath = _root,
        InstancePath = _instancePath,
        SelectedVersionName = _versionName,
        Endpoints = _endpoints.ToList(),
        SelectedEndpointIndex = _selectedEndpoint,
        TempDirectory = Path.Combine(ModBase.pathTemp, "McPatch")
    };
}
```

### 6.3 修改 [PageLaunchRight.xaml](file:///workspace/Plain%20Craft%20Launcher%202/Pages/PageLaunch/PageLaunchRight.xaml) — 挂载卡片

在 L7 `<StackPanel x:Name="PanCustom" />` 与 L8 `PanHint` 卡片之间插入一行（XAML 内置 = 不可删除卡片）：

```xml
            <local:CardMCPatch x:Name="CardMCPatch" />
```

---

## 7. Step 6 — 启动门禁联动（PageLaunchLeft.xaml.cs）

修改 [PageLaunchLeft.xaml.cs](file:///workspace/Plain%20Craft%20Launcher%202/Pages/PageLaunch/PageLaunchLeft.xaml.cs)：

**(1)** 在 `btnLaunchState` / `btnLaunchVersion` / `_btnLaunchLanguage` 字段声明旁（搜索 `private int btnLaunchState` 定位）新增字段：

```csharp
    private bool btnLaunchMcPatchBlocked;
```

**(2)** `RefreshButtonsUI()` 中，将早退条件（原 L275-279）改为（新增两行 + 条件追加一项）：

```csharp
        // 更新状态。
        var currentLanguage = LocalizationService.CurrentLanguage.Code;
        var mcPatchBlocked = CardMCPatch.IsLaunchBlocked;
        if (currentState == btnLaunchState &&
            currentLanguage == _btnLaunchLanguage &&
            mcPatchBlocked == btnLaunchMcPatchBlocked &&
            ((ModInstanceList.McMcInstanceSelected is null ? "" : ModInstanceList.McMcInstanceSelected.PathInstance) ?? "") ==
            ((btnLaunchVersion is null ? "" : btnLaunchVersion.PathInstance) ?? ""))
            goto ExitRefresh;
        _btnLaunchLanguage = currentLanguage;
        btnLaunchVersion = ModInstanceList.McMcInstanceSelected;
        btnLaunchState = currentState;
        btnLaunchMcPatchBlocked = mcPatchBlocked;
```

**(3)** `case 3:` 分支末尾（`LabVersion.Text = ...` 之后、`break;` 之前）追加：

```csharp
                // MCPatch 门禁：版本不一致 → 置灰并改文案（蓝图 §6.2）
                if (CardMCPatch.IsLaunchBlocked)
                {
                    _launchButtonAction = LaunchButtonAction.Disabled;
                    ModMain.frmLaunchLeft.BtnLaunch.Text = Lang.Text("Launch.Home.Button.VersionMismatch");
                    ModMain.frmLaunchLeft.BtnLaunch.IsEnabled = false;
                }
```

> `LaunchButtonClick()`（L210）已有 `!BtnLaunch.IsEnabled` 守卫，无需修改。MCPatch 状态变化时由 `CardMCPatch.UpdateGate()` 主动调用 `RefreshButtonsUI()`。

---

## 8. Step 7 — 非核心改动 ×3（蓝图 §7，全部保留）

### 8.1 禁用软件本体自动更新 — [UpdateManager.cs](file:///workspace/Plain%20Craft%20Launcher%202/Modules/Updates/UpdateManager.cs)

**(1)** `UpdateStart`（L73 起）方法体**整体替换**为直接 LOG + RETURN（保留签名，调用点 PageSetupUpdate.xaml.cs L164 随之空转，符合蓝图「入口直接返回」）：

```csharp
    public static void UpdateStart(UpdateEnums.UpdateType type, string receivedKey = null, bool forceValidated = false)
    {
        // MCPatch 分支需求：禁用 PCL CE 本体自动更新（蓝图 §7.1）
        ModBase.Log("[Update] 已禁用 PCL CE 自身更新（MCPatch 分支策略）");
    }
```

**(2)** `LoadOnlineInfo`（L238）删除自动更新分支调用，仅保留公告：

```csharp
    private static void LoadOnlineInfo()
    {
        // MCPatch 分支需求：跳过自动更新分支，仅保留公告等其他内容（蓝图 §7.1）
        AnnouncementService.Load();
    }
```

**(3)** 删除整个 `ScheduleBasedOnConfig` 方法（原 L244-265，已无调用方）。其余成员（`UpdateRestart`、`DownloadLatestPCL`、`remoteServer` 等）保留不动（最小 diff）。

### 8.2 简化登录选项 — [ProfileUi.cs](file:///workspace/Plain%20Craft%20Launcher%202/Modules/Minecraft/ProfileUi.cs)

`CanCreateOtherProfile()`（L182-189）改为无条件 `true`（3 个调用点零改动：CreateProfile、PageInstanceSetup L639/L722 自动生效）：

```csharp
    public static bool CanCreateOtherProfile()
    {
        // MCPatch 分支需求：新建档案无条件显示 正版 / 第三方 / 离线（蓝图 §7.2）
        return true;
    }
```

### 8.3 移除特殊版本提示 — [FormMain.xaml.cs](file:///workspace/Plain%20Craft%20Launcher%202/FormMain.xaml.cs)

删除 L230-251 的整块 `#if DEBUG || DEBUGCI ... #endif`（从 `#if DEBUG || DEBUGCI` 起到对应 `#endif` 止，含 `PCL_DISABLE_DEBUG_HINT` 判断与 `MyMsgBox` 弹窗），同时删除 L225 的 `// 特殊版本提示` 注释（该注释仅描述此块），**保留**外层 `try {`、`// EULA 提示` 及后续逻辑。删除后如产生多余空行，顺手清理。

---

## 9. 假设与决策

1. **配置定位语义**取蓝图 §2.1 字面：实例路径优先；`instancePath ≠ rootPath` 且根路径存在文件时**根路径胜出**（§2.2 测试锁定该行为）。若原意为「root 仅兜底」，只需改 `TryLoadEndpoints` 一处分支。
2. 端点默认值 = 蓝图 §2.1 JSON 示例值；`name` 缺省为空串，由 `DisplayName` 兜底展示。
3. 核心服务为**同步阻塞**方法（蓝图 §9「同步 / 可重入语义一致」），UI 用 `RunInNewThread` 调度，网络调用 `.GetAwaiter().GetResult()` 同步化（库内有先例）。
4. 核心服务自持静态 `HttpClient`（总超时 infinite，按请求控制：版本列表 30s / 下载 15min），不依赖 `NetworkService` DI，保证单测独立。
5. zip 读取用 `ZipArchive`；bzip2 解码用 SharpZipLib `BZip2InputStream`（前置 `0x42 0x5A` 两字节，蓝图 §4.1.6c）。
6. 卡片为 PageLaunchRight.xaml 内置元素 → 天然「不可删除」；位置在 `PanCustom` 之后、`PanHint` 之前。
7. `CanCreateOtherProfile` 保留方法仅改返回值（最小 diff）；`UpdateStart` 保留签名仅改方法体。
8. 新语言键只加 zh-CN + en-US（实测仅 4/7 语言文件含全量键，缺失键可回退）。
9. 三个项目均自动纳入新文件，**无需改任何 csproj**。
10. 版本状态文件 `mc-patch-version.txt` 位于逻辑根（`PathIndie`）下（蓝图 §2）。
11. 更新按钮在 `_isChecking` 期间也临时禁用（比蓝图略保守，避免检查/更新竞态）。
12. Unloaded 只取消检查不取消更新（避免切页中断更新；更新完成回调仍经 `RunInUi` 生效）。

---

## 10. 验证清单（Step 8）

1. **编译**：`dotnet build "Plain Craft Launcher 2.slnx"`（含 CI|x64 与 CI|ARM64 两种配置可选）。
2. **单测**：`dotnet test PCL.Core.Test --filter "FullyQualifiedName~McPatchTest"`，全部通过（测试含网络无关的纯逻辑 + 本地 zip 端到端构造）。
3. **静态确认**：
   - `UpdateStart` 空转、`ScheduleBasedOnConfig` 已删、启动时公告仍加载；
   - DEBUG/CI 配置启动不再弹「特殊版本提示」；
   - 新建档案恒显示 正版/第三方/离线 三项；
   - `RefreshButtonsUI` 早退条件含 `mcPatchBlocked`、`case 3` 有门禁覆盖分支。
4. **端到端验收**（蓝图 §9，Windows 实机 + 真实补丁服务器）：
   - 检查 → 下载 → 应用 → `mc-patch-version.txt` 逐版本推进 → 卡片转「已是最新」→ 启动按钮恢复「启动游戏」；
   - 版本落后时：启动按钮置灰、文案「版本不一致请更新」；更新完成即恢复；
   - 版本隔离两种布局（`.minecraft\` 与 `.minecraft\versions\<名>\`）补丁均落盘到 `PathIndie` 下正确位置；
   - 多端点下拉切换触发重查且保持选中；检查失败显示「手动重试」并可重试；
   - 游戏运行中点「立刻更新」被拦截提示；取消/断网路径不崩溃、日志可查。
