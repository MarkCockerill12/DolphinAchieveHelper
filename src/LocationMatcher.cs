using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace DolphinAchiever
{
    public class Achievement
    {
        public int Id;
        public string Title = "";
        public string Description = "";
        public string MemAddrRaw = "";
        public int Points;
        public string BadgeName = "";
        public string Type = "";        // progression | win_condition | missable | ""
        public int Flags;               // 3 = core, 5 = unofficial
        public bool Unlocked;

        public bool IsMissable { get { return string.Equals(Type, "missable", StringComparison.OrdinalIgnoreCase); } }

        public Trigger Parsed;

        // Location constraints: location-key index -> set of values that put the
        // player somewhere this achievement can be earned.
        //
        // A trigger fires when the core group holds AND at least one alt group holds,
        // so the constraints are kept separately: Fingerprint is the core group and
        // AltFingerprints holds one entry per alt group. Plenty of sets put a bare
        // "1=1" in the core and all the real logic in alts.
        public Dictionary<int, HashSet<double>> Fingerprint = new Dictionary<int, HashSet<double>>();
        public List<Dictionary<int, HashSet<double>>> AltFingerprints =
            new List<Dictionary<int, HashSet<double>>>();

        public bool HasLocation
        {
            get
            {
                if (Fingerprint.Count > 0) return true;
                foreach (var alt in AltFingerprints) if (alt.Count > 0) return true;
                return false;
            }
        }
    }

    // A memory read that discriminates *where the player is*: a pointer chain plus a
    // typed read at a fixed offset.
    public class LocationKey
    {
        public string ChainSig = "";
        public List<Condition> Sample;   // condition list containing the chain + the read
        public int SampleIndex;
        public uint Address;
        public MemSize Size;
        public HashSet<double> Values = new HashSet<double>();
        public HashSet<int> Achievements = new HashSet<int>();
        public int Cluster;

        public double Score { get { return Achievements.Count * Math.Min(Values.Count, 50); } }
    }

    // Works out which memory reads identify the player's location by finding the
    // pointer chain that the most achievements test against with the widest spread of
    // constants. Degenerates naturally to plain addresses for games that use a flat
    // "current stage" variable.
    //
    // Achievements express location two ways, and both are handled:
    //   positive : 0xG20=1164404563              -- "is EggStarGalaxy"
    //   reset    : R:0xG20!=1164404563           -- "reset unless EggStarGalaxy"
    // and reset conditions chain with AndNext to mean "one of these":
    //   N:0xG40!=1_R:0xG40!=4                    -- "scenario 1 or 4"
    public class LocationMatcher
    {
        public List<LocationKey> Keys = new List<LocationKey>();
        public string WinningChain = "";
        public int PrimaryCluster { get; private set; }

        readonly List<Achievement> _achs;

        public LocationMatcher(List<Achievement> achs) { _achs = achs; }

        // One location constraint extracted from a trigger.
        struct Constraint
        {
            public string ChainSig;
            public MemSize Size;
            public uint Address;
            public HashSet<double> Allowed;
        }

        static bool IsPositive(Condition c)
        {
            return (c.Flag == CondFlag.None || c.Flag == CondFlag.AndNext ||
                    c.Flag == CondFlag.MeasuredIf || c.Flag == CondFlag.Trigger)
                && (c.Op == "=" || c.Op == "==")
                && ShapeOk(c);
        }

        static bool IsResetNegation(Condition c)
        {
            return (c.Flag == CondFlag.ResetIf || c.Flag == CondFlag.AndNext)
                && c.Op == "!=" && ShapeOk(c);
        }

        static bool ShapeOk(Condition c)
        {
            return c.Lhs != null && c.Lhs.Kind == OperandKind.Memory
                && c.Lhs.Mod == OperandMod.None
                && c.Rhs != null && c.Rhs.Kind == OperandKind.Constant
                && c.RequiredHits == 0;
        }

        static string KeyId(string chain, MemSize size, uint addr)
        {
            return chain + "|" + size + "|" + addr.ToString("X");
        }

        // Pull every location constraint out of one condition list.
        static List<Constraint> Extract(List<Condition> core)
        {
            var found = new List<Constraint>();
            var pending = new List<int>();    // indices of AndNext '!=' conditions awaiting a ResetIf

            for (int i = 0; i < core.Count; i++)
            {
                Condition c = core[i];
                if (c.Flag == CondFlag.AddAddress) continue;

                if (IsPositive(c))
                {
                    pending.Clear();
                    var set = new HashSet<double>();
                    set.Add(c.Rhs.Value);
                    found.Add(new Constraint
                    {
                        ChainSig = MemAddr.ChainSignature(core, i),
                        Size = c.Lhs.Size,
                        Address = c.Lhs.Address,
                        Allowed = set
                    });
                    continue;
                }

                if (c.Flag == CondFlag.AndNext && c.Op == "!=" && ShapeOk(c))
                {
                    pending.Add(i);
                    continue;
                }

                if (c.Flag == CondFlag.ResetIf && c.Op == "!=" && ShapeOk(c))
                {
                    // "reset if (a != x AND b != y ...)" == "a == x OR b == y ...".
                    // Only usable as a per-key constraint when every term reads the
                    // same address; otherwise it is a disjunction across keys and we
                    // cannot express it, so skip it rather than guess.
                    string chain = MemAddr.ChainSignature(core, i);
                    var set = new HashSet<double>();
                    set.Add(c.Rhs.Value);
                    bool sameKey = true;
                    foreach (int j in pending)
                    {
                        Condition p = core[j];
                        if (p.Lhs.Address != c.Lhs.Address || p.Lhs.Size != c.Lhs.Size ||
                            MemAddr.ChainSignature(core, j) != chain)
                        { sameKey = false; break; }
                        set.Add(p.Rhs.Value);
                    }
                    if (sameKey)
                    {
                        found.Add(new Constraint
                        {
                            ChainSig = chain,
                            Size = c.Lhs.Size,
                            Address = c.Lhs.Address,
                            Allowed = set
                        });
                    }
                    pending.Clear();
                    continue;
                }

                // Anything else breaks an AndNext run.
                if (c.Flag != CondFlag.AndNext) pending.Clear();
            }
            return found;
        }

        public void Build()
        {
            var byId = new Dictionary<string, LocationKey>();
            // Per achievement, per condition group, the constraints found in it.
            var perAch = new Dictionary<int, List<List<Constraint>>>();

            foreach (Achievement a in _achs)
            {
                if (a.Parsed == null) continue;
                var groups = new List<List<Constraint>>();

                foreach (List<Condition> group in a.Parsed.Groups)
                {
                    List<Constraint> cons = Extract(group);
                    groups.Add(cons);

                    foreach (Constraint con in cons)
                    {
                        string id = KeyId(con.ChainSig, con.Size, con.Address);
                        LocationKey k;
                        if (!byId.TryGetValue(id, out k))
                        {
                            // Remember where this read lives so it can be evaluated later.
                            int idx = -1;
                            for (int i = 0; i < group.Count; i++)
                            {
                                Condition c = group[i];
                                if (c.Lhs != null && c.Lhs.Kind == OperandKind.Memory &&
                                    c.Lhs.Address == con.Address && c.Lhs.Size == con.Size &&
                                    MemAddr.ChainSignature(group, i) == con.ChainSig)
                                { idx = i; break; }
                            }
                            if (idx < 0) continue;
                            k = new LocationKey
                            {
                                ChainSig = con.ChainSig,
                                Sample = group,
                                SampleIndex = idx,
                                Address = con.Address,
                                Size = con.Size
                            };
                            byId[id] = k;
                        }
                        foreach (double v in con.Allowed) k.Values.Add(v);
                        k.Achievements.Add(a.Id);
                    }
                }
                perAch[a.Id] = groups;
            }

            // Score each pointer chain by the discriminating power of its reads.
            var chainScore = new Dictionary<string, double>();
            foreach (LocationKey k in byId.Values)
            {
                if (k.Values.Count < 2) continue;   // a constant is not a discriminator
                double s;
                chainScore.TryGetValue(k.ChainSig, out s);
                chainScore[k.ChainSig] = s + k.Score;
            }
            if (chainScore.Count == 0) return;

            string best = "";
            double bestScore = -1;
            foreach (var kv in chainScore)
                if (kv.Value > bestScore) { bestScore = kv.Value; best = kv.Key; }
            WinningChain = best;

            foreach (LocationKey k in byId.Values)
                if (k.ChainSig == best && k.Values.Count >= 2)
                    Keys.Add(k);

            Keys.Sort(delegate (LocationKey x, LocationKey y) { return x.Address.CompareTo(y.Address); });
            ClusterKeys();

            var index = new Dictionary<string, int>();
            for (int i = 0; i < Keys.Count; i++)
                index[KeyId(Keys[i].ChainSig, Keys[i].Size, Keys[i].Address)] = i;

            foreach (Achievement a in _achs)
            {
                a.Fingerprint.Clear();
                a.AltFingerprints.Clear();
                List<List<Constraint>> groups;
                if (!perAch.TryGetValue(a.Id, out groups)) continue;

                for (int gi = 0; gi < groups.Count; gi++)
                {
                    Dictionary<int, HashSet<double>> dict = ToFingerprint(groups[gi], index);
                    if (gi == 0) a.Fingerprint = dict;
                    else a.AltFingerprints.Add(dict);
                }
            }

            PruneWeakLocations();
        }

        // Drop location constraints too vague to identify a level.
        //
        // Where the identity is a name held as text, an achievement may only compare a
        // single 4-byte window of it. A window like the "Gala" of "...Galaxy", or a
        // trailing fragment, is shared by many level names, so such an achievement
        // would pop in every one of them. Require each achievement either to constrain
        // a high-cardinality "anchor" key, or to cover a decent share of the identity
        // bytes; otherwise treat it as having no location at all.
        void PruneWeakLocations()
        {
            if (Keys.Count == 0) return;

            int maxValues = 0;
            uint lo = uint.MaxValue, hi = 0;
            for (int i = 0; i < Keys.Count; i++)
            {
                if (!IsPrimary(i)) continue;
                if (Keys[i].Values.Count > maxValues) maxValues = Keys[i].Values.Count;
                if (Keys[i].Address < lo) lo = Keys[i].Address;
                uint end = Keys[i].Address + (uint)ByteLen(Keys[i].Size);
                if (end > hi) hi = end;
            }
            if (maxValues == 0) return;

            int span = (int)(hi - lo);
            int needBytes = Math.Max(4, (int)Math.Ceiling(span * 0.4));
            int anchorCutoff = Math.Max(2, maxValues / 2);

            foreach (Achievement a in _achs)
            {
                var covered = new HashSet<int>();
                bool hasAnchor = false;

                foreach (var dict in AllFingerprints(a))
                    foreach (var kv in dict)
                    {
                        if (!IsPrimary(kv.Key)) continue;
                        LocationKey k = Keys[kv.Key];
                        if (k.Values.Count >= anchorCutoff) hasAnchor = true;
                        int off = (int)(k.Address - lo);
                        for (int b = 0; b < ByteLen(k.Size); b++) covered.Add(off + b);
                    }

                if (covered.Count == 0) continue;               // nothing to prune
                if (hasAnchor || covered.Count >= needBytes) continue;

                foreach (var dict in AllFingerprints(a))
                {
                    var drop = new List<int>();
                    foreach (var kv in dict) if (IsPrimary(kv.Key)) drop.Add(kv.Key);
                    foreach (int k in drop) dict.Remove(k);
                }
            }
        }

        static IEnumerable<Dictionary<int, HashSet<double>>> AllFingerprints(Achievement a)
        {
            yield return a.Fingerprint;
            foreach (var alt in a.AltFingerprints) yield return alt;
        }

        static Dictionary<int, HashSet<double>> ToFingerprint(List<Constraint> cons,
                                                              Dictionary<string, int> index)
        {
            var result = new Dictionary<int, HashSet<double>>();
            foreach (Constraint con in cons)
            {
                int ki;
                if (!index.TryGetValue(KeyId(con.ChainSig, con.Size, con.Address), out ki)) continue;
                HashSet<double> existing;
                if (result.TryGetValue(ki, out existing))
                {
                    // Two constraints on one key within a group: both must hold.
                    existing.IntersectWith(con.Allowed);
                    if (existing.Count == 0) return new Dictionary<int, HashSet<double>>();
                }
                else
                {
                    result[ki] = new HashSet<double>(con.Allowed);
                }
            }
            return result;
        }

        static int ByteLen(MemSize s)
        {
            switch (s)
            {
                case MemSize.U16LE: case MemSize.U16BE: return 2;
                case MemSize.U24LE: case MemSize.U24BE: return 3;
                case MemSize.U32LE: case MemSize.U32BE:
                case MemSize.Float: case MemSize.FloatBE: return 4;
                default: return 1;
            }
        }

        // Group keys occupying overlapping or adjacent bytes. A multi-word string field
        // (a stage name) becomes one cluster; an unrelated scalar such as a mission index
        // becomes another. The richest cluster identifies the level; the rest narrow it
        // to a section inside that level.
        void ClusterKeys()
        {
            if (Keys.Count == 0) return;
            int cluster = 0;
            Keys[0].Cluster = 0;
            for (int i = 1; i < Keys.Count; i++)
            {
                LocationKey prev = Keys[i - 1];
                if (Keys[i].Address > prev.Address + (uint)ByteLen(prev.Size)) cluster++;
                Keys[i].Cluster = cluster;
            }

            var score = new Dictionary<int, double>();
            foreach (LocationKey k in Keys)
            {
                double s;
                score.TryGetValue(k.Cluster, out s);
                score[k.Cluster] = s + k.Score;
            }
            double bestScore = -1;
            foreach (var kv in score)
                if (kv.Value > bestScore) { bestScore = kv.Value; PrimaryCluster = kv.Key; }
        }

        public bool IsPrimary(int keyIndex)
        {
            return keyIndex >= 0 && keyIndex < Keys.Count && Keys[keyIndex].Cluster == PrimaryCluster;
        }

        // Current value of each location key, or null where unreadable.
        public double?[] ReadCurrent(RcEval eval)
        {
            var vals = new double?[Keys.Count];
            for (int i = 0; i < Keys.Count; i++)
            {
                double v;
                if (eval.TryReadLhs(Keys[i].Sample, Keys[i].SampleIndex, out v)) vals[i] = v;
                else vals[i] = null;
            }
            return vals;
        }

        public bool MatchesLevel(Achievement a, double?[] current) { return MatchesInternal(a, current, true); }
        public bool MatchesSection(Achievement a, double?[] current) { return MatchesInternal(a, current, false); }

        // A trigger needs its core group and at least one alt group to hold, so the
        // achievement belongs here when the core constraints match and — if there are
        // alt groups — some alt group's constraints match too.
        bool MatchesInternal(Achievement a, double?[] current, bool primaryOnly)
        {
            if (!a.HasLocation) return false;

            bool anyChecked;
            if (!GroupMatches(a.Fingerprint, current, primaryOnly, out anyChecked)) return false;

            if (a.AltFingerprints.Count > 0)
            {
                bool someAltHolds = false, altChecked = false;
                foreach (var alt in a.AltFingerprints)
                {
                    bool checkedHere;
                    if (GroupMatches(alt, current, primaryOnly, out checkedHere))
                    {
                        someAltHolds = true;
                        if (checkedHere) altChecked = true;
                    }
                }
                if (!someAltHolds) return false;
                anyChecked = anyChecked || altChecked;
            }
            return anyChecked;
        }

        bool GroupMatches(Dictionary<int, HashSet<double>> fp, double?[] current,
                          bool primaryOnly, out bool anyChecked)
        {
            anyChecked = false;
            foreach (var kv in fp)
            {
                if (kv.Key >= current.Length) return false;
                if (primaryOnly && !IsPrimary(kv.Key)) continue;
                anyChecked = true;
                double? cur = current[kv.Key];
                if (cur == null || !kv.Value.Contains(cur.Value)) return false;
            }
            return true;
        }

        // Does this achievement narrow to a section beyond the level itself?
        public bool HasSectionConstraint(Achievement a)
        {
            foreach (var kv in a.Fingerprint)
                if (!IsPrimary(kv.Key)) return true;
            foreach (var alt in a.AltFingerprints)
                foreach (var kv in alt)
                    if (!IsPrimary(kv.Key)) return true;
            return false;
        }

        public string LevelSignature(double?[] current) { return Signature(current, true); }
        public string SectionSignature(double?[] current) { return Signature(current, false); }

        string Signature(double?[] current, bool primaryOnly)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < current.Length; i++)
            {
                if (primaryOnly && !IsPrimary(i)) continue;
                if (sb.Length > 0) sb.Append(',');
                sb.Append(current[i] == null ? "?" : current[i].Value.ToString("R", CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }
    }
}
