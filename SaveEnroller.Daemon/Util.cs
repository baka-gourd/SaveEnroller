using System.IO.Hashing;
using System.Security.Cryptography;
using System.Text;
using ICSharpCode.SharpZipLib.Zip;

namespace SaveEnroller.Daemon;

public class Util
{
    public static bool IsZipFileValid(FileStream fs)
    {
        try
        {
            fs.Seek(0, SeekOrigin.Begin);

            using var zipFile = new ZipFile(fs);
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
}