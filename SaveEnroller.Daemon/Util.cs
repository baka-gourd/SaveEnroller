using System.IO.Hashing;
using ICSharpCode.SharpZipLib.Zip;

namespace SaveEnroller.Daemon;

public class Util
{
    public static bool IsZipFileValid(FileStream fs)
    {
        try
        {
            fs.Seek(0, SeekOrigin.Begin);

            // Pass 'true' for leaveOpen parameter to prevent ZipFile from closing the stream
            using var zipFile = new ZipFile(fs, true);
            List<string> failedEntries = [];
            failedEntries.AddRange(from ZipEntry? entry in zipFile
                                   where entry.IsFile
                                   let expectedCrc32 = (uint)entry.Crc
                                   let actualCrc32 = ComputeCrc32(zipFile, entry)
                                   where expectedCrc32 != actualCrc32
                                   select entry.Name);
            return failedEntries.Count <= 0;
            //Console.WriteLine($"{zipFilePath} failed:");
            //foreach (var file in failedEntries)
            //{
            //    Console.WriteLine($" - {file}");
            //}
        }
        catch (Exception)
        {
            //Console.WriteLine($"{zipFilePath}: {ex.Message}");
            return false;
        }
    }

    private static uint ComputeCrc32(ZipFile zipFile, ZipEntry entry)
    {
        using var zipStream = zipFile.GetInputStream(entry);
        var crc32 = new Crc32();
        var buffer = new byte[8192];
        int bytesRead;
        while ((bytesRead = zipStream.Read(buffer, 0, buffer.Length)) > 0)
        {
            crc32.Append(buffer.AsSpan(0, bytesRead));
        }

        return BitConverter.ToUInt32(crc32.GetHashAndReset(), 0);
    }

    public static long GetDirectorySize(string folderPath)
    {
        if (!Directory.Exists(folderPath)) return 0;
        long size = 0;
        string[] files = Directory.GetFiles(folderPath, "*", SearchOption.AllDirectories);
        foreach (string file in files)
        {
            try
            {
                size += new FileInfo(file).Length;
            }
            catch { /* 忽略无法访问的文件 */ }
        }
        return size;
    }
}
