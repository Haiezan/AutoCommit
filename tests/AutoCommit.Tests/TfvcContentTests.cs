using System.IO;
using System.Linq;
using System.Text;
using AutoCommit.Core;
using Xunit;

public class TfvcContentTests
{
    [Fact]
    public void LargeFileWithOneEditedLineProducesSmallUnifiedDiff()
    {
        var lines = Enumerable.Range(0, 3200).Select(i => $"int value_{i} = {i}; // sample source text").ToArray();
        var before = ReadText(string.Join("\r\n", lines) + "\r\n", Encoding.Unicode);
        lines[1599] = "int value_1599 = 42; // sample source text";
        var after = ReadText(string.Join("\n", lines) + "\n", Encoding.UTF8);

        var change = TfvcContent.CreateChange("$/Source/StructGraph/file.cpp", "Edit", null, before, after, 100_000, default);
        var hunk = Assert.Single(change.Hunks);
        Assert.True(before.Size + after.Size > 200_000);
        Assert.StartsWith("@@ -1597,7 +1597,7 @@\n", hunk);
        Assert.Contains("-int value_1599 = 1599;", hunk);
        Assert.Contains("+int value_1599 = 42;", hunk);
        Assert.DoesNotContain("value_0 ", hunk);
        Assert.InRange(hunk.Length, 1, 600);
        Assert.False(change.HasOmittedHunks);
        Assert.NotEqual(before.Fingerprint, after.Fingerprint);
    }

    [Fact]
    public void OversizedHunkLeavesRoomForLaterChange()
    {
        var unchanged = string.Concat(Enumerable.Range(0, 12).Select(i => $"context {i}\n"));
        var before = ReadText("before\n" + unchanged + "old ending\n", Encoding.UTF8);
        var after = ReadText(new string('x', 2_000) + "\n" + unchanged + "new ending\n", Encoding.UTF8);
        var change = TfvcContent.CreateChange("$/file", "Edit", null, before, after, 500, default);

        Assert.True(change.HasOmittedHunks);
        Assert.Contains("+new ending\n", Assert.Single(change.Hunks));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NewOrDeletedFileUsesEmptySideOfPatch(bool added)
    {
        var content = ReadText("text without final newline", Encoding.UTF8);
        var change = TfvcContent.CreateChange("$/file", added ? "Add" : "Delete", null,
            added ? null : content, added ? content : null, 1000, default);

        Assert.Equal(added ? "--- /dev/null\n+++ $/file\n" : "--- $/file\n+++ /dev/null\n", change.PatchHeader);
        Assert.Equal(added ? "@@ -0,0 +1,1 @@\n+text without final newline\n\\ No newline at end of file\n" :
            "@@ -1,1 +0,0 @@\n-text without final newline\n\\ No newline at end of file\n", Assert.Single(change.Hunks));
    }

    [Fact]
    public void UsesEachRevisionsCodePageAndHonorsExplicitBinaryType()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var path = Path.GetTempFileName();
        try {
            var gbk = Encoding.GetEncoding(936);
            File.WriteAllBytes(path, gbk.GetBytes("旧步长"));
            var before = TfvcContent.ReadFile(path, true, 936);
            File.WriteAllBytes(path, Encoding.UTF8.GetBytes("新步长"));
            var after = TfvcContent.ReadFile(path, true, 65001);
            var change = TfvcContent.CreateChange("$/file", "Edit, Encoding", null, before, after, 1000, default);
            var hunk = Assert.Single(change.Hunks);
            Assert.Contains("-旧步长", hunk);
            Assert.Contains("+新步长", hunk);

            var binary = TfvcContent.ReadFile(path, true, -1);
            Assert.Empty(TfvcContent.CreateChange("$/binary", "Add", null, null, binary, 1000, default).Hunks);
            Assert.Equal(after.Fingerprint, binary.Fingerprint);
        } finally { File.Delete(path); }
    }

    [Fact]
    public void BinaryAndLargeFileKeepMetadataAndFingerprint()
    {
        var path = Path.GetTempFileName();
        try {
            File.WriteAllBytes(path, new byte[] { 0, 1, 2, 3 });
            var binary = TfvcContent.ReadFile(path, includeText: true);
            var binaryChange = TfvcContent.CreateChange("$/binary", "Add", null, null, binary, 1000, default);
            Assert.Empty(binaryChange.Hunks);
            Assert.Contains("二进制", binaryChange.Metadata);

            var bytes = new byte[1024 * 1024 + 1];
            File.WriteAllBytes(path, bytes);
            var before = TfvcContent.ReadFile(path, includeText: true);
            Assert.Equal(before.Fingerprint, TfvcContent.ReadFile(path, includeText: false).Fingerprint);
            bytes[^1] = 42;
            File.WriteAllBytes(path, bytes);
            var after = TfvcContent.ReadFile(path, includeText: true);
            var largeChange = TfvcContent.CreateChange("$/large", "Edit", null, before, after, 1000, default);
            Assert.Empty(largeChange.Hunks);
            Assert.Contains("1048577 字节", largeChange.Metadata);
            Assert.Contains("超过 1 MiB", largeChange.Metadata);
            Assert.NotEqual(before.Fingerprint, after.Fingerprint);
        } finally { File.Delete(path); }
    }

    private static TfvcContent ReadText(string text, Encoding encoding)
    {
        var path = Path.GetTempFileName();
        try {
            File.WriteAllText(path, text, encoding);
            var content = TfvcContent.ReadFile(path, includeText: true);
            Assert.Equal(content.Fingerprint, TfvcContent.ReadFile(path, includeText: false).Fingerprint);
            return content;
        } finally { File.Delete(path); }
    }
}
