using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using ICSharpCode.SharpZipLib.BZip2;

namespace PCL.Core.Updater;

public sealed class McPatchUpdateService
{
    private const int FileOpRetryCount = 6;
    private static readonly HttpClient SharedHttpClient = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;

    public McPatchUpdateService(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? SharedHttpClient;
    }

    public McPatchEndpointOptions LoadEndpointOptions(string? configPath)
    {
        if (string.IsNullOrWhiteSpace(configPath) || !File.Exists(configPath))
        {
            return new McPatchEndpointOptions();
        }

        try
        {
            var text = File.ReadAllText(configPath, Encoding.UTF8);
            var parsed = JsonSerializer.Deserialize<McPatchEndpointOptions>(text, JsonOptions);
            if (parsed is null)
            {
                return new McPatchEndpointOptions();
            }

            if (string.IsNullOrWhiteSpace(parsed.VersionListUrl))
            {
                parsed.VersionListUrl = McPatchEndpointOptions.DefaultVersionListUrl;
            }

            if (string.IsNullOrWhiteSpace(parsed.PackageUrlTemplate))
            {
                parsed.PackageUrlTemplate = McPatchEndpointOptions.DefaultPackageUrlTemplate;
            }

            return parsed;
        }
        catch
        {
            return new McPatchEndpointOptions();
        }
    }

    public McPatchCheckResult CheckForUpdates(
        McPatchUpdateContext context,
        Action<McPatchListStatus, int, string?>? statusChanged = null,
        CancellationToken cancellationToken = default)
    {
        ValidateContext(context);

        var versionListText = GetStringWithRetry(context.Endpoints.VersionListUrl, statusChanged, cancellationToken);
        var allVersions = versionListText
            .Replace("\r", string.Empty)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(static line => line.Trim())
            .Where(static line => !string.IsNullOrWhiteSpace(line))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var currentVersion = ReadCurrentVersion(context);
        var latestVersion = allVersions.LastOrDefault() ?? string.Empty;
        var pendingVersions = BuildPendingVersionList(allVersions, currentVersion);

        return new McPatchCheckResult
        {
            CurrentVersion = currentVersion,
            LatestVersion = latestVersion,
            AllVersions = allVersions,
            PendingVersions = pendingVersions
        };
    }

    public void ApplyUpdates(
        McPatchUpdateContext context,
        IReadOnlyList<string> orderedPendingVersions,
        Action<double, string>? progressChanged = null,
        CancellationToken cancellationToken = default)
    {
        ValidateContext(context);
        if (orderedPendingVersions.Count == 0)
        {
            progressChanged?.Invoke(1d, "无需更新");
            return;
        }

        Directory.CreateDirectory(context.RootPath);

        for (var i = 0; i < orderedPendingVersions.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var version = orderedPendingVersions[i];
            var packageUrl = BuildPackageUrl(context.Endpoints.PackageUrlTemplate, version);
            var stageBase = (double)i / orderedPendingVersions.Count;
            progressChanged?.Invoke(stageBase, $"正在下载补丁 {version}");

            var packagePath = Path.Combine(Path.GetTempPath(), $"mcpatch-{version}-{Guid.NewGuid():N}.zip");
            try
            {
                DownloadFileWithRetry(packageUrl, packagePath, cancellationToken);
                progressChanged?.Invoke(stageBase + 0.2d / orderedPendingVersions.Count, $"正在应用补丁 {version}");

                ApplyPatchPackage(context, packagePath, cancellationToken);
                WriteCurrentVersion(context, version);

                progressChanged?.Invoke((double)(i + 1) / orderedPendingVersions.Count, $"补丁 {version} 已完成");
            }
            finally
            {
                TryDeleteFile(packagePath);
            }
        }
    }

    private static void ValidateContext(McPatchUpdateContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(context.Endpoints);

        if (string.IsNullOrWhiteSpace(context.RootPath))
        {
            throw new ArgumentException("RootPath 不能为空", nameof(context));
        }
    }

    private string GetStringWithRetry(
        string url,
        Action<McPatchListStatus, int, string?>? statusChanged,
        CancellationToken cancellationToken)
    {
        Exception? lastError = null;

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (attempt == 1)
            {
                statusChanged?.Invoke(McPatchListStatus.FirstFetching, 0, null);
            }
            else
            {
                statusChanged?.Invoke(McPatchListStatus.Retrying, attempt - 1, lastError?.Message);
            }

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                using var response = _httpClient.Send(request, cancellationToken);
                response.EnsureSuccessStatusCode();
                var result = response.Content.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult();
                statusChanged?.Invoke(McPatchListStatus.Succeeded, attempt - 1, null);
                return result;
            }
            catch (Exception ex)
            {
                lastError = ex;
                if (attempt >= 3)
                {
                    statusChanged?.Invoke(McPatchListStatus.Failed, attempt - 1, ex.Message);
                    throw;
                }

                WaitForRetry(cancellationToken);
            }
        }

        throw lastError ?? new InvalidOperationException("获取更新列表失败");
    }

    private void DownloadFileWithRetry(string url, string targetPath, CancellationToken cancellationToken)
    {
        Exception? lastError = null;

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                using var response = _httpClient.Send(request, cancellationToken);
                response.EnsureSuccessStatusCode();

                using var responseStream = response.Content.ReadAsStreamAsync(cancellationToken).GetAwaiter().GetResult();
                Directory.CreateDirectory(Path.GetDirectoryName(targetPath) ?? Path.GetTempPath());
                using var output = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None);
                responseStream.CopyTo(output);
                return;
            }
            catch (Exception ex)
            {
                lastError = ex;
                if (attempt >= 3)
                {
                    throw;
                }

                WaitForRetry(cancellationToken);
            }
        }

        throw lastError ?? new InvalidOperationException("下载补丁失败");
    }

    private void ApplyPatchPackage(McPatchUpdateContext context, string packagePath, CancellationToken cancellationToken)
    {
        using var archive = ZipFile.OpenRead(packagePath);
        var metaEntry = archive.GetEntry(".mcpatch-meta.json")
                       ?? throw new InvalidDataException("补丁包缺少 .mcpatch-meta.json");

        McPatchMeta meta;
        using (var metaStream = metaEntry.Open())
        using (var reader = new StreamReader(metaStream, Encoding.UTF8, true))
        {
            var metaText = reader.ReadToEnd();
            meta = JsonSerializer.Deserialize<McPatchMeta>(metaText, JsonOptions)
                   ?? throw new InvalidDataException("补丁元数据无法解析");
        }

        foreach (var oldFile in meta.OldFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = ResolvePatchPath(context, oldFile);
            DeleteFileWithRetry(path, cancellationToken);
        }

        foreach (var oldFolder in meta.OldFolders)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = ResolvePatchPath(context, oldFolder);
            DeleteDirectoryWithRetry(path, cancellationToken);
        }

        foreach (var newFolder in meta.NewFolders)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = ResolvePatchPath(context, newFolder);
            Directory.CreateDirectory(path);
        }

        foreach (var newFile in meta.NewFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ApplyNewFile(context, archive, newFile, cancellationToken);
        }
    }

    private void ApplyNewFile(McPatchUpdateContext context, ZipArchive archive, McPatchMetaFile file, CancellationToken cancellationToken)
    {
        if (file is null || string.IsNullOrWhiteSpace(file.Path))
        {
            return;
        }

        if (!HasData(file.Mode))
        {
            return;
        }

        var entryPath = file.Path.Replace('\\', '/');
        var entry = archive.GetEntry(entryPath) ?? archive.GetEntry(entryPath.TrimStart('/'));
        if (entry is null)
        {
            throw new InvalidDataException($"补丁包缺少数据条目：{file.Path}");
        }

        var compressedData = ReadEntryBytes(entry);
        VerifySha1(compressedData, file.BzippedHash, $"{file.Path} 的 bzipped-hash 不匹配");

        var targetPath = ResolvePatchPath(context, file.Path);
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath) ?? context.RootPath);

        var tempPath = targetPath + ".mcpatchtmp";
        TryDeleteFile(tempPath);

        long rawLength = 0;
        string? actualRawHash;
        using (var compressedStream = new MemoryStream(compressedData, writable: false))
        using (var prefixedStream = new PrefixStream(new byte[] { 0x42, 0x5A }, compressedStream))
        using (var bzipStream = new BZip2InputStream(prefixedStream))
        using (var output = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var sha1 = SHA1.Create())
        {
            var buffer = new byte[81920];

            while (true)
            {
                var read = bzipStream.Read(buffer, 0, buffer.Length);
                if (read <= 0)
                {
                    break;
                }

                output.Write(buffer, 0, read);
                sha1.TransformBlock(buffer, 0, read, null, 0);
                rawLength += read;
            }

            sha1.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            actualRawHash = ToSha1Hex(sha1.Hash ?? Array.Empty<byte>());
        }

        if (file.RawLength > 0 && rawLength != file.RawLength)
        {
            throw new InvalidDataException($"{file.Path} 的 raw-length 不匹配，期望 {file.RawLength}，实际 {rawLength}");
        }

        var expectedRawHash = string.IsNullOrWhiteSpace(file.RawHash) ? file.NewHash : file.RawHash;
        if (!string.IsNullOrWhiteSpace(expectedRawHash) &&
            !string.Equals(actualRawHash, expectedRawHash.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"{file.Path} 的 raw-hash 不匹配");
        }

        ReplaceFileWithRetry(tempPath, targetPath, file.Path, cancellationToken);
    }

    private static byte[] ReadEntryBytes(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    private static void VerifySha1(byte[] data, string? expectedHash, string errorMessage)
    {
        if (string.IsNullOrWhiteSpace(expectedHash))
        {
            return;
        }

        using var sha1 = SHA1.Create();
        var hash = sha1.ComputeHash(data);
        var actual = ToSha1Hex(hash);
        if (!actual.Equals(expectedHash.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(errorMessage);
        }
    }

    private static string ToSha1Hex(byte[] hash)
    {
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static bool HasData(string? mode)
    {
        if (string.IsNullOrWhiteSpace(mode))
        {
            return false;
        }

        var normalized = mode.Trim().ToLowerInvariant();
        return normalized is "f" or "m" or "fill" or "modify";
    }

    private static List<string> BuildPendingVersionList(IReadOnlyList<string> allVersions, string currentVersion)
    {
        if (allVersions.Count == 0)
        {
            return new List<string>();
        }

        if (string.IsNullOrWhiteSpace(currentVersion))
        {
            return allVersions.ToList();
        }

        var index = -1;
        for (var i = 0; i < allVersions.Count; i++)
        {
            if (!string.Equals(allVersions[i], currentVersion.Trim(), StringComparison.Ordinal))
            {
                continue;
            }

            index = i;
            break;
        }
        if (index < 0)
        {
            return allVersions.ToList();
        }

        if (index >= allVersions.Count - 1)
        {
            return new List<string>();
        }

        return allVersions.Skip(index + 1).ToList();
    }

    private static string BuildPackageUrl(string template, string version)
    {
        if (string.IsNullOrWhiteSpace(template))
        {
            template = McPatchEndpointOptions.DefaultPackageUrlTemplate;
        }

        return template.Replace("{version}", version);
    }

    private static void WaitForRetry(CancellationToken cancellationToken)
    {
        using var waiter = new ManualResetEventSlim(false);
        waiter.Wait(TimeSpan.FromSeconds(5), cancellationToken);
    }

    private static void WaitForShortRetry(int attempt, CancellationToken cancellationToken)
    {
        var delay = TimeSpan.FromMilliseconds(250 * attempt);
        using var waiter = new ManualResetEventSlim(false);
        waiter.Wait(delay, cancellationToken);
    }

    private static string ReadCurrentVersion(McPatchUpdateContext context)
    {
        foreach (var path in EnumerateVersionStateFiles(context))
        {
            if (!File.Exists(path))
            {
                continue;
            }

            var value = File.ReadAllText(path, Encoding.UTF8).Trim();
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return string.Empty;
    }

    private static void WriteCurrentVersion(McPatchUpdateContext context, string version)
    {
        var primaryPath = Path.Combine(context.RootPath, "mc-patch-version.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(primaryPath) ?? context.RootPath);
        File.WriteAllText(primaryPath, version + Environment.NewLine, Encoding.UTF8);
    }

    private static IEnumerable<string> EnumerateVersionStateFiles(McPatchUpdateContext context)
    {
        if (!string.IsNullOrWhiteSpace(context.RootPath))
        {
            yield return Path.Combine(context.RootPath, "mc-patch-version.txt");
        }
    }

    private static string ResolvePatchPath(McPatchUpdateContext context, string patchPath)
    {
        var normalized = patchPath.Replace('/', '\\').Trim();
        while (normalized.StartsWith("\\", StringComparison.Ordinal))
        {
            normalized = normalized[1..];
        }

        var selectedPrefix = $"versions\\{context.SelectedVersionName}\\";
        if (!string.IsNullOrWhiteSpace(context.SelectedVersionName) &&
            normalized.StartsWith($".minecraft\\{selectedPrefix}", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[$".minecraft\\{selectedPrefix}".Length..];
        }
        if (!string.IsNullOrWhiteSpace(context.SelectedVersionName) &&
            normalized.StartsWith(selectedPrefix, StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[selectedPrefix.Length..];
        }

        if (normalized.StartsWith(".minecraft\\", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[".minecraft\\".Length..];
        }

        return CombineSafe(context.RootPath, normalized);
    }

    private static string CombineSafe(string root, string relative)
    {
        var rootFull = Path.GetFullPath(root);
        if (!rootFull.EndsWith(Path.DirectorySeparatorChar))
        {
            rootFull += Path.DirectorySeparatorChar;
        }

        var full = Path.GetFullPath(Path.Combine(rootFull, relative));
        if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"非法路径：{relative}");
        }

        return full;
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // ignore
        }
    }

    private static void DeleteFileWithRetry(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return;
        }

        Exception? lastError = null;
        for (var attempt = 1; attempt <= FileOpRetryCount; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReadOnly) != 0)
                {
                    File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
                }

                File.Delete(path);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                lastError = ex;
                if (attempt >= FileOpRetryCount)
                {
                    throw new IOException($"无法删除文件（可能被占用）：{path}", ex);
                }

                WaitForShortRetry(attempt, cancellationToken);
            }
        }

        throw new IOException($"无法删除文件（可能被占用）：{path}", lastError);
    }

    private static void DeleteDirectoryWithRetry(string path, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        Exception? lastError = null;
        for (var attempt = 1; attempt <= FileOpRetryCount; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                Directory.Delete(path, true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                lastError = ex;
                if (attempt >= FileOpRetryCount)
                {
                    throw new IOException($"无法删除文件夹（可能被占用）：{path}", ex);
                }

                WaitForShortRetry(attempt, cancellationToken);
            }
        }

        throw new IOException($"无法删除文件夹（可能被占用）：{path}", lastError);
    }

    private static void ReplaceFileWithRetry(
        string tempPath,
        string targetPath,
        string patchDisplayPath,
        CancellationToken cancellationToken)
    {
        Exception? lastError = null;
        var backupPath = targetPath + ".mcpatchbak";

        for (var attempt = 1; attempt <= FileOpRetryCount; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                TryDeleteFile(backupPath);

                if (File.Exists(targetPath))
                {
                    var attributes = File.GetAttributes(targetPath);
                    if ((attributes & FileAttributes.ReadOnly) != 0)
                    {
                        File.SetAttributes(targetPath, attributes & ~FileAttributes.ReadOnly);
                    }

                    File.Replace(tempPath, targetPath, backupPath, true);
                    TryDeleteFile(backupPath);
                }
                else
                {
                    File.Move(tempPath, targetPath, false);
                }

                return;
            }
            catch (Exception ex) when (ex is System.IO.IOException or System.UnauthorizedAccessException)
            {
                lastError = ex;
                if (!File.Exists(tempPath) && File.Exists(targetPath))
                {
                    return;
                }

                if (attempt >= FileOpRetryCount)
                {
                    throw new IOException(
                        $"无法写入补丁文件（可能被占用）：{patchDisplayPath} -> {targetPath}。请先关闭正在运行的 Minecraft、Java 进程或相关文件预览后重试。",
                        ex);
                }

                WaitForShortRetry(attempt, cancellationToken);
            }
        }

        throw new IOException(
            $"无法写入补丁文件（可能被占用）：{patchDisplayPath} -> {targetPath}",
            lastError);
    }

    private sealed class PrefixStream : Stream
    {
        private readonly byte[] _prefix;
        private readonly Stream _inner;
        private int _offset;

        public PrefixStream(byte[] prefix, Stream inner)
        {
            _prefix = prefix;
            _inner = inner;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_offset < _prefix.Length)
            {
                var prefixCount = Math.Min(count, _prefix.Length - _offset);
                Buffer.BlockCopy(_prefix, _offset, buffer, offset, prefixCount);
                _offset += prefixCount;
                return prefixCount;
            }

            return _inner.Read(buffer, offset, count);
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class McPatchMeta
    {
        [JsonPropertyName("new-folders")]
        public List<string> NewFolders { get; set; } = new();

        [JsonPropertyName("old-files")]
        public List<string> OldFiles { get; set; } = new();

        [JsonPropertyName("old-folders")]
        public List<string> OldFolders { get; set; } = new();

        [JsonPropertyName("new-files")]
        public List<McPatchMetaFile> NewFiles { get; set; } = new();
    }

    private sealed class McPatchMetaFile
    {
        [JsonPropertyName("mode")]
        public string Mode { get; set; } = string.Empty;

        [JsonPropertyName("path")]
        public string Path { get; set; } = string.Empty;

        [JsonPropertyName("new-hash")]
        public string? NewHash { get; set; }

        [JsonPropertyName("bzipped-hash")]
        public string? BzippedHash { get; set; }

        [JsonPropertyName("raw-hash")]
        public string? RawHash { get; set; }

        [JsonPropertyName("raw-length")]
        public long RawLength { get; set; }
    }
}
