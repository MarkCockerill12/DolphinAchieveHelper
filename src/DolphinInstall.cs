using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace DolphinAchiever
{
    // Locates a Dolphin installation and its User directory.
    //
    // Nothing here writes into the Dolphin program folder: everything this tool needs
    // lives either in Dolphin's User\Config (which survives updates) or in the tool's
    // own data directory.
    public class DolphinInstall
    {
        public string ExePath { get; private set; }
        public string UserDir { get; private set; }

        public string ConfigDir { get { return Path.Combine(UserDir, "Config"); } }
        public string LogPath { get { return Path.Combine(Path.Combine(UserDir, "Logs"), "dolphin.log"); } }
        public string LoggerIni { get { return Path.Combine(ConfigDir, "Logger.ini"); } }
        public string AchievementsIni { get { return Path.Combine(ConfigDir, "RetroAchievements.ini"); } }

        public static DolphinInstall FromExe(string exePath)
        {
            if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath)) return null;
            string dir = Path.GetDirectoryName(exePath);
            var d = new DolphinInstall { ExePath = exePath };

            // Portable installs keep User next to the executable.
            bool portable = File.Exists(Path.Combine(dir, "portable.txt"));
            string localUser = Path.Combine(dir, "User");
            if (portable && Directory.Exists(localUser)) d.UserDir = localUser;
            else
            {
                string docs = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "Dolphin Emulator");
                d.UserDir = Directory.Exists(docs) ? docs
                          : (Directory.Exists(localUser) ? localUser : docs);
            }
            return d;
        }

        // Find Dolphin: a running instance first, then common install locations.
        public static DolphinInstall Discover()
        {
            foreach (string name in new[] { "Dolphin", "DolphinQt", "Dolphin-x64" })
            {
                foreach (Process p in Process.GetProcessesByName(name))
                {
                    try
                    {
                        string path = p.MainModule.FileName;
                        DolphinInstall d = FromExe(path);
                        if (d != null) return d;
                    }
                    catch { /* access denied on the module list; keep looking */ }
                }
            }

            foreach (string c in CandidatePaths())
            {
                DolphinInstall d = FromExe(c);
                if (d != null) return d;
            }
            return null;
        }

        public static IEnumerable<string> CandidatePaths()
        {
            var roots = new List<string>();
            foreach (var f in new[]
            {
                Environment.SpecialFolder.ProgramFiles,
                Environment.SpecialFolder.ProgramFilesX86,
                Environment.SpecialFolder.LocalApplicationData,
            })
            {
                string p = Environment.GetFolderPath(f);
                if (!string.IsNullOrEmpty(p)) roots.Add(p);
            }
            roots.Add(@"C:\");
            roots.Add(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

            foreach (string r in roots)
            {
                yield return Path.Combine(r, @"Dolphin-x64\Dolphin.exe");
                yield return Path.Combine(r, @"Dolphin\Dolphin.exe");
                yield return Path.Combine(r, @"Dolphin Emulator\Dolphin.exe");
            }

            // Registry uninstall entries, in case it was installed somewhere unusual.
            foreach (string hive in new[]
            {
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
            })
            {
                RegistryKey k = null;
                try { k = Registry.LocalMachine.OpenSubKey(hive); } catch { }
                if (k == null) continue;
                using (k)
                {
                    foreach (string sub in k.GetSubKeyNames())
                    {
                        string loc = null;
                        try
                        {
                            using (RegistryKey s = k.OpenSubKey(sub))
                            {
                                if (s == null) continue;
                                object name = s.GetValue("DisplayName");
                                if (name == null || name.ToString().IndexOf("Dolphin", StringComparison.OrdinalIgnoreCase) < 0)
                                    continue;
                                object il = s.GetValue("InstallLocation");
                                if (il != null) loc = il.ToString();
                            }
                        }
                        catch { }
                        if (!string.IsNullOrEmpty(loc))
                            yield return Path.Combine(loc, "Dolphin.exe");
                    }
                }
            }
        }

        // Read "Key = Value" pairs from a Dolphin ini section.
        public static Dictionary<string, string> ReadIniSection(string path, string section)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!File.Exists(path)) return result;
            bool inSection = false;
            foreach (string raw in File.ReadAllLines(path))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";")) continue;
                if (line.StartsWith("[") && line.EndsWith("]"))
                {
                    inSection = string.Equals(line.Substring(1, line.Length - 2), section,
                                              StringComparison.OrdinalIgnoreCase);
                    continue;
                }
                if (!inSection) continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                result[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
            }
            return result;
        }
    }
}
