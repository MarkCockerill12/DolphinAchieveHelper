using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace DolphinAchiever
{
    // Works out which RetroAchievements game is loaded.
    //
    // Dolphin already does the hard part: it hashes the disc and asks the server. It
    // then logs the answer, so the ID can simply be read back out of the log instead of
    // reimplementing RetroAchievements' GameCube/Wii disc hashing (which would have to
    // decompress RVZ/WIA images). The installer turns on the RetroAchievements log
    // channel to make this available.
    public class GameIdWatcher
    {
        static readonly Regex Identified =
            new Regex(@"Identified game:\s*(\d+)\s*""([^""]*)""", RegexOptions.Compiled);
        static readonly Regex LoadedData =
            new Regex(@"Loaded data for game ID\s*(\d+)", RegexOptions.Compiled);

        readonly string _logPath;
        long _position;

        public int GameId { get; private set; }
        public string GameTitle { get; private set; }

        public GameIdWatcher(string logPath)
        {
            _logPath = logPath;
            // Start from the end: only this session's lines are relevant.
            try { if (File.Exists(_logPath)) _position = new FileInfo(_logPath).Length; }
            catch { _position = 0; }
        }

        public void Reset()
        {
            GameId = 0;
            GameTitle = null;
        }

        // Rewind so an ID logged before we started watching is still picked up.
        public void RescanFromStart() { _position = 0; }

        // Returns true when a new game ID was discovered.
        public bool Poll()
        {
            if (!File.Exists(_logPath)) return false;
            long len;
            try { len = new FileInfo(_logPath).Length; } catch { return false; }
            if (len < _position) _position = 0;      // log was rotated or cleared
            if (len == _position) return false;

            string chunk;
            try
            {
                using (var fs = new FileStream(_logPath, FileMode.Open, FileAccess.Read,
                                               FileShare.ReadWrite | FileShare.Delete))
                {
                    fs.Seek(_position, SeekOrigin.Begin);
                    var buf = new byte[len - _position];
                    int read = fs.Read(buf, 0, buf.Length);
                    _position += read;
                    chunk = Encoding.UTF8.GetString(buf, 0, read);
                }
            }
            catch { return false; }

            bool found = false;
            foreach (string line in chunk.Split('\n'))
            {
                Match m = Identified.Match(line);
                if (m.Success)
                {
                    int id = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                    if (id != 0) { GameId = id; GameTitle = m.Groups[2].Value; found = true; }
                    continue;
                }
                m = LoadedData.Match(line);
                if (m.Success)
                {
                    int id = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                    if (id != 0 && id != GameId) { GameId = id; found = true; }
                }
            }
            return found;
        }

        // Fallback when the log channel is unavailable: match the disc's own title
        // against the RetroAchievements list for that console.
        public static int ResolveByDisc(DolphinMemory mem, RaClient ra)
        {
            string title = mem.ReadString(0x20, 64);
            if (string.IsNullOrEmpty(title)) return 0;
            return ra.FindGameIdByTitle(mem.IsWii ? 19 : 16, title);
        }
    }
}
