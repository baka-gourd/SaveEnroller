using System.Diagnostics;

namespace SaveEnroller.Daemon
{
    internal class Program
    {
        static void Main(string[] args)
        {
            var configDirectoryPath = args[0];
            var savesPath = args[1];
            if (args[2] != "debug")
            {
                var processId = int.Parse(args[2]);
                var mainProcess = Process.GetProcessById(processId);
                var watcher = new Thread(() =>
                {
                    var storage = Path.Combine(configDirectoryPath, ".SaveEnroller");
                    var sw = new SaveWatcher(savesPath, storage, Path.Combine(storage, "track.csv"),
                        Path.Combine(storage, "time.csv"));
                });
                watcher.Start();
                mainProcess.WaitForExit();
            }
            else
            {
                var watcher = new Thread(() =>
                {
                    var storage = Path.Combine(configDirectoryPath, ".SaveEnroller");
                    var sw = new SaveWatcher(savesPath, storage, Path.Combine(storage, "track.csv"),
                        Path.Combine(storage, "time.csv"));
                });
                watcher.Start();
                Console.ReadLine();
            }
        }
    }
}