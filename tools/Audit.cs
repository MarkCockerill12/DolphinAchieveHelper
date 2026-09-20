using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;
using DolphinAchiever;

// Offline accuracy audit.
//
// Reconstructs every level identity the achievement set implies, then reports which
// achievements the matcher would pop in each one. Achievements that match a large
// number of different levels are the false-positive source.
static class Audit
{
    static string Str(Dictionary<string, object> d, string k)
    {
        object o;
        if (d == null || !d.TryGetValue(k, out o) || o == null) return "";
        return o.ToString();
    }
    static int Int(Dictionary<string, object> d, string k)
    {
        int v; int.TryParse(Str(d, k), NumberStyles.Any, CultureInfo.InvariantCulture, out v); return v;
    }

    static int ByteLen(MemSize s)
    {
        switch (s)
        {
            case MemSize.U16LE: case MemSize.U16BE: return 2;
            case MemSize.U24LE: case MemSize.U24BE: return 3;
            case MemSize.U32LE: case MemSize.U32BE: return 4;
            default: return 1;
        }
    }

    // Big-endian byte expansion of a constrained value.
    static void Expand(MemSize size, double value, byte[] outBytes)
    {
        uint v = (uint)value;
        int n = ByteLen(size);
        for (int i = 0; i < n; i++) outBytes[i] = (byte)((v >> ((n - 1 - i) * 8)) & 0xFF);
    }

    static void Main(string[] args)
    {
        string path = args[0];
        var ser = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        var root = (Dictionary<string, object>)ser.DeserializeObject(File.ReadAllText(path));
        object inner;
        if (root.TryGetValue("PatchData", out inner) && inner is Dictionary<string, object>)
            root = (Dictionary<string, object>)inner;

        var achs = new List<Achievement>();
        foreach (object o in (object[])root["Achievements"])
        {
            var d = (Dictionary<string, object>)o;
            var a = new Achievement
            {
                Id = Int(d, "ID"),
                Title = Str(d, "Title"),
                Description = Str(d, "Description"),
                MemAddrRaw = Str(d, "MemAddr"),
                Points = Int(d, "Points"),
                Type = Str(d, "Type"),
                Flags = Int(d, "Flags")
            };
            if (a.Flags != 3 || a.Id >= 100000000) continue;
            try { a.Parsed = MemAddr.Parse(a.MemAddrRaw); } catch { }
            achs.Add(a);
        }

        var lm = new LocationMatcher(achs);
        lm.Build();
        Console.WriteLine("achievements: " + achs.Count + "  location keys: " + lm.Keys.Count);
        if (lm.Keys.Count == 0) return;

        var primary = new List<int>();
        for (int i = 0; i < lm.Keys.Count; i++) if (lm.IsPrimary(i)) primary.Add(i);
        Console.WriteLine("primary keys: " + primary.Count + "   (level identity)");
        foreach (int i in primary)
            Console.WriteLine(string.Format("   [{0}] {1}@{2:X} values={3} achievements={4}",
                i, lm.Keys[i].Size, lm.Keys[i].Address, lm.Keys[i].Values.Count, lm.Keys[i].Achievements.Count));

        uint baseAddr = uint.MaxValue, endAddr = 0;
        foreach (int i in primary)
        {
            if (lm.Keys[i].Address < baseAddr) baseAddr = lm.Keys[i].Address;
            uint e = lm.Keys[i].Address + (uint)ByteLen(lm.Keys[i].Size);
            if (e > endAddr) endAddr = e;
        }
        int span = (int)(endAddr - baseAddr);

        // For each achievement, lay its primary constraints out as known bytes.
        var patterns = new Dictionary<int, byte?[]>();
        foreach (Achievement a in achs)
        {
            var pat = new byte?[span];
            bool bad = false;
            foreach (var dict in AllGroups(a))
                foreach (var kv in dict)
                {
                    if (!lm.IsPrimary(kv.Key)) continue;
                    if (kv.Value.Count != 1) { continue; }   // "one of N": not a single pattern
                    LocationKey k = lm.Keys[kv.Key];
                    var tmp = new byte[8];
                    foreach (double v in kv.Value) Expand(k.Size, v, tmp);
                    int off = (int)(k.Address - baseAddr);
                    for (int b = 0; b < ByteLen(k.Size); b++)
                    {
                        if (pat[off + b] != null && pat[off + b] != tmp[b]) bad = true;
                        pat[off + b] = tmp[b];
                    }
                }
            if (!bad) patterns[a.Id] = pat;
        }

        // How much of the level identity does each achievement actually pin down?
        var maskCount = new Dictionary<string, int>();
        var maskExample = new Dictionary<string, string>();
        foreach (Achievement a in achs)
        {
            var keys = new SortedSet<int>();
            foreach (var dict in AllGroups(a))
                foreach (var kv in dict)
                    if (lm.IsPrimary(kv.Key)) keys.Add(kv.Key);
            if (keys.Count == 0) continue;
            var sb = new StringBuilder();
            foreach (int i in keys) sb.Append('[').Append(i).Append(']');
            string m = sb.ToString();
            int c; maskCount.TryGetValue(m, out c); maskCount[m] = c + 1;
            if (!maskExample.ContainsKey(m)) maskExample[m] = a.Title;
        }
        Console.WriteLine("\n=== which primary keys each achievement constrains ===");
        var masks = new List<string>(maskCount.Keys);
        masks.Sort();
        foreach (string m in masks)
            Console.WriteLine(string.Format("  {0,-18} {1,3} achievements   e.g. {2}",
                m, maskCount[m], maskExample[m]));

        // How specific is each individual constrained value? A value shared by many
        // different levels (like the "Gala" of "...Galaxy") identifies nothing.
        Console.WriteLine("\n=== per-key value spread ===");
        foreach (int i in primary)
        {
            LocationKey k = lm.Keys[i];
            var vals = new List<string>();
            int shown = 0;
            foreach (double v in k.Values)
            {
                if (shown++ >= 8) break;
                var tmp = new byte[8];
                Expand(k.Size, v, tmp);
                vals.Add("'" + Name(new[] { tmp[0], tmp[1], tmp[2], tmp[3] }) + "'");
            }
            Console.WriteLine(string.Format("  [{0}] {1} distinct: {2}",
                i, k.Values.Count, string.Join(" ", vals.ToArray())));
        }

        // ---- ground truth ------------------------------------------------------
        // Achievement text usually names its level ("...in Good Egg Galaxy"). Group
        // achievements by the level their text names, union the byte constraints of
        // each group, and that reconstructs a real level identity to test against.
        var rp = RichPresence.Parse(Str(root, "RichPresencePatch"));
        var levelNames = new List<string>();
        foreach (string ln in new[] { "Galaxy", "World", "Level", "Stage", "Zone", "Area", "Course" })
            foreach (string v in rp.LookupValues(ln))
                if (v != null && v.Length > 4) levelNames.Add(v);
        levelNames.Sort(delegate (string x, string y) { return y.Length.CompareTo(x.Length); });
        Console.WriteLine("\nlevel names from rich presence: " + levelNames.Count);

        var truth = new Dictionary<string, List<Achievement>>();
        foreach (Achievement a in achs)
        {
            string hay = (a.Title + " " + a.Description).ToLowerInvariant();
            foreach (string ln in levelNames)
            {
                if (hay.IndexOf(ln.ToLowerInvariant(), StringComparison.Ordinal) < 0) continue;
                if (!truth.ContainsKey(ln)) truth[ln] = new List<Achievement>();
                truth[ln].Add(a);
                break;
            }
        }
        Console.WriteLine("achievements whose text names a level: " +
                          CountAll(truth) + " across " + truth.Count + " levels");

        int tp = 0, fp = 0, fn = 0, testable = 0;
        var offenders = new Dictionary<string, int>();

        foreach (var g in truth)
        {
            // Union the constraints of this level's achievements into one identity.
            var idBytes = new byte?[span];
            bool conflict = false;
            foreach (Achievement a in g.Value)
            {
                byte?[] p;
                if (!patterns.TryGetValue(a.Id, out p)) continue;
                for (int i = 0; i < span; i++)
                {
                    if (p[i] == null) continue;
                    if (idBytes[i] != null && idBytes[i] != p[i]) { conflict = true; }
                    idBytes[i] = p[i];
                }
            }
            if (conflict) continue;

            var cur = new double?[lm.Keys.Count];
            foreach (int i in primary)
            {
                LocationKey k = lm.Keys[i];
                int off = (int)(k.Address - baseAddr);
                uint v = 0; bool known = true;
                for (int b = 0; b < ByteLen(k.Size); b++)
                {
                    if (idBytes[off + b] == null) { known = false; break; }
                    v = (v << 8) | idBytes[off + b].Value;
                }
                cur[i] = known ? (double?)v : null;
            }
            testable++;

            var expect = new HashSet<int>();
            foreach (Achievement a in g.Value) expect.Add(a.Id);

            foreach (Achievement a in achs)
            {
                bool matched = lm.MatchesLevel(a, cur);
                bool should = expect.Contains(a.Id);
                if (matched && should) tp++;
                else if (matched && !should)
                {
                    // Only count it as wrong if its own text names a *different* level.
                    string otherLevel = null;
                    string hay = (a.Title + " " + a.Description).ToLowerInvariant();
                    foreach (string ln in levelNames)
                        if (hay.IndexOf(ln.ToLowerInvariant(), StringComparison.Ordinal) >= 0) { otherLevel = ln; break; }
                    if (otherLevel != null && otherLevel != g.Key)
                    {
                        fp++;
                        int c; offenders.TryGetValue(a.Title, out c); offenders[a.Title] = c + 1;
                    }
                }
                else if (!matched && should) fn++;
            }
        }

        Console.WriteLine("\n=== accuracy over " + testable + " reconstructed levels ===");
        Console.WriteLine("  correct matches   : " + tp);
        Console.WriteLine("  WRONG level shown : " + fp);
        Console.WriteLine("  missed            : " + fn);
        if (tp + fp > 0)
            Console.WriteLine("  precision         : " + (100.0 * tp / (tp + fp)).ToString("0.0") + "%");
        Console.WriteLine("\n  worst offenders (achievement -> how many wrong levels it appears in):");
        var offList = new List<KeyValuePair<string, int>>(offenders);
        offList.Sort(delegate (KeyValuePair<string, int> x, KeyValuePair<string, int> y) { return y.Value.CompareTo(x.Value); });
        for (int i = 0; i < Math.Min(12, offList.Count); i++)
            Console.WriteLine(string.Format("    {0,3}x  {1}", offList[i].Value, offList[i].Key));

        // Complete patterns are concrete level identities.
        var identities = new Dictionary<string, byte[]>();
        foreach (var kv in patterns)
        {
            byte?[] p = kv.Value;
            bool complete = true;
            for (int i = 0; i < span; i++) if (p[i] == null) { complete = false; break; }
            if (!complete) continue;
            var bytes = new byte[span];
            for (int i = 0; i < span; i++) bytes[i] = p[i].Value;
            identities[Name(bytes)] = bytes;
        }
        Console.WriteLine("\ndistinct complete level identities found: " + identities.Count);

        // Simulate standing in each one and see what the matcher pops.
        var matchCount = new Dictionary<int, int>();
        var report = new List<string>();
        foreach (var id in identities)
        {
            var cur = new double?[lm.Keys.Count];
            foreach (int i in primary)
            {
                LocationKey k = lm.Keys[i];
                int off = (int)(k.Address - baseAddr);
                uint v = 0;
                for (int b = 0; b < ByteLen(k.Size); b++) v = (v << 8) | id.Value[off + b];
                cur[i] = v;
            }
            var hits = new List<Achievement>();
            foreach (Achievement a in achs)
                if (lm.MatchesLevel(a, cur)) hits.Add(a);

            foreach (Achievement a in hits)
            {
                int c; matchCount.TryGetValue(a.Id, out c); matchCount[a.Id] = c + 1;
            }
            report.Add(string.Format("{0,-24} {1,3} achievements", id.Key, hits.Count));
        }
        report.Sort();
        foreach (string r in report) Console.WriteLine("  " + r);

        Console.WriteLine("\n=== achievements matching MORE THAN ONE level (ambiguous) ===");
        int amb = 0;
        foreach (Achievement a in achs)
        {
            int c;
            if (!matchCount.TryGetValue(a.Id, out c) || c <= 1) continue;
            amb++;
            if (amb <= 25)
                Console.WriteLine(string.Format("  matches {0,2} levels: {1}", c, a.Title));
        }
        Console.WriteLine("ambiguous achievements: " + amb);
    }

    static int CountAll(Dictionary<string, List<Achievement>> d)
    {
        int n = 0;
        foreach (var kv in d) n += kv.Value.Count;
        return n;
    }

    static IEnumerable<Dictionary<int, HashSet<double>>> AllGroups(Achievement a)
    {
        yield return a.Fingerprint;
        foreach (var alt in a.AltFingerprints) yield return alt;
    }

    static string Name(byte[] b)
    {
        var sb = new StringBuilder();
        foreach (byte c in b)
        {
            if (c == 0) break;
            sb.Append(c >= 0x20 && c < 0x7F ? (char)c : '.');
        }
        return sb.Length > 0 ? sb.ToString() : "(binary)";
    }
}
