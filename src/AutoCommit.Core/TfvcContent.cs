using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace AutoCommit.Core;

public static class TfvcContent
{
    public static void AppendFile(string path, string label, StringBuilder body, StringBuilder identity)
    {
        using (var stream = File.OpenRead(path)) using (var sha = SHA256.Create())
            identity.Append('|').Append(Convert.ToBase64String(sha.ComputeHash(stream)));
        var length = new FileInfo(path).Length;
        if (length > 1024 * 1024) { body.AppendLine(label + "：大文件，仅分析元信息，字节数 " + length); return; }
        var bytes = File.ReadAllBytes(path);
        // Explicit encodings prevent binary bytes from being presented as meaningful source.
        string text;
        try {
            if (bytes.Length >= 2 && bytes[0] == 255 && bytes[1] == 254) text = Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
            else if (bytes.Length >= 2 && bytes[0] == 254 && bytes[1] == 255) text = Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
            else text = new UTF8Encoding(false, true).GetString(bytes);
            if (text.Contains("\0")) throw new DecoderFallbackException();
        } catch (DecoderFallbackException) { body.AppendLine(label + "：二进制或非 UTF 编码，仅分析元信息，字节数 " + length); return; }
        body.AppendLine("--- " + label + " ---").AppendLine(text).AppendLine("--- 内容结束 ---");
    }

}
