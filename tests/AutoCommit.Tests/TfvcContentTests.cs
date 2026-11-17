using System.IO;
using System.Text;
using AutoCommit.Core;
using Xunit;

public class TfvcContentTests
{
    [Fact]
    public void Utf16SourceIsReadableAndBinaryIsMetadataOnlyButStillHasFingerprint()
    {
        var path = Path.GetTempFileName();
        try {
            File.WriteAllText(path, "中文源代码", Encoding.Unicode);
            var body = new StringBuilder(); var hash = new StringBuilder();
            TfvcContent.AppendFile(path, "基线", body, hash);
            Assert.Contains("中文源代码", body.ToString()); var original = hash.ToString();
            File.WriteAllBytes(path, new byte[] { 0, 1, 2, 3 }); body.Clear(); hash.Clear();
            TfvcContent.AppendFile(path, "本地", body, hash);
            Assert.Contains("仅分析元信息", body.ToString()); Assert.NotEqual(original, hash.ToString());
        } finally { File.Delete(path); }
    }
}
