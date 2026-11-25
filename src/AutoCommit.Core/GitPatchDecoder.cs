using System;
using System.Text;

namespace AutoCommit.Core;

// Git emits blob bytes unchanged. Detect each patch line separately so deleted and
// added lines can even use different encodings. Bound buffering for generated files.
internal sealed class GitPatchDecoder
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);
    private readonly byte[] line = new byte[64 * 1024];
    private readonly char[] chars = new char[64 * 1024 + 2];
    private int length;
    private Decoder? continuation;

    public string Read(byte[] bytes, int count)
    {
        var result = new StringBuilder();
        for (var start = 0; start < count;) {
            var available = Math.Min(count - start, line.Length - length);
            var lf = Array.IndexOf(bytes, (byte)'\n', start, available);
            var take = lf < 0 ? available : lf - start + 1;
            Buffer.BlockCopy(bytes, start, line, length, take);
            length += take;
            start += take;
            if (lf >= 0 || length == line.Length) Decode(result, lf >= 0);
        }
        if (count == 0) Decode(result, true);
        return result.ToString();
    }

    private void Decode(StringBuilder result, bool endOfLine)
    {
        if (continuation == null && Array.FindIndex(line, 0, length, b => b >= 128) < 0) {
            result.Append(Encoding.ASCII.GetString(line, 0, length));
        } else {
            var decoder = continuation ?? ChooseEncoding(endOfLine).GetDecoder();
            var count = decoder.GetChars(line, 0, length, chars, 0, endOfLine);
            result.Append(chars, 0, count);
            continuation = endOfLine ? null : decoder;
        }
        length = 0;
    }

    private Encoding ChooseEncoding(bool endOfLine)
    {
        try {
            StrictUtf8.GetDecoder().GetCharCount(line, 0, length, endOfLine);
        } catch (DecoderFallbackException) {
            try {
                var gbk = Encoding.GetEncoding(936, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
                gbk.GetDecoder().GetCharCount(line, 0, length, endOfLine);
                return Encoding.GetEncoding(936);
            } catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException) {
                // Retain the existing UTF-8 replacement behavior for unrecognized bytes.
            }
        }
        return Encoding.UTF8;
    }
}
