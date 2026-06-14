using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Updater;

namespace PCL.Core.Test.Updater;

[TestClass]
public class McPatchUpdateServiceTest
{
    [TestMethod]
    public void LoadEndpointOptions_SingleObject_ReturnsDefaultEndpoint()
    {
        var service = new McPatchUpdateService();
        var endpoints = service.LoadEndpointOptions("""
            {
              "name": "主服务器",
              "versionListUrl": "",
              "packageUrlTemplate": ""
            }
            """);

        Assert.AreEqual(1, endpoints.Count);
        Assert.AreEqual("主服务器", endpoints[0].Name);
        Assert.AreEqual(McPatchEndpointOptions.DefaultVersionListUrl, endpoints[0].VersionListUrl);
        Assert.AreEqual(McPatchEndpointOptions.DefaultPackageUrlTemplate, endpoints[0].PackageUrlTemplate);
    }

    [TestMethod]
    public void LoadEndpointOptions_Array_ReturnsMultipleEndpoints()
    {
        var service = new McPatchUpdateService();
        var endpoints = service.LoadEndpointOptions("""
            [
              {
                "name": "A",
                "versionListUrl": "https://example.com/a.txt",
                "packageUrlTemplate": "https://example.com/a/{version}.zip"
              },
              {
                "name": "B",
                "versionListUrl": "https://example.com/b.txt",
                "packageUrlTemplate": "https://example.com/b/{version}.zip"
              }
            ]
            """);

        Assert.AreEqual(2, endpoints.Count);
        Assert.AreEqual("A", endpoints[0].Name);
        Assert.AreEqual("https://example.com/a.txt", endpoints[0].VersionListUrl);
        Assert.AreEqual("B", endpoints[1].Name);
        Assert.AreEqual("https://example.com/b/{version}.zip", endpoints[1].PackageUrlTemplate);
    }

    [TestMethod]
    public void LoadEndpointOptions_InvalidJson_FallsBackToDefaultEndpoint()
    {
        var service = new McPatchUpdateService();
        var endpoints = service.LoadEndpointOptions("not-json");

        Assert.AreEqual(1, endpoints.Count);
        Assert.AreEqual(McPatchEndpointOptions.DefaultVersionListUrl, endpoints[0].VersionListUrl);
        Assert.AreEqual(McPatchEndpointOptions.DefaultPackageUrlTemplate, endpoints[0].PackageUrlTemplate);
    }
}
