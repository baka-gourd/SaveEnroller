using Colossal.IO.AssetDatabase;
using Colossal.Logging;

using Game;
using Game.Modding;
using Game.SceneFlow;

using System.Diagnostics;
using System.IO;
using Colossal.PSI.Environment;

namespace SaveEnroller
{
    public class SaveEnroller : IMod
    {
        public int ProcessId { get; set; }
        public static ILog Logger = LogManager.GetLogger($"{nameof(SaveEnroller)}").SetShowsErrorsInUI(false);
        private Setting Setting;
        public static string Version = "";

        public void OnLoad(UpdateSystem updateSystem)
        {
            Logger.Info(nameof(OnLoad));

            if (GameManager.instance.modManager.TryGetExecutableAsset(this, out var asset))
            {
                Logger.Info($"Current mod asset at {asset.path}");
                ProcessId = Process.GetCurrentProcess().Id;
                Version = asset.version.ToString();
                LaunchDaemon(Path.Combine(Path.GetDirectoryName(asset.path) ?? "", "SaveEnroller.Daemon.exe"),
                    $"\"{Path.Combine(EnvPath.kUserDataPath, "ModsData")}\" \"{Path.Combine(EnvPath.kUserDataPath, "Saves")}\" {ProcessId}");
            }

            Setting = new Setting(this);
            Setting.RegisterInOptionsUI();
            GameManager.instance.localizationManager.AddSource("en-US", new LocaleEN(Setting));


            AssetDatabase.global.LoadSettings(nameof(SaveEnroller), Setting, new Setting(this));
        }

        public void OnDispose()
        {
            Logger.Info(nameof(OnDispose));
            if (Setting != null)
            {
                Setting.UnregisterInOptionsUI();
                Setting = null;
            }
        }

        public static void LaunchDaemon(string daemonPath, string arguments)
        {
            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = daemonPath,
                Arguments = arguments,
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            Process process = new Process
            {
                StartInfo = startInfo
            };

            process.Start();
        }
    }
}
