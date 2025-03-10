using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using nietras.SeparatedValues;
using Timer = System.Timers.Timer;

namespace SaveEnroller.Daemon;

/// <summary>
/// Manages file change tracking by recording file versions in a CSV file.
/// This class handles reading existing records, updating with new versions,
/// and periodically writing changes to the track file.
/// </summary>
internal class TrackHelper : IDisposable, IAsyncDisposable
{
    /// <summary>
    /// Lock object to ensure thread safety between Write and UpdateRecord operations
    /// </summary>
    private readonly Lock _lockObject = new Lock();

    /// <summary>
    /// Path to the CSV file that stores file history records
    /// </summary>
    private string TrackFile { get; set; }

    /// <summary>
    /// Path to the CSV file that stores file state records including timestamps and deletion status
    /// </summary>
    private string TimeFile { get; set; }

    /// <summary>
    /// Dictionary of file history records, keyed by file name
    /// </summary>
    public Dictionary<string, FileHistoryRecord> Records { get; set; }

    /// <summary>
    /// Dictionary of file state records, keyed by SHA1 hash
    /// </summary>
    public Dictionary<string, FileStateRecord> Times { get; set; }

    /// <summary>
    /// Current version counter, incremented when records are modified
    /// </summary>
    private int Version { get; set; }

    /// <summary>
    /// Previous version counter, used to detect when changes need to be written
    /// </summary>
    private int PreviousVersion { get; set; }

    /// <summary>
    /// Timer that triggers periodic writes to the track file
    /// </summary>
    private Timer Timer { get; set; }

    /// <summary>
    /// Writer configuration for creating CSV writers
    /// </summary>
    private readonly Func<SepWriterOptions, SepWriterOptions> _writerConfig;

    /// <summary>
    /// Initializes a new instance of the TrackHelper class.
    /// Creates the track file if it doesn't exist, reads existing records,
    /// and sets up a timer for periodic writes.
    /// </summary>
    /// <param name="trackFile">Path to the CSV file for tracking file history</param>
    /// <param name="timeFile"></param>
    public TrackHelper(string trackFile, string timeFile)
    {
        TrackFile = trackFile;
        TimeFile = timeFile;
        // If the file doesn't exist, create an empty file first
        if (!File.Exists(trackFile))
        {
            File.WriteAllText(trackFile, string.Empty);
        }

        if (!File.Exists(timeFile))
        {
            File.WriteAllText(timeFile, string.Empty);
        }

        ReadAllRecords();
        ReadAllTimes();

        _writerConfig = sep => sep with
        {
            WriteHeader = false,
            CultureInfo = CultureInfo.InvariantCulture,
            Escape = true,
            DisableColCountCheck = true
        };

        Timer = new Timer(TimeSpan.FromSeconds(5))
        {
            AutoReset = true,
        };
        Timer.Elapsed += (_, _) => Write();
        Timer.Start();
    }

    /// <summary>
    /// Reads all records from the track file, parsing each line into a FileHistoryRecord object.
    /// This method initializes the Records dictionary with file tracking information.
    /// </summary>
    [MemberNotNull(nameof(Records))]
    private void ReadAllRecords()
    {
        using var reader = Sep.Default.Reader(sep => sep with
        {
            DisableColCountCheck = true,
            Unescape = true,
            CultureInfo = CultureInfo.InvariantCulture,
            HasHeader = false
        }).FromFile(TrackFile);

        Records = new Dictionary<string, FileHistoryRecord>(reader.ParallelEnumerate(row =>
        {
            var fileName = row[0].ToString();
            var fileBaseSha1 = row[1].ToString();
            List<string> versions = [];
            if (row.ColCount <= 2) return new FileHistoryRecord(fileName, fileBaseSha1, versions);
            for (var i = 2; i < row.ColCount; i++)
            {
                versions.Add(row[i].ToString());
            }

            return new FileHistoryRecord(fileName, fileBaseSha1, versions);
        }).AsParallel().ToDictionary(record => record.FileName));
    }

    /// <summary>
    /// Reads all time records from the time file, parsing each line into a FileStateRecord object.
    /// This method initializes the Times dictionary with file state information including deletion status.
    /// </summary>
    [MemberNotNull(nameof(Times))]
    private void ReadAllTimes()
    {
        using var reader = Sep.Default.Reader(sep => sep with
        {
            DisableColCountCheck = true,
            Unescape = true,
            CultureInfo = CultureInfo.InvariantCulture,
            HasHeader = false
        }).FromFile(TimeFile);

        Times = new Dictionary<string, FileStateRecord>(reader.ParallelEnumerate(row =>
        {
            var sha1 = row[0].ToString();
            var time = row[1].Parse<DateTimeOffset>();
            var state = row[2].Parse<bool>();

            return new FileStateRecord(sha1, time, state);
        }).AsParallel().ToDictionary(record => record.Sha1));
    }

    /// <summary>
    /// Updates the record for the specified file. If the record doesn't exist, a new one is created.
    /// If the record exists, the new SHA1 version is appended (if it's not a duplicate of the last version).
    /// The method increments the Version counter when changes are made to track unsaved modifications.
    /// </summary>
    /// <param name="fileName">The name of the file to update</param>
    /// <param name="newSha1">The SHA1 hash of the latest version of the file</param>
    /// <param name="isDuplicate"></param>
    /// <param name="deleted">Whether the file has been deleted</param>
    /// <returns>Returns true if a new record was created, false if an existing record was updated or no changes were made</returns>
    public bool UpdateRecord(string fileName, string newSha1, out bool isDuplicate, bool deleted = false)
    {
        var currentTime = DateTimeOffset.Now;
        lock (_lockObject)
        {
            if (Records.TryGetValue(fileName, out var record))
            {
                if (record.Versions.LastOrDefault() != newSha1)
                {
                    record.Versions.Add(newSha1);
                    Times.Add(newSha1, new FileStateRecord(newSha1, currentTime, deleted));
                    Version++;
                    isDuplicate = false;
                    return false;
                }
                else
                {
                    isDuplicate = true;
                    return false;
                }
            }
            else
            {
                Records.Add(fileName, new FileHistoryRecord(fileName, newSha1, [newSha1]));
                Times.Add(newSha1, new FileStateRecord(newSha1, currentTime, deleted));
                Version++;
                isDuplicate = false;
                return true;
            }
        }
    }

    /// <summary>
    /// Writes all records to the track file if there have been changes since the last write.
    /// This method is called periodically by the timer and before disposal.
    /// Creates a new writer for each write operation to correctly overwrite the file.
    /// </summary>
    private void Write()
    {
        lock (_lockObject)
        {
            // Only write if there have been changes
            if (Version == PreviousVersion) return;
            PreviousVersion = Version;

            // Create a new writer for each write operation
            using var trackWriter = Sep.Default.Writer(_writerConfig).ToFile(TrackFile);
            using var timeWriter = Sep.Default.Writer(_writerConfig).ToFile(TimeFile);

            foreach (var rec in Records.Values)
            {
                using var row = trackWriter.NewRow();
                row[0].Set(rec.FileName);
                row[1].Set(rec.BaseSha1);
                for (var i = 0; i < rec.Versions.Count; i++)
                {
                    row[i + 2].Set(rec.Versions[i]);
                }
            }

            foreach (var time in Times.Values)
            {
                using var row = timeWriter.NewRow();
                row[0].Set(time.Sha1);
                row[1].Set(time.Time.ToString());
                row[2].Set(time.Deleted.ToString());
            }
        }
    }

    /// <summary>
    /// Disposes resources used by the TrackHelper.
    /// Ensures all pending changes are written to the track file.
    /// </summary>
    public void Dispose()
    {
        Write();
        Timer.Dispose();
    }

    /// <summary>
    /// Asynchronously disposes resources used by the TrackHelper.
    /// Ensures all pending changes are written to the track file.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        Write();
        Timer.Dispose();
        await Task.CompletedTask;
    }

    /// <summary>
    /// Calculates the size of a directory by recursively summing the sizes of all files
    /// </summary>
    /// <param name="folderPath">Path to the directory</param>
    /// <returns>Total size in bytes</returns>
    private static long GetDirectorySize(string folderPath)
    {
        if (!Directory.Exists(folderPath)) return 0;
        long size = 0;
        string[] files = Directory.GetFiles(folderPath, "*", SearchOption.AllDirectories);
        foreach (var file in files)
        {
            try
            {
                size += new FileInfo(file).Length;
            }
            catch { /* Ignore inaccessible files */ }
        }
        return size;
    }

    /// <summary>
    /// Shortens a SHA1 hash for use in filenames by keeping only the first 4 and last 4 characters
    /// </summary>
    private static string ShortenSha1ForFilename(string sha1)
    {
        if (string.IsNullOrEmpty(sha1) || sha1.Length < 8)
            return sha1;

        return sha1.Substring(0, 4) + sha1.Substring(sha1.Length - 4, 4);
    }

    /// <summary>
    /// Determines the backup file path based on the version hash
    /// </summary>
    /// <param name="versionHash">Version hash (SHA1)</param>
    /// <param name="versionsDirectory">Directory where versions are stored</param>
    /// <returns>Path to the backup file</returns>
    private static string GetBackupFilePath(string versionHash, string versionsDirectory)
    {
        // Use the same shortened hash format as in SaveWatcher
        return Path.Combine(versionsDirectory, $"{ShortenSha1ForFilename(versionHash)}.save");
    }

    /// <summary>
    /// Split versions into two lists based on timestamp: versions before/equal to the threshold and versions after it
    /// </summary>
    /// <param name="sortedVersions">List of version hashes, sorted by time (oldest first)</param>
    /// <param name="threshold">Time threshold for splitting</param>
    /// <returns>Tuple of (versions before or equal to threshold, versions after threshold)</returns>
    private (List<string> olderOrEqual, List<string> newer) SplitByTime(List<string> sortedVersions, DateTime threshold)
    {
        List<string> olderOrEqual = new List<string>();
        List<string> newer = new List<string>();

        foreach (var ver in sortedVersions)
        {
            if (Times.ContainsKey(ver))
            {
                if (Times[ver].Time <= threshold)
                    olderOrEqual.Add(ver);
                else
                    newer.Add(ver);
            }
        }

        return (olderOrEqual, newer);
    }

    /// <summary>
    /// Marks a file as deleted in the Times dictionary
    /// </summary>
    /// <param name="fileName">The name of the file that was deleted</param>
    public void MarkFileAsDeleted(string fileName)
    {
        lock (_lockObject)
        {
            if (Records.TryGetValue(fileName, out var record))
            {
                foreach (var versionSha1 in record.Versions)
                {
                    if (Times.TryGetValue(versionSha1, out var timeRecord))
                    {
                        timeRecord.Deleted = true;
                    }
                }
                Version++; // Increment to trigger write
            }
        }
    }

    /// <summary>
    /// Clean up old versions according to the retention policy:
    /// - Keep all versions from the last day
    /// - Keep 3 versions per day for 1-7 days old versions
    /// - Keep 4 versions total for 7-30 days old versions
    /// - Keep only 1 version per month for versions older than 30 days
    /// - If total backup size exceeds 10GB, delete older versions (preferably non-monthly archives)
    /// </summary>
    public void CleanupOldVersions(string versionsDirectory)
    {
        var now = DateTime.Now;
        // Threshold: 10GB (configurable)
        var sizeLimitBytes = 10L * 1024 * 1024 * 1024;

        // Track files marked for deletion
        Dictionary<string, List<string>> filesToRemove = new Dictionary<string, List<string>>();

        // Process each file's version history
        foreach (var kv in Records)
        {
            var filePath = kv.Key;
            var record = kv.Value;
            if (record.Versions.Count == 0) continue;

            // Get all versions that have timestamp records
            var validVersions = record.Versions.Where(v => Times.ContainsKey(v)).ToList();
            if (validVersions.Count == 0) continue;

            // Sort versions by time (oldest to newest)
            validVersions.Sort((a, b) => Times[a].Time.CompareTo(Times[b].Time));

            // List to track versions to remove for this file
            List<string> toRemove = new List<string>();
            filesToRemove[filePath] = toRemove;

            // 1. Keep all versions from the last day
            var oneDayAgo = now.AddDays(-1);
            var (olderThan1Day, _) = SplitByTime(validVersions, oneDayAgo);

            // Keep all versions from the last day, clean up only older versions

            // 2. For versions 1-7 days old: keep at most 3 per day
            var sevenDaysAgo = now.AddDays(-7);
            var (olderThan7Days, within7Days) = SplitByTime(olderThan1Day, sevenDaysAgo);

            if (within7Days.Count > 0)
            {
                // Group by day
                var within7ByDay = within7Days.GroupBy(v => Times[v].Time.Date);
                foreach (var dayGroup in within7ByDay)
                {
                    var versionsThisDay = dayGroup.ToList();
                    if (versionsThisDay.Count > 3)
                    {
                        // For each day with more than 3 versions, keep first, middle, and last
                        versionsThisDay.Sort((a, b) => Times[a].Time.CompareTo(Times[b].Time));
                        var count = versionsThisDay.Count;
                        var keepIndices = new HashSet<int> { 0, count / 2, count - 1 };

                        for (var i = 0; i < versionsThisDay.Count; i++)
                        {
                            if (!keepIndices.Contains(i))
                            {
                                toRemove.Add(versionsThisDay[i]);
                            }
                        }
                    }
                }
            }

            // 3. For versions 7-30 days old: keep at most 4 total
            var thirtyDaysAgo = now.AddDays(-30);
            var (olderThan30Days, within30Days) = SplitByTime(olderThan7Days, thirtyDaysAgo);

            if (within30Days.Count > 4)
            {
                // Sort by time, keep newest 4, remove the rest
                within30Days.Sort((a, b) => Times[a].Time.CompareTo(Times[b].Time));
                var excessCount = within30Days.Count - 4;

                // Remove oldest versions (keep newest 4)
                for (var i = 0; i < excessCount; i++)
                {
                    toRemove.Add(within30Days[i]);
                }
            }

            // 4. For versions older than 30 days: keep only 1 per month
            if (olderThan30Days.Count > 0)
            {
                var olderByMonth = olderThan30Days.GroupBy(v =>
                {
                    var t = Times[v].Time;
                    return new {t.Year, t.Month };
                });

                foreach (var monthGroup in olderByMonth)
                {
                    var versionsThisMonth = monthGroup.ToList();
                    if (versionsThisMonth.Count > 1)
                    {
                        // Find the latest version time for this month
                        versionsThisMonth.Sort((a, b) => Times[a].Time.CompareTo(Times[b].Time));
                        var latestVersion = versionsThisMonth[^1]; // Latest version for the month

                        // Remove all except the latest
                        foreach (var ver in versionsThisMonth)
                        {
                            if (ver != latestVersion)
                                toRemove.Add(ver);
                        }
                    }
                }
            }
        }

        // Apply removals for each file - mark versions as deleted in Times dictionary and remove actual files
        foreach (var kv in filesToRemove)
        {
            List<string> toRemove = kv.Value;

            foreach (var ver in toRemove.Distinct())
            {
                if (Times.TryGetValue(ver, out var timeRecord))
                {
                    // Mark as deleted in Times dictionary
                    timeRecord.Deleted = true;

                    // Mark as deleted in Times dictionary and delete actual backup file
                    try
                    {
                        var backupFile = GetBackupFilePath(ver, versionsDirectory);
                        if (File.Exists(backupFile))
                        {
                            File.Delete(backupFile);
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Failed to delete backup file for version {ver}: {ex.Message}");
                    }
                }

                // Note: We're not removing from Records.Versions to maintain history integrity
            }
        }

        // 5. Check total size and remove older versions if exceeding limit
        var totalSize = GetDirectorySize(versionsDirectory);
        if (totalSize > sizeLimitBytes)
        {
            // Build list of all remaining versions across all files
            var allVersions = new List<(string filePath, string ver, DateTimeOffset time)>();
            foreach (var kv in Records)
            {
                var filePath = kv.Key;
                var record = kv.Value;

                foreach (var ver in record.Versions)
                {
                    if (Times.TryGetValue(ver, out var timeRecord) && !timeRecord.Deleted)
                    {
                        allVersions.Add((filePath, ver, timeRecord.Time));
                    }
                }
            }

            // Sort by time from oldest to newest
            allVersions.Sort((a, b) => a.time.CompareTo(b.time));

            // First pass: Try to delete non-monthly archive versions
            foreach (var (filePath, ver, time) in allVersions)
            {
                if (totalSize <= sizeLimitBytes) break;

                var isMonthlyArchive = false;
                if (time < now.AddDays(-30))
                {
                    // Check if this is a monthly archive (only version for its month)
                    var fileRecord = Records[filePath];
                    var sameMonthVersions = fileRecord.Versions
                        .Where(v => Times.ContainsKey(v) && !Times[v].Deleted &&
                               Times[v].Time.Year == time.Year &&
                               Times[v].Time.Month == time.Month)
                        .ToList();

                    if (sameMonthVersions.Count == 1)
                    {
                        isMonthlyArchive = true;
                    }
                }

                if (!isMonthlyArchive)
                {
                    // Delete this version and mark as deleted in Times dictionary
                    try
                    {
                        var backupFile = GetBackupFilePath(ver, versionsDirectory);
                        long fileSize;

                        if (File.Exists(backupFile))
                        {
                            fileSize = new FileInfo(backupFile).Length;
                            File.Delete(backupFile);
                            Times[ver].Deleted = true; // Mark as deleted
                            totalSize -= fileSize;
                        }
                        else
                        {
                            // If file doesn't exist, still mark as deleted in Times dictionary
                            Times[ver].Deleted = true;
                        }
                    }
                    catch { /* Ignore deletion errors */ }
                }
            }

            // Second pass: If still over limit, delete monthly archives too, starting with oldest
            if (totalSize > sizeLimitBytes)
            {
                foreach (var (_, ver, _) in allVersions)
                {
                    if (totalSize <= sizeLimitBytes) break;

                    if (Times.TryGetValue(ver, out var timeRecord) && !timeRecord.Deleted)
                    {
                        try
                        {
                            var backupFile = GetBackupFilePath(ver, versionsDirectory);
                            long fileSize;

                            if (File.Exists(backupFile))
                            {
                                fileSize = new FileInfo(backupFile).Length;
                                File.Delete(backupFile);
                                timeRecord.Deleted = true; // Mark as deleted
                                totalSize -= fileSize;
                            }
                            else
                            {
                                // If file doesn't exist, still mark as deleted in Times dictionary
                                timeRecord.Deleted = true;
                            }
                        }
                        catch { /* Ignore deletion errors */ }
                    }
                }
            }
        }

        // Update the version counter to trigger a write of the updated records
        Version++;
    }
}
