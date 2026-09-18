using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ICSharpCode.SharpZipLib.BZip2;
using PCL.Core.IO.Net.Http;

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

    /// <summary>共享 HttpClient；经 HttpProxyManager 接入 PCL 可选 HTTP 代理，总超时交由各请求自行控制。</summary>
    private static readonly Lazy<HttpClient> _httpClient = new(() => new HttpClient(
        new SocketsHttpHandler
        {
            UseProxy = true,
            Proxy = HttpProxyManager.Instance
        })
        {
            Timeout = Timeout.InfiniteTimeSpan
        });

    // ---------------- 检查（蓝图 §3） ----------------

    public static McPatchCheckResult Check(McPatchContext context,
        Action<McPatchListStatus, int>? statusCallback = null,
        Action<string>? currentVersionCallback = null, CancellationToken cancellationToken = default)
    {
        if (context is null || string.IsNullOrWhiteSpace(context.RootPath) ||
            context.Endpoints is not { Count: > 0 })
            throw new McPatchException("MCPatch 更新上下文无效");

        statusCallback?.Invoke(McPatchListStatus.FirstFetch, 0);
        // 本地版本先于远程读取：让 UI 在等待远程期间立即展示本地版本
        var current = McPatchPath.ReadCurrentVersion(context.RootPath);
        currentVersionCallback?.Invoke(current);

        var content = _FetchTextWithRetry(context.SelectedEndpoint.VersionListUrl, statusCallback, cancellationToken);

        var allVersions = McPatchConfig.ParseVersionList(content);
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
                // 响应头获取阶段尚无字节进度：先提示“开始获取数据”，避免进度条/文案停滞（下载完上一包后同样如此）
                progressCallback?.Invoke(new McPatchProgress
                {
                    Overall = stageStart,
                    PackagePercent = null,
                    Message = $"开始获取数据（{version}）..."
                });
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
                using var rawHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
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