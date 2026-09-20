using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;
using DolphinAchiever;

class TestMatch
{
    static string Str(Dictionary<string, object> d, string k)
    {
        object o;
        if (!d.TryGetValue(k, out o) || o == null) return "";
        return o.ToString();
    }

    static int Int(Dictionary<string, object> d, string k)
    {
        object o;
        if (!d.TryGetValue(k, out o) || o == null) return 0;
        int v;
        int.TryParse(o.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out v);
        return v;
    }

    static void Main(string[] args)
    {
        string path = args.Length > 0 ? args[0] : "patch_189.json";
        var ser = new JavaScriptSerializer();
        ser.MaxJsonLength = int.MaxValue;
        var root = (Dictionary<string, object>)ser.DeserializeObject(File.ReadAllText(path));

        // Accept either a raw PatchData object or the full server response wrapping it.
        object inner;
        if (root.TryGetValue("PatchData", out inner) && inner is Dictionary<string, object>)
            root = (Dictionary<string, object>)inner;

        object achList;
        if (!root.TryGetValue("Achievements", out achList) || !(achList is object[]))
        {
            Console.WriteLine("No Achievements array in " + path);
            return;
        }
        var list = (object[])achList;
        var achs = new List<Achievement>();
        int failed = 0;
        foreach (object o in list)
        {
            var d = (Dictionary<string, object>)o;
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
            if (a.Flags != 3) continue;
            try { a.Parsed = MemAddr.Parse(a.MemAddrRaw); }
            catch (Exception e) { failed++; Console.WriteLine("PARSE FAIL " + a.Id + " " + a.Title + ": " + e.Message); }
            achs.Add(a);
        }
        Console.WriteLine("achievements: " + achs.Count + "  parse failures: " + failed);

        var lm = new LocationMatcher(achs);
        lm.Build();
        Console.WriteLine("winning chain: " + (lm.WinningChain == "" ? "(direct addresses)" : lm.WinningChain));
        Console.WriteLine("location keys: " + lm.Keys.Count);
        foreach (LocationKey k in lm.Keys)
            Console.WriteLine(string.Format("   {0}@{1:X}  values={2} achievements={3}",
                k.Size, k.Address, k.Values.Count, k.Achievements.Count));

        int located = 0;
        foreach (Achievement a in achs) if (a.HasLocation) located++;
        Console.WriteLine(string.Format("achievements with a location: {0}/{1}", located, achs.Count));

        using (var mem = new DolphinMemory())
        {
            if (!mem.TryAttach())
            {
                Console.WriteLine("\nDolphin not attached (no game running?) - static analysis only.");
                return;
            }
            Console.WriteLine(string.Format("\nattached: pid={0} game={1} wii={2}", mem.ProcessId, mem.GameId, mem.IsWii));

            var eval = new RcEval(mem);
            double?[] cur = lm.ReadCurrent(eval);
            for (int i = 0; i < lm.Keys.Count; i++)
            {
                LocationKey k = lm.Keys[i];
                string asAscii = "";
                if (cur[i] != null && (k.Size == MemSize.U32BE || k.Size == MemSize.U16BE))
                {
                    uint v = (uint)cur[i].Value;
                    var sb = new StringBuilder();
                    for (int b = 3; b >= 0; b--)
                    {
                        int ch = (int)((v >> (b * 8)) & 0xFF);
                        sb.Append(ch >= 0x20 && ch < 0x7F ? (char)ch : '.');
                    }
                    asAscii = " '" + sb + "'";
                }
                Console.WriteLine(string.Format("   key {0} {1}@{2:X} = {3}{4}",
                    i, k.Size, k.Address,
                    cur[i] == null ? "<unreadable>" : cur[i].Value.ToString(CultureInfo.InvariantCulture),
                    asAscii));
            }

            // Show the raw string at the winning chain's lowest offset, which is how
            // several games (including SMG) name the current stage.
            if (lm.Keys.Count > 0)
            {
                uint addr;
                if (eval.TryResolveAddress(lm.Keys[0].Sample, lm.Keys[0].SampleIndex, out addr))
                    Console.WriteLine("   chain string @0x" + addr.ToString("X") + " = \"" + mem.ReadString(addr, 48) + "\"");
            }

            object rpObj;
            if (root.TryGetValue("RichPresencePatch", out rpObj) && rpObj != null)
            {
                var rp = RichPresence.Parse(rpObj.ToString());
                Console.WriteLine("\n=== rich presence ===");
                Console.WriteLine("  lookups: " + string.Join(", ", new List<string>(rp.LookupNames).ToArray()));
                Console.WriteLine("  full   : " + (rp.Evaluate(eval) ?? "<none>"));
                foreach (string ln in new[] { "Galaxy", "Scenario", "Dome", "Level", "Stage", "Area", "World" })
                    if (rp.HasLookup(ln))
                        Console.WriteLine("  " + ln + " = " + (rp.EvaluateLookup(ln, eval) ?? "<unresolved>"));
            }

            Console.WriteLine("\n=== achievements in this LEVEL ===");
            int n = 0;
            foreach (Achievement a in achs)
            {
                if (!lm.MatchesLevel(a, cur)) continue;
                n++;
                bool here = lm.MatchesSection(a, cur);
                Console.WriteLine(string.Format("  [{0,3}pt]{1}{2} {3}\n          {4}",
                    a.Points, a.IsMissable ? " [MISSABLE]" : "",
                    here ? " <-- THIS SECTION" : "", a.Title, a.Description));
            }
            Console.WriteLine("total in level: " + n);
        }
    }
}
