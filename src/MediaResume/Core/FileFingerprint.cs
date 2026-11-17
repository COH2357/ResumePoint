using System.Security.Cryptography;

namespace MediaResume.Core;

/// <summary>
/// 文件指纹：size + 首 64KB + 尾 64KB 的 SHA-256。
/// 用途：文件被改名或移动后，仍能通过指纹认出是同一个视频（路径匹配的直接补丁）。
/// </summary>
public static class FileFingerprint
{
    private const int Chunk = 64 * 1024;

    public static string Compute(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, bufferSize: 4096, FileOptions.SequentialScan);
            long len = fs.Length;
            using var sha = SHA256.Create();

            var buf = new byte[Chunk];

            // 把长度混进摘要，降低不同文件撞指纹的概率
            BitConverter.TryWriteBytes(buf, len);
            sha.TransformBlock(buf, 0, 8, null, 0);

            if (len <= 0)
            {
                sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                return Convert.ToHexString(sha.Hash!).ToLowerInvariant();
            }

            int head = (int)Math.Min(Chunk, len);
            ReadExact(fs, buf, head);
            sha.TransformBlock(buf, 0, head, null, 0);

            if (len > Chunk)
            {
                long tailLen = Math.Min(Chunk, len - Chunk);
                fs.Seek(-tailLen, SeekOrigin.End);
                int tail = (int)tailLen;
                ReadExact(fs, buf, tail);
                sha.TransformBlock(buf, 0, tail, null, 0);
            }

            sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            return Convert.ToHexString(sha.Hash!).ToLowerInvariant();
        }
        catch
        {
            return string.Empty;
        }
    }

    private static void ReadExact(Stream s, byte[] dest, int count)
    {
        int off = 0;
        while (off < count)
        {
            int n = s.Read(dest, off, count - off);
            if (n <= 0) break;
            off += n;
        }
    }
}
