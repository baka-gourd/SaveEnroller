using System.Security.Cryptography;
using System.Timers;

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
        private System.Timers.Timer CleanupTimer { get; set; }

        public SaveWatcher(string savePath, string storagePath, string trackFile, string timeFile)
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
            FileSystemWatcher.Deleted += OnDeleted;
            TargetStorageDirectory = new DirectoryInfo(storagePath);
            if (!TargetStorageDirectory.Exists)
            {
                TargetStorageDirectory.Create();
            }

            VersionsDirectory = TargetStorageDirectory.CreateSubdirectory("versions");
            Tracker = new TrackHelper(trackFile, timeFile);

            CleanupTimer = new System.Timers.Timer(TimeSpan.FromMinutes(5).TotalMilliseconds)
            {
                AutoReset = true,
            };
            CleanupTimer.Elapsed += (_, _) => RunCleanup();
            CleanupTimer.Start();

            // Run initial scan
            ScanOnStart(savePath);

            // Run initial cleanup after scan
            RunCleanup();
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

        /// <summary>
        /// Handles file deletion events from the FileSystemWatcher.
        /// Marks the file as deleted in the tracking system.
        /// </summary>
        /// <param name="sender">Source of the event</param>
        /// <param name="e">Event data containing file path and change information</param>
        private void OnDeleted(object sender, FileSystemEventArgs e)
        {
            Console.WriteLine($"File deleted: {e.FullPath}");
            // Mark the file as deleted in the tracking system
            Tracker.MarkFileAsDeleted(Path.GetFileName(e.FullPath));
        }

        /// <summary>
        /// Handles file change events from the FileSystemWatcher.
        /// Records the new file version and stores it if it's not a duplicate.
        /// </summary>
        /// <param name="sender">Source of the event</param>
        /// <param name="e">Event data containing file path and change information</param>
        private void OnChanged(object sender, FileSystemEventArgs e)
        {
            Console.WriteLine($"File {e.ChangeType}: {e.FullPath}");
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

        /// <summary>
        /// Calculates the SHA1 hash of a file stream
        /// </summary>
        /// <param name="fs">The file stream to hash</param>
        /// <returns>SHA1 hash as a hexadecimal string</returns>
        private string CalculateSha1(Stream fs)
        {
            fs.Seek(0, SeekOrigin.Begin);
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
            // Ensure reading from the beginning of the file
            sourceStream.Seek(0, SeekOrigin.Begin);
            // Calculate SHA1 hash of the file content
            string sha1 = CalculateSha1(sourceStream);

            // Find the corresponding record in the tracker (ensure it exists)
            var record = Tracker.Records.Values.FirstOrDefault(r => r.Versions.LastOrDefault() == sha1);
            if (record == null)
            {
                throw new Exception("Record not found for the given stream");
            }

            if (string.IsNullOrEmpty(sha1))
            {
                throw new Exception("No version found in record");
            }

            // Construct target version file path using shortened SHA1
            var versionPath = Path.Combine(VersionsDirectory.FullName, $"{ShortenSha1ForFilename(sha1)}.save");

            // If this version file doesn't exist yet, copy content from source stream
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

        /// <summary>
        /// Waits until a file is available for reading, with retry logic to handle locked files
        /// </summary>
        /// <param name="filePath">Path to the file to open</param>
        /// <param name="retryDelayMilliseconds">Delay between retry attempts in milliseconds</param>
        /// <param name="maxRetries">Maximum number of retry attempts</param>
        /// <returns>An open FileStream for reading the file</returns>
        /// <exception cref="Exception">Thrown if the file cannot be opened after the maximum retries</exception>
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

        /// <summary>
        /// Runs the version cleanup process to manage disk space and retain important versions
        /// according to the retention policy
        /// </summary>
        private void RunCleanup()
        {
            try
            {
                Console.WriteLine("Starting version cleanup process...");
                Tracker.CleanupOldVersions(VersionsDirectory.FullName);
                Console.WriteLine("Version cleanup completed successfully");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error during version cleanup: {ex.Message}");
            }
        }
    }
}
