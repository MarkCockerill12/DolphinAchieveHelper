using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace DolphinAchiever
{
    // Size of a memory read, matching rcheevos RC_MEMSIZE_*.
    public enum MemSize
    {
        U8, U16LE, U16BE, U24LE, U24BE, U32LE, U32BE,
        LowNibble, HighNibble, BitCount,
        Bit0, Bit1, Bit2, Bit3, Bit4, Bit5, Bit6, Bit7,
        Float, FloatBE, Double32, Double32BE, MBF32, MBF32LE
    }

    // Operand modifier prefix: d=delta, p=prior, b=BCD, ~=invert.
    public enum OperandMod { None, Delta, Prior, Bcd, Invert }

    public enum OperandKind { Memory, Constant, Recall }

    // Condition flag prefix, matching rcheevos RC_CONDITION_*.
    public enum CondFlag
    {
        None, PauseIf, ResetIf, AddSource, SubSource, AddHits, SubHits,
        AndNext, OrNext, Measured, MeasuredIf, AddAddress, Trigger,
        ResetNextIf, Remember, MeasuredPercent
    }

    public class Operand
    {
        public OperandKind Kind;
        public MemSize Size;
        public uint Address;
        public OperandMod Mod;
        public double Value;      // for Constant
        public bool IsFloat;      // constant was a float literal

        public override string ToString()
        {
            if (Kind == OperandKind.Constant) return Value.ToString(CultureInfo.InvariantCulture);
            if (Kind == OperandKind.Recall) return "{recall}";
            return Size + "@" + Address.ToString("X");
        }
    }

    public class Condition
    {
        public CondFlag Flag;
        public Operand Lhs;
        public string Op;        // null when there is no comparison/modifier
        public Operand Rhs;
        public uint RequiredHits;
    }

    public class Trigger
    {
        // Groups[0] is the core group; the remainder are alt groups.
        public List<List<Condition>> Groups = new List<List<Condition>>();
        public List<Condition> Core { get { return Groups.Count > 0 ? Groups[0] : new List<Condition>(); } }
    }

    // Parser for the rcheevos MemAddr serialization format.
    public static class MemAddr
    {
        static readonly string[] Operators = { "!=", "<=", ">=", "==", "=", "<", ">", "*", "/", "&", "^", "%", "+", "-" };

        static CondFlag FlagFor(char c)
        {
            switch (char.ToUpperInvariant(c))
            {
                case 'P': return CondFlag.PauseIf;
                case 'R': return CondFlag.ResetIf;
                case 'A': return CondFlag.AddSource;
                case 'B': return CondFlag.SubSource;
                case 'C': return CondFlag.AddHits;
                case 'D': return CondFlag.SubHits;
                case 'N': return CondFlag.AndNext;
                case 'O': return CondFlag.OrNext;
                case 'M': return CondFlag.Measured;
                case 'Q': return CondFlag.MeasuredIf;
                case 'I': return CondFlag.AddAddress;
                case 'T': return CondFlag.Trigger;
                case 'Z': return CondFlag.ResetNextIf;
                case 'K': return CondFlag.Remember;
                case 'G': return CondFlag.MeasuredPercent;
                default: return CondFlag.None;
            }
        }

        static bool IsFlagChar(char c)
        {
            return "PRABCDNOMQITZKGprabcdnomqitzkg".IndexOf(c) >= 0;
        }

        static bool TryMemSize(char c, out MemSize size)
        {
            switch (char.ToLowerInvariant(c))
            {
                case 'h': size = MemSize.U8; return true;
                case 'x': size = MemSize.U32LE; return true;
                case 'g': size = MemSize.U32BE; return true;
                case 'i': size = MemSize.U16BE; return true;
                case 'j': size = MemSize.U24BE; return true;
                case 'w': size = MemSize.U24LE; return true;
                case 'l': size = MemSize.LowNibble; return true;
                case 'u': size = MemSize.HighNibble; return true;
                case 'k': size = MemSize.BitCount; return true;
                case 'm': size = MemSize.Bit0; return true;
                case 'n': size = MemSize.Bit1; return true;
                case 'o': size = MemSize.Bit2; return true;
                case 'p': size = MemSize.Bit3; return true;
                case 'q': size = MemSize.Bit4; return true;
                case 'r': size = MemSize.Bit5; return true;
                case 's': size = MemSize.Bit6; return true;
                case 't': size = MemSize.Bit7; return true;
                default: size = MemSize.U16LE; return false;
            }
        }

        static bool TryFloatSize(char c, out MemSize size)
        {
            switch (char.ToLowerInvariant(c))
            {
                case 'f': size = MemSize.Float; return true;
                case 'b': size = MemSize.FloatBE; return true;
                case 'h': size = MemSize.Double32; return true;
                case 'i': size = MemSize.Double32BE; return true;
                case 'm': size = MemSize.MBF32; return true;
                case 'l': size = MemSize.MBF32LE; return true;
                default: size = MemSize.Float; return false;
            }
        }

        static bool IsHex(char c)
        {
            return (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
        }

        static Operand ParseOperand(string s, ref int i)
        {
            var op = new Operand();
            op.Mod = OperandMod.None;

            // Modifier prefix must be followed by a memory reference.
            if (i + 1 < s.Length && "dDpPbB".IndexOf(s[i]) >= 0 &&
                (s[i + 1] == '0' || s[i + 1] == 'f' || s[i + 1] == 'F'))
            {
                char m = char.ToLowerInvariant(s[i]);
                op.Mod = m == 'd' ? OperandMod.Delta : (m == 'p' ? OperandMod.Prior : OperandMod.Bcd);
                i++;
            }
            else if (s[i] == '~')
            {
                op.Mod = OperandMod.Invert;
                i++;
            }

            if (string.CompareOrdinal(s, i, "{recall}", 0, 8) == 0)
            {
                i += 8;
                op.Kind = OperandKind.Recall;
                return op;
            }

            // 0x<sizechar><hexaddr>
            if (i + 1 < s.Length && s[i] == '0' && (s[i + 1] == 'x' || s[i + 1] == 'X'))
            {
                i += 2;
                MemSize size;
                if (i < s.Length && TryMemSize(s[i], out size)) i++;
                else size = MemSize.U16LE;   // bare "0x" means 16-bit
                op.Kind = OperandKind.Memory;
                op.Size = size;
                op.Address = ReadHex(s, ref i);
                return op;
            }

            // An 'f' introduces either a float literal (f1.5, f-3) or a float memory
            // reference (fB0c, fB00000130). A digit or sign after the 'f' means a
            // literal; a size letter means a memory reference. Address length cannot be
            // used to tell them apart, because addresses may be as short as two digits.
            if (s[i] == 'f' || s[i] == 'F')
            {
                if (i + 1 < s.Length &&
                    (char.IsDigit(s[i + 1]) || s[i + 1] == '-' || s[i + 1] == '+'))
                {
                    i++;
                    op.Kind = OperandKind.Constant;
                    op.IsFloat = true;
                    op.Value = ReadNumber(s, ref i, true);
                    return op;
                }

                MemSize fsize;
                if (i + 2 < s.Length && TryFloatSize(s[i + 1], out fsize) && IsHex(s[i + 2]))
                {
                    i += 2;
                    op.Kind = OperandKind.Memory;
                    op.Size = fsize;
                    op.Address = ReadHex(s, ref i);
                    return op;
                }
            }

            // integer literal, optional 'v' prefix
            if (s[i] == 'v' || s[i] == 'V') i++;
            op.Kind = OperandKind.Constant;
            if (i + 1 < s.Length && s[i] == '0' && (s[i + 1] == 'x' || s[i + 1] == 'X'))
            {
                i += 2;
                op.Value = ReadHex(s, ref i);
            }
            else
            {
                op.Value = ReadNumber(s, ref i, false);
            }
            return op;
        }

        static uint ReadHex(string s, ref int i)
        {
            int start = i;
            while (i < s.Length && IsHex(s[i])) i++;
            if (i == start) throw new FormatException("expected hex at " + start);
            return uint.Parse(s.Substring(start, i - start), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }

        static double ReadNumber(string s, ref int i, bool allowFraction)
        {
            int start = i;
            if (i < s.Length && (s[i] == '-' || s[i] == '+')) i++;
            while (i < s.Length && char.IsDigit(s[i])) i++;
            if (allowFraction && i < s.Length && s[i] == '.')
            {
                i++;
                while (i < s.Length && char.IsDigit(s[i])) i++;
            }
            if (i == start) throw new FormatException("expected number at " + start);
            return double.Parse(s.Substring(start, i - start), CultureInfo.InvariantCulture);
        }

        static Condition ParseCondition(string s, ref int i)
        {
            var c = new Condition();
            c.Flag = CondFlag.None;
            if (i + 1 < s.Length && IsFlagChar(s[i]) && s[i + 1] == ':')
            {
                c.Flag = FlagFor(s[i]);
                i += 2;
            }
            c.Lhs = ParseOperand(s, ref i);

            foreach (string o in Operators)
            {
                if (i + o.Length <= s.Length && string.CompareOrdinal(s, i, o, 0, o.Length) == 0)
                {
                    c.Op = o;
                    i += o.Length;
                    c.Rhs = ParseOperand(s, ref i);
                    break;
                }
            }

            // hit count: (N) or .N.
            if (i < s.Length && s[i] == '(')
            {
                int j = i + 1, st = j;
                while (j < s.Length && char.IsDigit(s[j])) j++;
                if (j < s.Length && s[j] == ')' && j > st)
                {
                    c.RequiredHits = uint.Parse(s.Substring(st, j - st), CultureInfo.InvariantCulture);
                    i = j + 1;
                }
            }
            else if (i < s.Length && s[i] == '.')
            {
                int j = i + 1, st = j;
                while (j < s.Length && char.IsDigit(s[j])) j++;
                if (j < s.Length && s[j] == '.' && j > st)
                {
                    c.RequiredHits = uint.Parse(s.Substring(st, j - st), CultureInfo.InvariantCulture);
                    i = j + 1;
                }
            }
            return c;
        }

        // Parse a full trigger (achievement MemAddr) or value expression.
        // 'S' and '$' both start a new group.
        public static Trigger Parse(string s)
        {
            var t = new Trigger();
            t.Groups.Add(new List<Condition>());
            if (string.IsNullOrEmpty(s)) return t;
            int i = 0;
            while (i < s.Length)
            {
                char c = s[i];
                if (c == '_') { i++; continue; }
                if (c == 'S' || c == '$') { i++; t.Groups.Add(new List<Condition>()); continue; }
                int before = i;
                t.Groups[t.Groups.Count - 1].Add(ParseCondition(s, ref i));
                if (i == before) throw new FormatException("no progress at " + i + " in " + s);
            }
            return t;
        }

        // Stable signature of the AddAddress pointer chain preceding condition 'index'.
        public static string ChainSignature(List<Condition> conds, int index)
        {
            var parts = new List<string>();
            int j = index - 1;
            while (j >= 0 && conds[j].Flag == CondFlag.AddAddress)
            {
                var c = conds[j];
                if (c.Lhs.Kind != OperandKind.Memory) break;
                var sb = new StringBuilder();
                sb.Append(c.Lhs.Size).Append('@').Append(c.Lhs.Address.ToString("X"));
                if (c.Op != null && c.Rhs != null && c.Rhs.Kind == OperandKind.Constant)
                    sb.Append(c.Op).Append(c.Rhs.Value.ToString(CultureInfo.InvariantCulture));
                parts.Add(sb.ToString());
                j--;
            }
            parts.Reverse();
            return string.Join("->", parts.ToArray());
        }
    }
}
