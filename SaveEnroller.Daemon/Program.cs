using System.Diagnostics;

namespace SaveEnroller.Daemon
{
    internal class Program
    {
        static void Main(string[] args)
        {
            var configDirectoryPath = args[0];
            var savesPath = args[1];
            var processId = int.Parse(args[2]);
            var mainProcess = Process.GetProcessById(processId);
            var watcher = new Thread(() => { });
            watcher.Start();
            mainProcess.WaitForExit();
        }
    }
}
