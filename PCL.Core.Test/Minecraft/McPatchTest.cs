using System;
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
            """
            { "versionListUrl": "http://a.example/versions.txt" }
            """);
        var found = McPatchConfig.TryLoadEndpoints(dir.Path, dir.Path, out var endpoints);
        Assert.IsTrue(found);
        Assert.AreEqual(1, endpoints.Count);
        Assert.AreEqual("", endpoints[0].Name);
        Assert.AreEqual("http://a.example/versions.txt", endpoints[0].VersionListUrl);
        Assert.AreEqual(McPatchEndpoint.DefaultPackageUrlTemplate, endpoints[0].PackageUrlTemplate);
        Assert.AreEqual("a.example/versions.txt", endpoints[0].DisplayName); // name 空 → host+path（蓝图 §2.1）
    }

    [TestMethod]
    public void LoadEndpoints_Array_LoadsMultiple()
    {
        using var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Path, McPatchConfig.ConfigFileName),
            """
            [{ "name": "S1", "versionListUrl": "http://s1/v.txt", "packageUrlTemplate": "http://s1/{version}.zip" },
             { "name": "S2", "versionListUrl": "http://s2/v.txt", "packageUrlTemplate": "http://s2/{version}.zip" }]
            """);
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
            """
            { "name": "INSTANCE" }
            """);
        File.WriteAllText(Path.Combine(root.Path, McPatchConfig.ConfigFileName),
            """
            { "name": "ROOT" }
            """);
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
        Assert.ThrowsExactly<McPatchException>(() =>
            McPatchPath.MapRealPath(@"C:\mc\", "1.20.1", @"..\..\evil.jar"));
    }

    // ---------- ApplyPackage（蓝图 §8 补充） ----------

    [TestMethod]
    public void ApplyPackage_WritesFileWithPrefixStripAndHashVerify()
    {
        using var root = new TempDir();
        using var zip = TempFile(".zip");
        var raw = "hello mcpatch"u8.ToArray();
        CreatePatchZip(zip.Path, ".minecraft/mods/Mod.jar", raw, oldFiles: [], oldFolders: [],
            newFolders: ["minecraft/config"], correctHashes: true);

        var context = NewContext(root.Path, "1.20.1");
        McPatchService.ApplyPackage(context, "1.0", zip.Path, 0, 1, null);

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
        CreatePatchZip(zip.Path, ".minecraft/new.txt", "n"u8.ToArray(),
            oldFiles: [".minecraft/old.txt"], oldFolders: [".minecraft/olddir"],
            newFolders: [], correctHashes: true);

        McPatchService.ApplyPackage(NewContext(root.Path, "1.20.1"), "1.0", zip.Path, 0, 1, null);
        Assert.IsFalse(File.Exists(Path.Combine(root.Path, "old.txt")));
        Assert.IsFalse(Directory.Exists(Path.Combine(root.Path, "olddir")));
    }

    [TestMethod]
    public void ApplyPackage_WrongBzippedHash_Throws()
    {
        using var root = new TempDir();
        using var zip = TempFile(".zip");
        CreatePatchZip(zip.Path, ".minecraft/mods/Mod.jar", "data"u8.ToArray(), [], [], [], correctHashes: false);
        Assert.ThrowsExactly<McPatchException>(() =>
            McPatchService.ApplyPackage(NewContext(root.Path, "1.20.1"), "1.0", zip.Path, 0, 1, null));
    }

    [TestMethod]
    public void ApplyPackage_WrongRawLength_Throws()
    {
        using var root = new TempDir();
        using var zip = TempFile(".zip");
        var raw = "0123456789"u8.ToArray();
        // 构造一个 raw-length 故意错误的包
        CreatePatchZip(zip.Path, ".minecraft/mods/Mod.jar", raw, [], [], [],
            correctHashes: true, wrongRawLength: true);
        Assert.ThrowsExactly<McPatchException>(() =>
            McPatchService.ApplyPackage(NewContext(root.Path, "1.20.1"), "1.0", zip.Path, 0, 1, null));
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
        using (var bzip2 = new BZip2OutputStream(compressed, 1) { IsStreamOwner = false }) // 结束后不关闭底层流
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

    private static McTempZip TempFile(string ext)
    {
        return new McTempZip(Path.Combine(Path.GetTempPath(), "mcpatch-" + Guid.NewGuid().ToString("N") + ext));
    }

    /// <summary>自动清理的临时补丁包文件。</summary>
    private sealed class McTempZip(string path) : IDisposable
    {
        public string Path => path;
        public void Dispose()
        {
            try { File.Delete(path); } catch { }
        }
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