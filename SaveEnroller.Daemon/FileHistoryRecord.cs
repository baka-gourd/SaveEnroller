namespace SaveEnroller.Daemon;

internal class FileHistoryRecord(string fileName, string baseSha1, IEnumerable<string> versions)
{
    public string FileName { get; set; } = fileName;
    public string BaseSha1 { get; set; } = baseSha1;
    public List<string> Versions { get; set; } = [..versions];
}