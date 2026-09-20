using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Web.Script.Serialization;

namespace DolphinAchiever
{
    public class GameData
    {
        public int GameId;
        public string Title = "";
        public string RichPresenceScript = "";
        public List<Achievement> Achievements = new List<Achievement>();
    }

    // Talks to the RetroAchievements Connect API (dorequest.php) using the credentials
    // Dolphin already stores, so there is nothing extra for the user to set up.
    //
    // The Connect API is used rather than the Web API because only it returns real
    // trigger logic; the Web API's MemAddr field is an md5 hash of the logic, which is
    // useless for working out where an achievement applies.
    public class RaClient
    {
        const string Endpoint = "https://retroachievements.org/dorequest.php";
        const string BadgeBase = "https://media.retroachievements.org/Badge/";
        const string UserAgent = "DolphinAchiever/1.0 (Windows)";

        readonly string _user;
        readonly string _token;
        readonly string _cacheDir;

        public bool HasCredentials { get { return !string.IsNullOrEmpty(_user) && !string.IsNullOrEmpty(_token); } }
        public string Username { get { return _user; } }

        public RaClient(string user, string token, string cacheDir)
        {
            _user = user;
            _token = token;
            _cacheDir = cacheDir;
            Directory.CreateDirectory(_cacheDir);
            Directory.CreateDirectory(BadgeDir);
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
        }

        public string BadgeDir { get { return Path.Combine(_cacheDir, "badges"); } }

        public static RaClient FromDolphin(DolphinInstall d, string cacheDir)
        {
            var cfg = DolphinInstall.ReadIniSection(d.AchievementsIni, "Achievements");
            string u, t;
            cfg.TryGetValue("Username", out u);
            cfg.TryGetValue("ApiToken", out t);
            return new RaClient(u, t, cacheDir);
        }

        public static bool IsHardcore(DolphinInstall d)
        {
            var cfg = DolphinInstall.ReadIniSection(d.AchievementsIni, "Achievements");
            string v;
            return cfg.TryGetValue("HardcoreEnabled", out v) &&
                   v.Equals("True", StringComparison.OrdinalIgnoreCase);
        }

        string Post(Dictionary<string, string> form)
        {
            var sb = new StringBuilder();
            foreach (var kv in form)
            {
                if (sb.Length > 0) sb.Append('&');
                sb.Append(Uri.EscapeDataString(kv.Key)).Append('=').Append(Uri.EscapeDataString(kv.Value));
            }
            byte[] body = Encoding.UTF8.GetBytes(sb.ToString());

            var req = (HttpWebRequest)WebRequest.Create(Endpoint);
            req.Method = "POST";
            req.ContentType = "application/x-www-form-urlencoded";
            req.UserAgent = UserAgent;
            req.Timeout = 20000;
            req.ContentLength = body.Length;
            using (Stream s = req.GetRequestStream()) s.Write(body, 0, body.Length);
            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                return sr.ReadToEnd();
        }

        static string Str(Dictionary<string, object> d, string k)
        {
            object o;
            if (d == null || !d.TryGetValue(k, out o) || o == null) return "";
            return o.ToString();
        }

        static int Int(Dictionary<string, object> d, string k)
        {
            int v;
            int.TryParse(Str(d, k), NumberStyles.Any, CultureInfo.InvariantCulture, out v);
            return v;
        }

        static Dictionary<string, object> Deserialize(string json)
        {
            var ser = new JavaScriptSerializer();
            ser.MaxJsonLength = int.MaxValue;
            return (Dictionary<string, object>)ser.DeserializeObject(json);
        }

        // Achievement definitions + rich presence for a game, cached on disk so that
        // repeat sessions do not hit the network at all.
        public GameData GetGameData(int gameId, TimeSpan maxAge)
        {
            string cache = Path.Combine(_cacheDir, "patch_" + gameId + ".json");
            string json = null;

            if (File.Exists(cache) && DateTime.UtcNow - File.GetLastWriteTimeUtc(cache) < maxAge)
            {
                try { json = File.ReadAllText(cache, Encoding.UTF8); } catch { }
            }

            if (json == null && HasCredentials)
            {
                try
                {
                    string resp = Post(new Dictionary<string, string> {
                        { "r", "patch" }, { "u", _user }, { "t", _token },
                        { "g", gameId.ToString(CultureInfo.InvariantCulture) } });
                    var root = Deserialize(resp);
                    object ok;
                    if (root.TryGetValue("Success", out ok) && Convert.ToBoolean(ok))
                    {
                        json = resp;
                        try { File.WriteAllText(cache, resp, Encoding.UTF8); } catch { }
                    }
                }
                catch { /* offline: fall through to any stale cache */ }
            }

            if (json == null && File.Exists(cache))
            {
                try { json = File.ReadAllText(cache, Encoding.UTF8); } catch { }
            }
            if (json == null) return null;

            Dictionary<string, object> r2;
            try { r2 = Deserialize(json); } catch { return null; }
            object pd;
            if (!r2.TryGetValue("PatchData", out pd)) return null;
            var patch = pd as Dictionary<string, object>;
            if (patch == null) return null;

            var g = new GameData
            {
                GameId = Int(patch, "ID"),
                Title = Str(patch, "Title"),
                RichPresenceScript = Str(patch, "RichPresencePatch")
            };
            if (g.GameId == 0) g.GameId = gameId;

            object achObj;
            if (patch.TryGetValue("Achievements", out achObj) && achObj is object[])
            {
                foreach (object o in (object[])achObj)
                {
                    var d = o as Dictionary<string, object>;
                    if (d == null) continue;
                    var a = new Achievement
                    {
                        Id = Int(d, "ID"),
                        Title = Str(d, "Title"),
                        Description = Str(d, "Description"),
                        MemAddrRaw = Str(d, "MemAddr"),
                        Points = Int(d, "Points"),
                        BadgeName = Str(d, "BadgeName"),
                        Type = Str(d, "Type"),
                        Flags = Int(d, "Flags")
                    };
                    if (a.Flags != 3) continue;              // core set only
                    if (a.Id >= 100000000) continue;         // server-side warning pseudo-achievements
                    try { a.Parsed = MemAddr.Parse(a.MemAddrRaw); } catch { a.Parsed = null; }
                    g.Achievements.Add(a);
                }
            }
            return g;
        }

        // IDs the player has already earned.
        public HashSet<int> GetUnlocked(int gameId, bool hardcore)
        {
            var set = new HashSet<int>();
            if (!HasCredentials) return set;
            try
            {
                string resp = Post(new Dictionary<string, string> {
                    { "r", "unlocks" }, { "u", _user }, { "t", _token },
                    { "g", gameId.ToString(CultureInfo.InvariantCulture) },
                    { "h", hardcore ? "1" : "0" } });
                var root = Deserialize(resp);
                object arr;
                if (root.TryGetValue("UserUnlocks", out arr) && arr is object[])
                    foreach (object o in (object[])arr)
                    {
                        int v;
                        if (int.TryParse(Convert.ToString(o, CultureInfo.InvariantCulture),
                                         NumberStyles.Any, CultureInfo.InvariantCulture, out v))
                            set.Add(v);
                    }
            }
            catch { }
            return set;
        }

        // Local path to an achievement badge, downloading it once if needed.
        public string GetBadge(string badgeName)
        {
            if (string.IsNullOrEmpty(badgeName)) return null;
            string file = Path.Combine(BadgeDir, badgeName + ".png");
            if (File.Exists(file) && new FileInfo(file).Length > 0) return file;
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(BadgeBase + badgeName + ".png");
                req.UserAgent = UserAgent;
                req.Timeout = 15000;
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (Stream s = resp.GetResponseStream())
                using (var fs = new FileStream(file, FileMode.Create, FileAccess.Write))
                {
                    var buf = new byte[8192];
                    int n;
                    while ((n = s.Read(buf, 0, buf.Length)) > 0) fs.Write(buf, 0, n);
                }
                return File.Exists(file) ? file : null;
            }
            catch { return null; }
        }

        // Resolve a game by title when the log has not revealed an ID (unauthenticated).
        public int FindGameIdByTitle(int consoleId, string title)
        {
            try
            {
                string resp = Post(new Dictionary<string, string> {
                    { "r", "systemgames" },
                    { "s", consoleId.ToString(CultureInfo.InvariantCulture) } });
                var root = Deserialize(resp);
                object arr;
                if (!root.TryGetValue("Response", out arr) || !(arr is object[])) return 0;
                string want = Normalize(title);
                int best = 0, bestLen = int.MaxValue;
                foreach (object o in (object[])arr)
                {
                    var d = o as Dictionary<string, object>;
                    if (d == null) continue;
                    string t = Normalize(Str(d, "Title"));
                    if (t.Length == 0) continue;
                    if (t == want) return Int(d, "ID");
                    if (want.Length > 3 && t.StartsWith(want) && t.Length < bestLen)
                    { best = Int(d, "ID"); bestLen = t.Length; }
                }
                return best;
            }
            catch { return 0; }
        }

        static string Normalize(string s)
        {
            var sb = new StringBuilder();
            foreach (char c in s.ToLowerInvariant())
                if (char.IsLetterOrDigit(c)) sb.Append(c);
            return sb.ToString();
        }
    }
}
