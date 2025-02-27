namespace SaveEnroller.Daemon
{
    public class SaveWatcher
    {
        private FileSystemWatcher FileSystemWatcher { get; set; }

        public SaveWatcher(string savePath)
        {
            FileSystemWatcher = new()
            {
                Path = savePath,
            };
            FileSystemWatcher.Filters.Add("*.cok");
            FileSystemWatcher.Filters.Add("*.cok.cid");
            FileSystemWatcher.Created += OnChanged;
            FileSystemWatcher.Changed += OnChanged;
            FileSystemWatcher.Deleted += OnChanged;
            FileSystemWatcher.Renamed += OnRenamed;
        }

        private void OnChanged(object sender, FileSystemEventArgs e)
        {
            Console.WriteLine($"文件 {e.ChangeType}: {e.FullPath}");
        }

        private void OnRenamed(object sender, RenamedEventArgs e)
        {
            Console.WriteLine($"文件重命名: 从 {e.OldFullPath} 到 {e.FullPath}");
        }

        public void Watch()
        {

        }
    }
}