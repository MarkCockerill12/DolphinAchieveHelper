using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace DolphinAchiever
{
    // Read-only access to Dolphin's emulated GameCube/Wii RAM.
    //
    // Addresses used by RetroAchievements are *physical* guest addresses, per the
    // rcheevos console memory map:
    //   GameCube : 0x00000000-0x017FFFFF -> MEM1 (guest 0x80000000)
    //   Wii      : additionally 0x10000000-0x13FFFFFF -> MEM2 (guest 0x90000000)
    // This matches Dolphin's own AchievementManager::MemoryPeeker, which reads with
    // RequestedAddressSpace::Physical.
    public class DolphinMemory : IDisposable
    {
        const int PROCESS_QUERY_INFORMATION = 0x0400;
        const int PROCESS_VM_READ = 0x0010;
        const uint MEM_COMMIT = 0x1000;
        const uint MEM_MAPPED = 0x40000;

        const ulong MEM1_SIZE = 0x2000000;   // 32MB region (24MB usable)
        const ulong MEM2_SIZE = 0x4000000;   // 64MB
        const ulong FASTMEM_MEM2_OFFSET = 0x10000000;

        [StructLayout(LayoutKind.Sequential)]
        struct MEMORY_BASIC_INFORMATION
        {
            public IntPtr BaseAddress;
            public IntPtr AllocationBase;
            public uint AllocationProtect;
            public uint __alignment1;
            public IntPtr RegionSize;
            public uint State;
            public uint Protect;
            public uint Type;
            public uint __alignment2;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr OpenProcess(int access, bool inherit, int pid);
        [DllImport("kernel32.dll")]
        static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll")]
        static extern IntPtr VirtualQueryEx(IntPtr h, IntPtr addr, out MEMORY_BASIC_INFORMATION mbi, IntPtr len);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool ReadProcessMemory(IntPtr h, IntPtr addr, byte[] buf, IntPtr size, out IntPtr read);

        IntPtr _proc = IntPtr.Zero;
        int _pid = -1;
        ulong _mem1, _mem2;

        public bool IsAttached { get { return _proc != IntPtr.Zero && _mem1 != 0; } }
        public int ProcessId { get { return _pid; } }
        public string GameId { get; private set; }
        public bool IsWii { get; private set; }

        struct Region { public ulong Base, Size; public uint Type; }

        public void Dispose()
        {
            Detach();
        }

        public void Detach()
        {
            if (_proc != IntPtr.Zero) CloseHandle(_proc);
            _proc = IntPtr.Zero;
            _mem1 = _mem2 = 0;
            _pid = -1;
            GameId = null;
        }

        static Process FindDolphin()
        {
            foreach (string name in new[] { "Dolphin", "DolphinQt", "Dolphin-x64" })
            {
                Process[] ps = Process.GetProcessesByName(name);
                if (ps.Length > 0) return ps[0];
            }
            return null;
        }

        // Attach to a running Dolphin and locate emulated RAM.
        // Returns false if Dolphin is not running or no game is booted.
        public bool TryAttach()
        {
            if (IsAttached && IsAlive()) return true;
            Detach();

            Process p = FindDolphin();
            if (p == null) return false;

            IntPtr h = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, false, p.Id);
            if (h == IntPtr.Zero) return false;
            _proc = h;
            _pid = p.Id;

            if (!Locate()) { Detach(); return false; }
            return true;
        }

        bool IsAlive()
        {
            try { Process.GetProcessById(_pid); return true; }
            catch { return false; }
        }

        bool Locate()
        {
            var regions = new List<Region>();
            var mem1Candidates = new List<ulong>();
            byte[] hdr = new byte[0x40];

            ulong addr = 0;
            MEMORY_BASIC_INFORMATION mbi;
            int sz = Marshal.SizeOf(typeof(MEMORY_BASIC_INFORMATION));
            while (addr < 0x00007FFFFFFF0000UL)
            {
                if (VirtualQueryEx(_proc, (IntPtr)(long)addr, out mbi, (IntPtr)sz) == IntPtr.Zero) break;
                ulong size = (ulong)(long)mbi.RegionSize;
                if (size == 0) break;
                ulong b = (ulong)(long)mbi.BaseAddress;

                if (mbi.State == MEM_COMMIT && mbi.Type == MEM_MAPPED && size >= 0x1000000UL)
                {
                    regions.Add(new Region { Base = b, Size = size, Type = mbi.Type });
                    if (size >= 0x1800000UL && ReadRaw(b, hdr, 0x40) && IsDiscHeader(hdr))
                        mem1Candidates.Add(b);
                }
                addr = b + size;
            }

            if (mem1Candidates.Count == 0) return false;

            // Prefer the fastmem arena: the candidate that has a 64MB mapped region
            // exactly 0x10000000 above it. Dolphin maps RAM several times, and only in
            // that arena does MEM1+0x10000000 actually land on MEM2.
            ulong chosen1 = 0, chosen2 = 0;
            foreach (ulong c in mem1Candidates)
            {
                foreach (Region r in regions)
                {
                    if (r.Base == c + FASTMEM_MEM2_OFFSET && r.Size >= MEM2_SIZE)
                    {
                        chosen1 = c; chosen2 = r.Base; break;
                    }
                }
                if (chosen1 != 0) break;
            }

            if (chosen1 == 0)
            {
                // Fall back to the first candidate; pair it with the nearest following
                // 64MB mapped region (the non-fastmem arena layout).
                chosen1 = mem1Candidates[0];
                ulong best = ulong.MaxValue;
                foreach (Region r in regions)
                {
                    if (r.Base > chosen1 && r.Size >= MEM2_SIZE && r.Base - chosen1 < best)
                    {
                        best = r.Base - chosen1;
                        chosen2 = r.Base;
                    }
                }
                // Only trust it if it is close by (same arena), not a random heap block.
                if (best > 0x20000000UL) chosen2 = 0;
            }

            _mem1 = chosen1;
            _mem2 = chosen2;

            if (!ReadRaw(_mem1, hdr, 0x40)) return false;
            GameId = Encoding.ASCII.GetString(hdr, 0, 6);
            IsWii = BE32(hdr, 0x18) == 0x5D1C9EA3;
            return true;
        }

        static bool IsDiscHeader(byte[] hdr)
        {
            bool wii = BE32(hdr, 0x18) == 0x5D1C9EA3;
            bool gc = BE32(hdr, 0x1C) == 0xC2339F3D;
            if (!wii && !gc) return false;
            for (int i = 0; i < 6; i++)
                if (hdr[i] < 0x20 || hdr[i] > 0x7E) return false;
            return true;
        }

        static uint BE32(byte[] b, int o)
        {
            return ((uint)b[o] << 24) | ((uint)b[o + 1] << 16) | ((uint)b[o + 2] << 8) | b[o + 3];
        }

        bool ReadRaw(ulong host, byte[] buf, int len)
        {
            IntPtr got;
            return ReadProcessMemory(_proc, (IntPtr)(long)host, buf, (IntPtr)len, out got) && (long)got == len;
        }

        // Map a RetroAchievements (physical guest) address to a host address.
        ulong Resolve(uint ra, int len)
        {
            if (ra < 0x01800000U && ra + (uint)len <= 0x01800000U) return _mem1 + ra;
            if (_mem2 != 0 && ra >= 0x10000000U && ra + (uint)len <= 0x14000000U)
                return _mem2 + (ra - 0x10000000U);
            return 0;
        }

        readonly byte[] _tmp = new byte[8];

        public bool TryReadBytes(uint ra, byte[] buf, int len)
        {
            ulong h = Resolve(ra, len);
            if (h == 0) return false;
            return ReadRaw(h, buf, len);
        }

        // Read a null-terminated ASCII string (used for games that identify levels by name).
        public string ReadString(uint ra, int max)
        {
            byte[] b = new byte[max];
            int n = max;
            while (n > 0 && !TryReadBytes(ra, b, n)) n /= 2;
            if (n == 0) return null;
            int len = 0;
            while (len < n && b[len] != 0) len++;
            var sb = new StringBuilder(len);
            for (int i = 0; i < len; i++)
                sb.Append(b[i] >= 0x20 && b[i] < 0x7F ? (char)b[i] : '?');
            return sb.ToString();
        }

        static uint BitsFor(MemSize s)
        {
            switch (s)
            {
                case MemSize.U8: case MemSize.LowNibble: case MemSize.HighNibble:
                case MemSize.BitCount:
                case MemSize.Bit0: case MemSize.Bit1: case MemSize.Bit2: case MemSize.Bit3:
                case MemSize.Bit4: case MemSize.Bit5: case MemSize.Bit6: case MemSize.Bit7:
                    return 1;
                case MemSize.U16LE: case MemSize.U16BE: return 2;
                case MemSize.U24LE: case MemSize.U24BE: return 3;
                default: return 4;
            }
        }

        static int PopCount(byte v)
        {
            int n = 0;
            while (v != 0) { n += v & 1; v >>= 1; }
            return n;
        }

        // Read a value using rcheevos size semantics. Returns false if unreadable.
        public bool TryRead(uint ra, MemSize size, out double value)
        {
            value = 0;
            int len = (int)BitsFor(size);
            ulong h = Resolve(ra, len);
            if (h == 0) return false;
            if (!ReadRaw(h, _tmp, len)) return false;
            byte b0 = _tmp[0];
            switch (size)
            {
                case MemSize.U8: value = b0; return true;
                case MemSize.LowNibble: value = b0 & 0x0F; return true;
                case MemSize.HighNibble: value = (b0 >> 4) & 0x0F; return true;
                case MemSize.BitCount: value = PopCount(b0); return true;
                case MemSize.Bit0: value = (b0 >> 0) & 1; return true;
                case MemSize.Bit1: value = (b0 >> 1) & 1; return true;
                case MemSize.Bit2: value = (b0 >> 2) & 1; return true;
                case MemSize.Bit3: value = (b0 >> 3) & 1; return true;
                case MemSize.Bit4: value = (b0 >> 4) & 1; return true;
                case MemSize.Bit5: value = (b0 >> 5) & 1; return true;
                case MemSize.Bit6: value = (b0 >> 6) & 1; return true;
                case MemSize.Bit7: value = (b0 >> 7) & 1; return true;
                case MemSize.U16LE: value = (uint)(_tmp[0] | (_tmp[1] << 8)); return true;
                case MemSize.U16BE: value = (uint)((_tmp[0] << 8) | _tmp[1]); return true;
                case MemSize.U24LE: value = (uint)(_tmp[0] | (_tmp[1] << 8) | (_tmp[2] << 16)); return true;
                case MemSize.U24BE: value = (uint)((_tmp[0] << 16) | (_tmp[1] << 8) | _tmp[2]); return true;
                case MemSize.U32LE:
                    value = (uint)(_tmp[0] | (_tmp[1] << 8) | (_tmp[2] << 16) | (_tmp[3] << 24)); return true;
                case MemSize.U32BE:
                    value = BE32(_tmp, 0); return true;
                case MemSize.Float:
                    value = BitConverter.ToSingle(_tmp, 0); return true;
                case MemSize.FloatBE:
                    {
                        byte[] r = { _tmp[3], _tmp[2], _tmp[1], _tmp[0] };
                        value = BitConverter.ToSingle(r, 0); return true;
                    }
                default:
                    value = BE32(_tmp, 0); return true;
            }
        }
    }
}
