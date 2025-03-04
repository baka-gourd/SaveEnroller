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
    private readonly object _lockObject = new object();

    /// <summary>
    /// Path to the CSV file that stores file history records
    /// </summary>
    private string TrackFile { get; set; }

    /// <summary>
    /// Dictionary of file history records, keyed by file name
    /// </summary>
    public Dictionary<string, FileHistoryRecord> Records { get; set; }

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
    public TrackHelper(string trackFile)
    {
        TrackFile = trackFile;
        // If the file doesn't exist, create an empty file first
        if (!File.Exists(trackFile))
        {
            File.WriteAllText(trackFile, string.Empty);
        }

        ReadAllRecords();

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
    /// Updates the record for the specified file. If the record doesn't exist, a new one is created.
    /// If the record exists, the new SHA1 version is appended (if it's not a duplicate of the last version).
    /// The method increments the Version counter when changes are made to track unsaved modifications.
    /// </summary>
    /// <param name="fileName">The name of the file to update</param>
    /// <param name="newSha1">The SHA1 hash of the latest version of the file</param>
    /// <returns>Returns true if a new record was created, false if an existing record was updated or no changes were made</returns>
    public bool UpdateRecord(string fileName, string newSha1, out bool isDuplicate)
    {
        lock (_lockObject)
        {
            if (Records.TryGetValue(fileName, out var record))
            {
                if (record.Versions.LastOrDefault() != newSha1)
                {
                    record.Versions.Add(newSha1);
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
                Version++;
                isDuplicate = false;
                return true;
            }

            return false;
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
            using var writer = Sep.Default.Writer(_writerConfig).ToFile(TrackFile);

            foreach (var rec in Records.Values)
            {
                using var row = writer.NewRow();
                row[0].Set(rec.FileName);
                row[1].Set(rec.BaseSha1);
                for (var i = 0; i < rec.Versions.Count; i++)
                {
                    row[i + 2].Set(rec.Versions[i]);
                }
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
}