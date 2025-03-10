namespace SaveEnroller.Daemon;

class FileStateRecord(string sha1,DateTimeOffset time,bool deleted)
{
    public string Sha1 { get; set; } = sha1;
    public DateTimeOffset Time { get; set; } = time;
    public bool Deleted { get; set; } = deleted;
}