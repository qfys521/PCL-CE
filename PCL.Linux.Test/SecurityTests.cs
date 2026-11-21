using PCL.Portable;

namespace PCL.Linux.Test;

/// <summary>针对“远程文件名 / 清理数据”等危险操作的安全回归测试。</summary>
[TestClass]
public sealed class SecurityTests
{
    [TestMethod]
    [DataRow("../../.bashrc")]
    [DataRow("../evil.jar")]
    [DataRow("/etc/passwd")]
    [DataRow("sub/name.jar")]
    [DataRow("back\\slash.jar")]
    [DataRow(".")]
    [DataRow("..")]
    public void RejectsUnsafeRemoteFileNames(string fileName)
    {
        var root = Path.Combine(Path.GetTempPath(), "pcl-security-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Assert.ThrowsExactly<InvalidDataException>(() => LocalMods.ResolveContentTarget(root, fileName));
            Assert.ThrowsExactly<InvalidDataException>(() => LocalMods.ResolveExistingCaseInsensitive(root, fileName));
        }
        finally { try { Directory.Delete(root, true); } catch (Exception) { } }
    }

    [TestMethod]
    public void AcceptsSafeRemoteFileNames()
    {
        var root = Path.Combine(Path.GetTempPath(), "pcl-security-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var target = LocalMods.ResolveContentTarget(root, "sodium-fabric-0.6.0.jar");
            Assert.AreEqual(root, Path.GetDirectoryName(target));
            Assert.AreEqual("sodium-fabric-0.6.0.jar", Path.GetFileName(target));
            LocalMods.RequireSafeFileName("资源包-中文名前缀.zip");
        }
        finally { try { Directory.Delete(root, true); } catch (Exception) { } }
    }

    [TestMethod]
    public void CaseVariantTargetStaysInsideDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "pcl-security-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var existing = Path.Combine(root, "JEI.jar");
            File.WriteAllText(existing, "x");
            var resolved = LocalMods.ResolveContentTarget(root, "jei.jar");
            Assert.AreEqual(existing, resolved);
            Assert.IsTrue(LinuxPaths.IsSameOrChild(resolved, root));
        }
        finally { try { Directory.Delete(root, true); } catch (Exception) { } }
    }
}
