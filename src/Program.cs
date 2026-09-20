using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace DolphinAchiever
{
    static class Program
    {
        static ToastWindow _toast;
        static Options _opt;
        static readonly Dictionary<string, Image> _icons = new Dictionary<string, Image>();
        static HashSet<int> _lastLevelIds = new HashSet<int>();
        static volatile int _dolphinPid;

        class Options
        {
            public string DolphinExe;
            public List<string> LaunchArgs = new List<string>();
            public bool Launch;
            public bool Console;
            public int Corner = 3;
            public double Seconds = 7;
            public int MaxToasts = 5;
            public double Settle = 1.5;
            public DescriptionMode Descriptions = DescriptionMode.Hover;
            public bool ExitWithDolphin = true;
            public bool Demo;
        }

        public static string DataDir
        {
            get
            {
                string d = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "DolphinAchiever");
                Directory.CreateDirectory(d);
                return d;
            }
        }

        static void Log(string msg)
        {
            string line = DateTime.Now.ToString("HH:mm:ss") + "  " + msg;
            if (_opt != null && _opt.Console) System.Console.WriteLine(line);
            try { File.AppendAllText(Path.Combine(DataDir, "achiever.log"), line + Environment.NewLine); }
            catch { }
        }

        [STAThread]
        static int Main(string[] args)
        {
            _opt = ParseArgs(args);

            DolphinInstall install = _opt.DolphinExe != null
                ? DolphinInstall.FromExe(_opt.DolphinExe)
                : DolphinInstall.Discover();

            if (install == null)
            {
                Fail("Could not find Dolphin. Pass --dolphin \"C:\\path\\to\\Dolphin.exe\".");
                return 2;
            }
            Log("Dolphin: " + install.ExePath);
            Log("User dir: " + install.UserDir);

            if (_opt.Launch)
            {
                try
                {
                    var psi = new ProcessStartInfo(install.ExePath)
                    {
                        UseShellExecute = false,
                        WorkingDirectory = Path.GetDirectoryName(install.ExePath),
                        Arguments = Quote(_opt.LaunchArgs)
                    };
                    Process.Start(psi);
                    Log("Launched Dolphin.");
                }
                catch (Exception e)
                {
                    Fail("Could not start Dolphin: " + e.Message);
                    return 3;
                }
            }

            Application.EnableVisualStyles();
            _toast = new ToastWindow
            {
                CornerIndex = _opt.Corner,
                Descriptions = _opt.Descriptions,
                // Keep the panel inside Dolphin's picture rather than the desktop corner.
                TargetProvider = delegate { return DolphinWindow.GetRenderArea(_dolphinPid); }
            };

            if (_opt.Demo)
            {
                // Sample panel for checking placement and size without playing.
                var demo = new Notice { Header = "Good Egg Galaxy", HeaderNote = "3 left", Seconds = _opt.Seconds };
                demo.Rows.Add(new NoticeRow { Title = "Sunny Side Up", Corner = "10",
                    Body = "Complete \"Purple Coin Omelet\" in Good Egg Galaxy in under 1 minute and 30 seconds." });
                demo.Rows.Add(new NoticeRow { Title = "Kalimari Koins", Corner = "10", Missable = true,
                    Body = "Collect 15 coins during the battle with King Kaliente in Good Egg Galaxy." });
                demo.Rows.Add(new NoticeRow { Title = "Up, Up, and Away", Corner = "5",
                    Body = "Obtain the star in \"Luigi on the Roof\" without entering the orange pipe." });
                _toast.Push(demo);
            }

            var worker = new Thread(delegate () { Run(install); });
            worker.IsBackground = true;
            worker.Start();

            Application.Run();
            return 0;
        }

        static string Quote(List<string> args)
        {
            var sb = new StringBuilder();
            foreach (string a in args)
            {
                if (sb.Length > 0) sb.Append(' ');
                if (a.IndexOf(' ') >= 0 && !a.StartsWith("\"")) sb.Append('"').Append(a).Append('"');
                else sb.Append(a);
            }
            return sb.ToString();
        }

        static void Fail(string msg)
        {
            Log("ERROR: " + msg);
            if (_opt != null && _opt.Console) return;
            try { MessageBox.Show(msg, "Dolphin Achiever", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            catch { }
        }

        static Options ParseArgs(string[] args)
        {
            var o = new Options();
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                switch (a)
                {
                    case "--dolphin": if (i + 1 < args.Length) o.DolphinExe = args[++i]; break;
                    case "--launch": o.Launch = true; break;
                    case "--console": o.Console = true; break;
                    case "--corner": if (i + 1 < args.Length) int.TryParse(args[++i], out o.Corner); break;
                    case "--seconds":
                        if (i + 1 < args.Length)
                            double.TryParse(args[++i], NumberStyles.Any, CultureInfo.InvariantCulture, out o.Seconds);
                        break;
                    case "--max":
                        if (i + 1 < args.Length) int.TryParse(args[++i], out o.MaxToasts);
                        break;
                    case "--stay": o.ExitWithDolphin = false; break;
                    case "--demo": o.Demo = true; break;
                    case "--settle":
                        if (i + 1 < args.Length)
                            double.TryParse(args[++i], NumberStyles.Any, CultureInfo.InvariantCulture, out o.Settle);
                        break;
                    case "--text":
                        if (i + 1 < args.Length)
                        {
                            string m = args[++i].ToLowerInvariant();
                            o.Descriptions = m == "always" ? DescriptionMode.Always
                                           : m == "never" ? DescriptionMode.Never
                                           : DescriptionMode.Hover;
                        }
                        break;
                    case "--":
                        for (int j = i + 1; j < args.Length; j++) o.LaunchArgs.Add(args[j]);
                        i = args.Length;
                        break;
                    default:
                        o.LaunchArgs.Add(a);
                        break;
                }
            }
            return o;
        }

        static Image LoadIcon(RaClient ra, string badge)
        {
            if (string.IsNullOrEmpty(badge)) return null;
            Image img;
            if (_icons.TryGetValue(badge, out img)) return img;
            string path = ra.GetBadge(badge);
            if (path == null) { _icons[badge] = null; return null; }
            try
            {
                // Copy into memory so the file is not kept locked.
                using (var tmp = Image.FromFile(path))
                    img = new Bitmap(tmp);
            }
            catch { img = null; }
            _icons[badge] = img;
            return img;
        }

        static readonly string[] LevelLookups =
            { "Galaxy", "World", "Level", "Stage", "Zone", "Area", "Map", "Course", "Region", "Location" };
        static readonly string[] SectionLookups =
            { "Scenario", "Mission", "Star", "Act", "Chapter", "Section", "Phase", "Room", "Task" };

        static string FirstLookup(RichPresence rp, RcEval eval, string[] names, string skip)
        {
            if (rp == null || !rp.IsUsable) return null;
            foreach (string n in names)
            {
                if (skip != null && string.Equals(n, skip, StringComparison.OrdinalIgnoreCase)) continue;
                if (!rp.HasLookup(n)) continue;
                string v = rp.EvaluateLookup(n, eval);
                if (!string.IsNullOrEmpty(v)) return v;
            }
            return null;
        }

        static string UsedLevelLookupName(RichPresence rp, RcEval eval)
        {
            if (rp == null || !rp.IsUsable) return null;
            foreach (string n in LevelLookups)
                if (rp.HasLookup(n) && !string.IsNullOrEmpty(rp.EvaluateLookup(n, eval))) return n;
            return null;
        }

        static void Run(DolphinInstall install)
        {
            var ra = RaClient.FromDolphin(install, Path.Combine(DataDir, "cache"));
            if (!ra.HasCredentials)
                Log("WARNING: no RetroAchievements credentials in Dolphin config; " +
                    "sign in to RetroAchievements in Dolphin first.");

            var watcher = new GameIdWatcher(install.LogPath);
            watcher.RescanFromStart();
            bool hardcore = RaClient.IsHardcore(install);

            using (var mem = new DolphinMemory())
            {
                GameData game = null;
                LocationMatcher matcher = null;
                RichPresence rp = null;
                RcEval eval = null;
                HashSet<int> unlocked = new HashSet<int>();
                DateTime lastUnlockFetch = DateTime.MinValue;

                string lastLevel = null, lastSection = null;
                string pendingLevel = null;
                int pendingCount = 0;
                int loadedGameId = 0;
                DateTime attachedAt = DateTime.MinValue;
                bool sawDolphin = false;

                while (true)
                {
                    try
                    {
                        if (!mem.TryAttach())
                        {
                            if (sawDolphin && _opt.ExitWithDolphin && !DolphinRunning())
                            {
                                Log("Dolphin closed; exiting.");
                                try { Application.Exit(); } catch { }
                                return;
                            }
                            game = null; matcher = null; rp = null; eval = null;
                            _dolphinPid = 0;
                            loadedGameId = 0;
                            lastLevel = lastSection = null;
                            Thread.Sleep(1500);
                            continue;
                        }
                        sawDolphin = true;
                        _dolphinPid = mem.ProcessId;
                        if (attachedAt == DateTime.MinValue)
                        {
                            attachedAt = DateTime.UtcNow;
                            Log("Attached to Dolphin, disc " + mem.GameId + (mem.IsWii ? " (Wii)" : " (GC)"));
                        }
                        if (eval == null) eval = new RcEval(mem);

                        watcher.Poll();
                        int gid = watcher.GameId;

                        if (gid == 0 && (DateTime.UtcNow - attachedAt).TotalSeconds > 12)
                        {
                            gid = GameIdWatcher.ResolveByDisc(mem, ra);
                            if (gid != 0) Log("Resolved game by disc title -> " + gid);
                            else
                            {
                                Log("No game ID yet. Is the RetroAchievements log channel enabled? " +
                                    "Re-run Install.ps1 if not.");
                                attachedAt = DateTime.UtcNow;   // back off before trying again
                            }
                        }

                        if (gid != 0 && gid != loadedGameId)
                        {
                            Log("Loading achievement data for game " + gid + "...");
                            game = ra.GetGameData(gid, TimeSpan.FromDays(3));
                            if (game == null)
                            {
                                Log("Could not load achievement data (offline and nothing cached).");
                                Thread.Sleep(5000);
                                continue;
                            }
                            loadedGameId = gid;
                            matcher = new LocationMatcher(game.Achievements);
                            matcher.Build();
                            rp = RichPresence.Parse(game.RichPresenceScript);
                            unlocked = ra.GetUnlocked(gid, hardcore);
                            lastUnlockFetch = DateTime.UtcNow;
                            lastLevel = lastSection = null;

                            int located = 0;
                            foreach (Achievement a in game.Achievements) if (a.HasLocation) located++;
                            Log(string.Format("{0}: {1} achievements, {2} location-aware, {3} already unlocked, {4} location keys",
                                game.Title, game.Achievements.Count, located, unlocked.Count, matcher.Keys.Count));
                            if (matcher.Keys.Count == 0)
                                Log("This set does not identify locations in a way that can be detected; " +
                                    "no location popups will appear for it.");
                        }

                        if (matcher == null || matcher.Keys.Count == 0) { Thread.Sleep(700); continue; }

                        double?[] cur = matcher.ReadCurrent(eval);
                        string levelSig = matcher.LevelSignature(cur);
                        string sectionSig = matcher.SectionSignature(cur);
                        if (levelSig.IndexOf('?') >= 0) { Thread.Sleep(300); continue; }

                        // Require the same reading twice before acting: memory is briefly
                        // inconsistent while a level loads.
                        if (levelSig != lastLevel)
                        {
                            if (levelSig != pendingLevel) { pendingLevel = levelSig; pendingCount = 1; }
                            else pendingCount++;

                            int needed = Math.Max(2, (int)Math.Round(_opt.Settle / 0.3));
                            if (pendingCount >= needed)
                            {
                                if (lastLevel != null && (DateTime.UtcNow - lastUnlockFetch).TotalSeconds > 90)
                                {
                                    unlocked = ra.GetUnlocked(loadedGameId, hardcore);
                                    lastUnlockFetch = DateTime.UtcNow;
                                }
                                lastLevel = levelSig;
                                lastSection = sectionSig;
                                AnnounceLevel(ra, matcher, game, rp, eval, cur, unlocked);
                            }
                            Thread.Sleep(300);
                            continue;
                        }

                        if (sectionSig != lastSection)
                        {
                            lastSection = sectionSig;
                            AnnounceSection(ra, matcher, game, rp, eval, cur, unlocked);
                        }
                    }
                    catch (Exception e)
                    {
                        Log("poll error: " + e.Message);
                        Thread.Sleep(1000);
                    }
                    Thread.Sleep(300);
                }
            }
        }

        static bool DolphinRunning()
        {
            foreach (string n in new[] { "Dolphin", "DolphinQt", "Dolphin-x64" })
                if (Process.GetProcessesByName(n).Length > 0) return true;
            return false;
        }

        static List<Achievement> Pick(LocationMatcher m, GameData g, double?[] cur,
                                      HashSet<int> unlocked, bool sectionOnly)
        {
            var list = new List<Achievement>();
            foreach (Achievement a in g.Achievements)
            {
                if (unlocked.Contains(a.Id)) continue;
                if (sectionOnly)
                {
                    // A section is part of a level, so the achievement must belong to
                    // this level too. Without that check, an achievement constrained
                    // only to "mission 1" would match mission 1 of every level.
                    if (!m.HasSectionConstraint(a)) continue;
                    if (!m.MatchesLevel(a, cur)) continue;
                    if (!m.MatchesSection(a, cur)) continue;
                }
                else
                {
                    if (!m.MatchesLevel(a, cur)) continue;
                }
                list.Add(a);
            }
            // Missables first, then higher point values.
            list.Sort(delegate (Achievement x, Achievement y)
            {
                if (x.IsMissable != y.IsMissable) return x.IsMissable ? -1 : 1;
                return y.Points.CompareTo(x.Points);
            });
            return list;
        }

        static void AnnounceLevel(RaClient ra, LocationMatcher m, GameData g, RichPresence rp,
                                  RcEval eval, double?[] cur, HashSet<int> unlocked)
        {
            List<Achievement> list = Pick(m, g, cur, unlocked, false);
            string name = FirstLookup(rp, eval, LevelLookups, null) ?? ReadLevelName(m, eval) ?? "New area";
            Log("Level -> " + name + " (" + list.Count + " locked achievements here)");
            _lastLevelIds = new HashSet<int>();
            foreach (Achievement a in list) _lastLevelIds.Add(a.Id);
            if (list.Count == 0) return;
            Announce(ra, name, list, "here");
        }

        static void AnnounceSection(RaClient ra, LocationMatcher m, GameData g, RichPresence rp,
                                    RcEval eval, double?[] cur, HashSet<int> unlocked)
        {
            List<Achievement> list = Pick(m, g, cur, unlocked, true);
            if (list.Count == 0) return;

            // Nothing new to say if the level popup already listed exactly these.
            bool sameAsLevel = list.Count == _lastLevelIds.Count;
            if (sameAsLevel)
                foreach (Achievement a in list)
                    if (!_lastLevelIds.Contains(a.Id)) { sameAsLevel = false; break; }
            if (sameAsLevel) return;

            string lvl = UsedLevelLookupName(rp, eval);
            string name = FirstLookup(rp, eval, SectionLookups, lvl) ?? "This section";
            Log("Section -> " + name + " (" + list.Count + " locked achievements)");
            Announce(ra, name, list, "in this part");
        }

        static void Announce(RaClient ra, string title, List<Achievement> list, string where)
        {
            var n = new Notice
            {
                Header = title,
                Seconds = _opt.Seconds
            };

            int shown = Math.Min(list.Count, _opt.MaxToasts);
            n.HeaderNote = list.Count > shown
                ? shown + " of " + list.Count
                : list.Count + (list.Count == 1 ? " left" : " left");

            for (int i = 0; i < shown; i++)
            {
                Achievement a = list[i];
                n.Rows.Add(new NoticeRow
                {
                    Title = a.Title,
                    Body = a.Description,
                    Corner = a.Points.ToString(CultureInfo.InvariantCulture),
                    Missable = a.IsMissable,
                    Icon = LoadIcon(ra, a.BadgeName)
                });
            }
            _toast.Push(n);
        }

        // Some games identify a level by an ASCII name in memory; show it when the
        // rich presence script cannot supply something friendlier.
        static string ReadLevelName(LocationMatcher m, RcEval eval)
        {
            if (m.Keys.Count == 0) return null;
            foreach (LocationKey k in m.Keys)
            {
                if (!m.IsPrimary(m.Keys.IndexOf(k))) continue;
                uint addr;
                if (!eval.TryResolveAddress(k.Sample, k.SampleIndex, out addr)) return null;
                string s = ReadableAt(eval, addr);
                if (s != null) return Prettify(s);
                return null;
            }
            return null;
        }

        static string ReadableAt(RcEval eval, uint addr)
        {
            var sb = new StringBuilder();
            for (uint i = 0; i < 32; i++)
            {
                double v;
                if (!eval.TryOperand(new Operand { Kind = OperandKind.Memory, Size = MemSize.U8, Address = addr + i }, 0, out v))
                    break;
                int c = (int)v;
                if (c == 0) break;
                if (c < 0x20 || c > 0x7E) return null;
                sb.Append((char)c);
            }
            return sb.Length >= 3 ? sb.ToString() : null;
        }

        // "EggStarGalaxy" -> "Egg Star Galaxy"
        static string Prettify(string s)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < s.Length; i++)
            {
                if (i > 0 && char.IsUpper(s[i]) && !char.IsUpper(s[i - 1])) sb.Append(' ');
                sb.Append(s[i]);
            }
            return sb.ToString();
        }
    }
}
