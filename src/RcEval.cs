using System;
using System.Collections.Generic;

namespace DolphinAchiever
{
    // Evaluates the subset of rcheevos semantics needed to answer
    // "where is the player right now?" -- pointer chains (AddAddress),
    // typed reads, operand arithmetic and comparisons.
    public class RcEval
    {
        readonly DolphinMemory _mem;
        readonly Dictionary<uint, double> _prev = new Dictionary<uint, double>();

        public RcEval(DolphinMemory mem) { _mem = mem; }

        public bool TryOperand(Operand o, uint addend, out double value)
        {
            value = 0;
            if (o == null) return false;
            if (o.Kind == OperandKind.Constant) { value = o.Value; return true; }
            if (o.Kind == OperandKind.Recall) return false;   // handled by caller

            uint addr = unchecked(o.Address + addend);
            double v;
            if (!_mem.TryRead(addr, o.Size, out v)) return false;

            switch (o.Mod)
            {
                case OperandMod.Delta:
                case OperandMod.Prior:
                    {
                        double old;
                        bool had = _prev.TryGetValue(addr, out old);
                        _prev[addr] = v;
                        if (!had) return false;
                        value = old;
                        return true;
                    }
                case OperandMod.Bcd:
                    {
                        long iv = (long)v, r = 0, mul = 1;
                        while (iv > 0) { r += (iv & 0x0F) * mul; mul *= 10; iv >>= 4; }
                        value = r;
                        return true;
                    }
                case OperandMod.Invert:
                    value = ~(uint)v;
                    return true;
                default:
                    _prev[addr] = v;
                    value = v;
                    return true;
            }
        }

        static double ApplyArith(double a, string op, double b)
        {
            switch (op)
            {
                case "&": return (double)(((ulong)a) & ((ulong)b));
                case "^": return (double)(((ulong)a) ^ ((ulong)b));
                case "%": return b == 0 ? 0 : (double)(((ulong)a) % ((ulong)b));
                case "*": return a * b;
                case "/": return b == 0 ? 0 : a / b;
                case "+": return a + b;
                case "-": return a - b;
                default: return a;
            }
        }

        static bool IsComparison(string op)
        {
            return op == "=" || op == "==" || op == "!=" || op == "<" || op == "<=" || op == ">" || op == ">=";
        }

        public static bool Compare(double a, string op, double b)
        {
            switch (op)
            {
                case "=":
                case "==": return a == b;
                case "!=": return a != b;
                case "<": return a < b;
                case "<=": return a <= b;
                case ">": return a > b;
                case ">=": return a >= b;
                default: return false;
            }
        }

        // Accumulate the AddAddress pointer chain that precedes index 'i'.
        // Returns false if any link is unreadable or null.
        public bool TryChain(List<Condition> conds, int i, out uint addend)
        {
            addend = 0;
            int start = i - 1;
            while (start >= 0 && conds[start].Flag == CondFlag.AddAddress) start--;
            start++;

            uint acc = 0;
            for (int j = start; j < i; j++)
            {
                Condition c = conds[j];
                double v;
                if (!TryOperand(c.Lhs, acc, out v)) return false;
                if (c.Op != null && !IsComparison(c.Op) && c.Rhs != null)
                {
                    double rv;
                    if (!TryOperand(c.Rhs, acc, out rv)) return false;
                    v = ApplyArith(v, c.Op, rv);
                }
                acc = (uint)v;
            }
            addend = acc;
            return true;
        }

        // Evaluate a standalone comparison condition (with its pointer chain).
        public bool TryEvalComparison(List<Condition> conds, int i, out bool result)
        {
            result = false;
            Condition c = conds[i];
            if (c.Op == null || !IsComparison(c.Op)) return false;
            uint addend;
            if (!TryChain(conds, i, out addend)) return false;
            double a, b;
            if (!TryOperand(c.Lhs, addend, out a)) return false;
            if (!TryOperand(c.Rhs, addend, out b)) return false;
            result = Compare(a, c.Op, b);
            return true;
        }

        // Read the value a condition's left-hand memory operand currently holds.
        public bool TryReadLhs(List<Condition> conds, int i, out double value)
        {
            value = 0;
            uint addend;
            if (!TryChain(conds, i, out addend)) return false;
            return TryOperand(conds[i].Lhs, addend, out value);
        }

        // Resolve the absolute (physical) address a condition's LHS refers to.
        public bool TryResolveAddress(List<Condition> conds, int i, out uint addr)
        {
            addr = 0;
            uint addend;
            if (!TryChain(conds, i, out addend)) return false;
            addr = unchecked(conds[i].Lhs.Address + addend);
            return true;
        }

        // Evaluate a value expression group (used by Rich Presence macros):
        // conditions are ANDed (AndNext/MeasuredIf), and the Measured operand
        // supplies the value when they all hold.
        public bool TryEvalValueGroup(List<Condition> g, out double value)
        {
            value = 0;
            bool haveMeasured = false;
            double measured = 0;
            double addSource = 0;

            for (int i = 0; i < g.Count; i++)
            {
                Condition c = g[i];
                if (c.Flag == CondFlag.AddAddress) continue;

                if (c.Flag == CondFlag.Measured || c.Flag == CondFlag.MeasuredPercent)
                {
                    uint addend;
                    if (!TryChain(g, i, out addend)) return false;
                    double v;
                    if (!TryOperand(c.Lhs, addend, out v)) return false;
                    if (c.Op != null && !IsComparison(c.Op) && c.Rhs != null)
                    {
                        double rv;
                        if (!TryOperand(c.Rhs, addend, out rv)) return false;
                        v = ApplyArith(v, c.Op, rv);
                    }
                    measured = v + addSource;
                    addSource = 0;
                    haveMeasured = true;
                    continue;
                }

                if (c.Flag == CondFlag.AddSource || c.Flag == CondFlag.SubSource)
                {
                    uint addend;
                    if (!TryChain(g, i, out addend)) return false;
                    double v;
                    if (!TryOperand(c.Lhs, addend, out v)) return false;
                    if (c.Op != null && !IsComparison(c.Op) && c.Rhs != null)
                    {
                        double rv;
                        if (!TryOperand(c.Rhs, addend, out rv)) return false;
                        v = ApplyArith(v, c.Op, rv);
                    }
                    addSource += (c.Flag == CondFlag.AddSource) ? v : -v;
                    continue;
                }

                // AndNext / MeasuredIf / plain: all must hold.
                if (c.Op != null && IsComparison(c.Op))
                {
                    bool ok;
                    if (!TryEvalComparison(g, i, out ok)) return false;
                    if (!ok) return false;
                }
            }

            if (!haveMeasured) return false;
            value = measured;
            return true;
        }

        // A value expression is the maximum over its alternative groups.
        public bool TryEvalValue(Trigger t, out double value)
        {
            value = 0;
            bool any = false;
            foreach (var g in t.Groups)
            {
                if (g.Count == 0) continue;
                double v;
                if (TryEvalValueGroup(g, out v))
                {
                    if (!any || v > value) value = v;
                    any = true;
                }
            }
            return any;
        }
    }
}
