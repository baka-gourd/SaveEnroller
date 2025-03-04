using System.Security.Cryptography;

namespace SaveEnroller.Daemon
{
    /// <summary>
    /// Watches for save file changes and manages versioning by storing complete files for each version.
    /// Uses shortened SHA1 hashes (first 4 + last 4 characters) for filenames to reduce length,
    /// while preserving full SHA1 hashes in the CSV tracking records.
    /// </summary>
    public class SaveWatcher
    {
        private FileSystemWatcher FileSystemWatcher { get; set; }
        private DirectoryInfo TargetStorageDirectory { get; set; }
        private DirectoryInfo VersionsDirectory { get; set; }
        private TrackHelper Tracker { get; set; }

        public SaveWatcher(string savePath, string storagePath, string trackFile)
        {
            FileSystemWatcher = new FileSystemWatcher
            {
                Path = savePath,
                EnableRaisingEvents = true,
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName |
                               NotifyFilters.CreationTime | NotifyFilters.FileName
            };
            FileSystemWatcher.Filters.Add("*.cok");
            FileSystemWatcher.Filters.Add("*.cok.cid");
            FileSystemWatcher.Changed += OnChanged;
            TargetStorageDirectory = new DirectoryInfo(storagePath);
            if (!TargetStorageDirectory.Exists)
            {
                TargetStorageDirectory.Create();
            }

            VersionsDirectory = TargetStorageDirectory.CreateSubdirectory("versions");
            Tracker = new TrackHelper(trackFile);

            ScanOnStart(savePath);
        }

        private void ScanOnStart(string savePath)
        {
            var coks = Directory.GetFiles(savePath, "*.cok");
            var cids = Directory.GetFiles(savePath, "*.cok.cid");
            foreach (var file in coks.Concat(cids))
            {
                try
                {
                    using var fs = WaitUntilFileIsReady(file);
                    if (!Util.IsZipFileValid(fs)) continue;
                    var sha = CalculateSha1(fs);
                    // During initialization, check if it's a new record
                    var first = Tracker.UpdateRecord(Path.GetFileName(file), sha, out var duplicate);
                    if (!duplicate)
                    {
                        StoreVersion(fs, first);
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine(e);
                }
            }
        }

        private void OnChanged(object sender, FileSystemEventArgs e)
        {
            Console.WriteLine($"文件 {e.ChangeType}: {e.FullPath}");
            var file = e.FullPath;
            try
            {
                using var fs = WaitUntilFileIsReady(file);
                if (!Util.IsZipFileValid(fs)) return;
                var sha = CalculateSha1(fs);
                fs.Seek(0, SeekOrigin.Begin);
                var first = Tracker.UpdateRecord(Path.GetFileName(file), sha, out var duplicate);
                if (!duplicate)
                {
                    StoreVersion(fs, first);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
            }
        }

        private string CalculateSha1(Stream fs)
        {
            var hash = SHA1.HashData(fs);
            return string.Concat(hash.Select(b => b.ToString("x2")));
        }

        /// <summary>
        /// Shortens a SHA1 hash for use in filenames by keeping only the first 4 and last 4 characters
        /// </summary>
        private string ShortenSha1ForFilename(string sha1)
        {
            if (string.IsNullOrEmpty(sha1) || sha1.Length < 8)
                return sha1;

            return sha1.Substring(0, 4) + sha1.Substring(sha1.Length - 4, 4);
        }

        /// <summary>
        /// Stores a new version of a save file with its SHA1 hash as the filename.
        /// </summary>
        /// <param name="fs">The file stream to store</param>
        /// <param name="isNewFile">Indicates if this is a new file or an update to an existing file</param>
        private void StoreVersion(Stream sourceStream, bool isNewFile)
        {
            // 确保从文件开始读取
            sourceStream.Seek(0, SeekOrigin.Begin);
            // 计算文件内容的 SHA1 哈希值
            string sha1 = CalculateSha1(sourceStream);

            // 根据 SHA1 查找对应的记录（确保 Tracker 中已存在该记录）
            var record = Tracker.Records.Values.FirstOrDefault(r => r.Versions.LastOrDefault() == sha1);
            if (record == null)
            {
                throw new Exception("Record not found for the given stream");
            }
            if (string.IsNullOrEmpty(sha1))
            {
                throw new Exception("No version found in record");
            }

            // 根据缩短后的 SHA1 构造目标版本文件路径
            var versionPath = Path.Combine(VersionsDirectory.FullName, $"{ShortenSha1ForFilename(sha1)}.save");

            // 如果该版本文件尚不存在，则直接从源流复制文件内容
            if (!File.Exists(versionPath))
            {
                sourceStream.Seek(0, SeekOrigin.Begin);
                using (var outputStream = File.Create(versionPath))
                {
                    sourceStream.CopyTo(outputStream);
                }
            }
        }


        /// <summary>
        /// Retrieves a stored save file as a stream based on its SHA1 hash.
        /// </summary>
        /// <param name="fileName">The original filename (for error reporting)</param>
        /// <param name="sha1">The SHA1 hash of the version to retrieve</param>
        /// <returns>A stream containing the saved file content</returns>
        private Stream GetStoredFileAsStream(string fileName, string sha1)
        {
            // Simply return the saved file directly based on its SHA1
            string versionPath = Path.Combine(VersionsDirectory.FullName, $"{ShortenSha1ForFilename(sha1)}.save");

            if (!File.Exists(versionPath))
            {
                throw new Exception($"Version {sha1} not found for {fileName}");
            }

            return File.OpenRead(versionPath);
        }

        /// <summary>
        /// Retrieves a stored save file as a byte array based on its SHA1 hash.
        /// Maintained for backward compatibility.
        /// </summary>
        /// <param name="fileName">The original filename (for error reporting)</param>
        /// <param name="sha1">The SHA1 hash of the version to retrieve</param>
        /// <param name="unused">Parameter kept for backward compatibility, not used</param>
        /// <returns>The file content as a byte array</returns>
        private byte[] GetStoredFile(string fileName, string sha1, bool unused)
        {
            using var stream = GetStoredFileAsStream(fileName, sha1);
            using var memoryStream = new MemoryStream();
            stream.CopyTo(memoryStream);
            return memoryStream.ToArray();
        }

        private FileStream WaitUntilFileIsReady(string filePath, int retryDelayMilliseconds = 1000, int maxRetries = 10)
        {
            var retries = 0;
            while (true)
            {
                try
                {
                    var stream = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.None);
                    return stream;
                }
                catch (IOException)
                {
                    if (++retries > maxRetries)
                    {
                        throw new Exception("File is locked and could not be opened within the timeout period.");
                    }

                    Thread.Sleep(retryDelayMilliseconds);
                }
            }
        }
    }
}