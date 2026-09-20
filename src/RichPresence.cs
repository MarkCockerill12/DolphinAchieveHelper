using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace DolphinAchiever
{
    // Parses and evaluates a RetroAchievements Rich Presence script.
    //
    // The script is authored by the set's developer and names the player's current
    // location in plain language ("Good Egg Galaxy", "Dino Piranha"), which no amount
    // of trigger analysis can recover on its own. Format:
    //
    //   Lookup:Galaxy
    //   6=Good Egg Galaxy
    //   3-4=the Observatory
    //   *=an unknown galaxy
    //
    //   Display:
    //   ?<condition>?<text with @Galaxy(<value expression>)>
    //   <fallback text>
    public class RichPresence
    {
        class Lookup
        {
            public string Name;
            public Dictionary<double, string> Exact = new Dictionary<double, string>();
            public List<KeyValuePair<double[], string>> Ranges = new List<KeyValuePair<double[], string>>();
            public string Default;

            public string Resolve(double v)
            {
                string s;
                if (Exact.TryGetValue(v, out s)) return s;
                foreach (var r in Ranges)
                    if (v >= r.Key[0] && v <= r.Key[1]) return r.Value;
                return Default;
            }
        }

        class DisplayLine
        {
            public Trigger Condition;    // null for the fallback line
            public string Text;
        }

        readonly Dictionary<string, Lookup> _lookups = new Dictionary<string, Lookup>(StringComparer.OrdinalIgnoreCase);
        readonly List<DisplayLine> _display = new List<DisplayLine>();
        readonly Dictionary<string, Trigger> _exprCache = new Dictionary<string, Trigger>();

        public bool IsUsable { get { return _display.Count > 0; } }

        static double ParseNum(string s)
        {
            s = s.Trim();
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                return uint.Parse(s.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            double d;
            double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out d);
            return d;
        }

        public static RichPresence Parse(string script)
        {
            var rp = new RichPresence();
            if (string.IsNullOrEmpty(script)) return rp;
            string[] lines = script.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

            Lookup current = null;
            bool inDisplay = false;

            foreach (string raw in lines)
            {
                string line = raw;
                if (line.StartsWith("//")) continue;

                if (line.Trim().Length == 0)
                {
                    current = null;
                    // A blank line does not end the Display section in practice, but an
                    // empty display body is harmless either way.
                    continue;
                }

                if (line.StartsWith("Lookup:", StringComparison.OrdinalIgnoreCase))
                {
                    current = new Lookup { Name = line.Substring(7).Trim() };
                    rp._lookups[current.Name] = current;
                    inDisplay = false;
                    continue;
                }
                if (line.StartsWith("Format:", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("FormatFunction:", StringComparison.OrdinalIgnoreCase))
                {
                    current = null;
                    inDisplay = false;
                    continue;
                }
                if (line.StartsWith("Display:", StringComparison.OrdinalIgnoreCase))
                {
                    current = null;
                    inDisplay = true;
                    continue;
                }

                if (current != null)
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = line.Substring(0, eq).Trim();
                    string val = line.Substring(eq + 1);
                    if (key == "*") { current.Default = val; continue; }
                    // A key may be a comma-separated list of values and ranges.
                    foreach (string part in key.Split(','))
                    {
                        string k = part.Trim();
                        if (k.Length == 0) continue;
                        int dash = k.IndexOf('-', 1);
                        if (dash > 0)
                        {
                            double lo = ParseNum(k.Substring(0, dash));
                            double hi = ParseNum(k.Substring(dash + 1));
                            current.Ranges.Add(new KeyValuePair<double[], string>(new[] { lo, hi }, val));
                        }
                        else
                        {
                            current.Exact[ParseNum(k)] = val;
                        }
                    }
                    continue;
                }

                if (inDisplay)
                {
                    if (line.StartsWith("?"))
                    {
                        int end = line.IndexOf('?', 1);
                        if (end < 0) continue;
                        string cond = line.Substring(1, end - 1);
                        string text = line.Substring(end + 1);
                        Trigger t = null;
                        try { t = MemAddr.Parse(cond); }
                        catch { continue; }
                        rp._display.Add(new DisplayLine { Condition = t, Text = text });
                    }
                    else
                    {
                        rp._display.Add(new DisplayLine { Condition = null, Text = line });
                    }
                }
            }
            return rp;
        }

        Trigger GetExpr(string s)
        {
            Trigger t;
            if (_exprCache.TryGetValue(s, out t)) return t;
            try { t = MemAddr.Parse(s); }
            catch { t = null; }
            _exprCache[s] = t;
            return t;
        }

        // All conditions in a display line's guard must hold.
        bool ConditionHolds(Trigger t, RcEval eval)
        {
            foreach (var g in t.Groups)
            {
                bool groupOk = true;
                for (int i = 0; i < g.Count; i++)
                {
                    Condition c = g[i];
                    if (c.Flag == CondFlag.AddAddress) continue;
                    if (c.Op == null) continue;
                    bool ok;
                    if (!eval.TryEvalComparison(g, i, out ok) || !ok) { groupOk = false; break; }
                }
                if (groupOk && g.Count > 0) return true;
            }
            return false;
        }

        // Expand @Macro(expr) references in a display line.
        string Expand(string text, RcEval eval)
        {
            var sb = new StringBuilder();
            int i = 0;
            while (i < text.Length)
            {
                if (text[i] != '@') { sb.Append(text[i++]); continue; }
                int open = text.IndexOf('(', i);
                if (open < 0) { sb.Append(text.Substring(i)); break; }

                // Find the matching close paren.
                int depth = 0, close = -1;
                for (int j = open; j < text.Length; j++)
                {
                    if (text[j] == '(') depth++;
                    else if (text[j] == ')') { depth--; if (depth == 0) { close = j; break; } }
                }
                if (close < 0) { sb.Append(text.Substring(i)); break; }

                string name = text.Substring(i + 1, open - i - 1);
                string expr = text.Substring(open + 1, close - open - 1);
                i = close + 1;

                Trigger t = GetExpr(expr);
                double v;
                if (t == null || !eval.TryEvalValue(t, out v)) { sb.Append('?'); continue; }

                Lookup lk;
                if (_lookups.TryGetValue(name, out lk))
                {
                    string s = lk.Resolve(v);
                    sb.Append(s ?? v.ToString(CultureInfo.InvariantCulture));
                }
                else
                {
                    sb.Append(v.ToString(CultureInfo.InvariantCulture));
                }
            }
            return sb.ToString();
        }

        // The full rich presence string, e.g. "Mario is in Good Egg Galaxy".
        public string Evaluate(RcEval eval)
        {
            foreach (DisplayLine d in _display)
            {
                if (d.Condition == null) return Expand(d.Text, eval);
                if (ConditionHolds(d.Condition, eval)) return Expand(d.Text, eval);
            }
            return null;
        }

        // Resolve a single named lookup (e.g. "Galaxy") using the value expression the
        // Display section feeds it. Returns null when the game has no such lookup.
        public string EvaluateLookup(string lookupName, RcEval eval)
        {
            foreach (DisplayLine d in _display)
            {
                string expr = FindMacroExpr(d.Text, lookupName);
                if (expr == null) continue;
                if (d.Condition != null && !ConditionHolds(d.Condition, eval)) continue;
                Trigger t = GetExpr(expr);
                double v;
                if (t == null || !eval.TryEvalValue(t, out v)) continue;
                Lookup lk;
                if (!_lookups.TryGetValue(lookupName, out lk)) return null;
                return lk.Resolve(v);
            }
            return null;
        }

        public bool HasLookup(string name) { return _lookups.ContainsKey(name); }

        // The human-readable names a lookup can produce (e.g. every galaxy name).
        public IEnumerable<string> LookupValues(string name)
        {
            Lookup lk;
            if (!_lookups.TryGetValue(name, out lk)) yield break;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string v in lk.Exact.Values) if (seen.Add(v)) yield return v;
            foreach (var r in lk.Ranges) if (seen.Add(r.Value)) yield return r.Value;
        }

        public IEnumerable<string> LookupNames { get { return _lookups.Keys; } }

        static string FindMacroExpr(string text, string name)
        {
            string tag = "@" + name + "(";
            int i = text.IndexOf(tag, StringComparison.OrdinalIgnoreCase);
            if (i < 0) return null;
            int open = i + tag.Length - 1;
            int depth = 0;
            for (int j = open; j < text.Length; j++)
            {
                if (text[j] == '(') depth++;
                else if (text[j] == ')') { depth--; if (depth == 0) return text.Substring(open + 1, j - open - 1); }
            }
            return null;
        }
    }
}
