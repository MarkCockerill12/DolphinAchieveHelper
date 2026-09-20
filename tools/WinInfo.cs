using System;using System.Collections.Generic;using System.Diagnostics;
using System.Runtime.InteropServices;using System.Text;
class WinInfo{
 [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr p);
 [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
 [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
 [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
 [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr h, out RECT r);
 [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h,int i);
 [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
 [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, IntPtr extra);
 delegate bool EnumProc(IntPtr h, IntPtr p);
 [StructLayout(LayoutKind.Sequential)] struct RECT{public int L,T,R,B;}
 const int GWL_STYLE=-16, GWL_EXSTYLE=-20;

 static List<IntPtr> Windows(string proc){
  var pids=new List<uint>();
  foreach(Process p in Process.GetProcessesByName(proc)) pids.Add((uint)p.Id);
  var found=new List<IntPtr>();
  EnumWindows(delegate(IntPtr h, IntPtr l){
    uint pid; GetWindowThreadProcessId(h,out pid);
    if(pids.Contains(pid) && IsWindowVisible(h)) found.Add(h);
    return true;},IntPtr.Zero);
  return found;
 }

 static void Dump(string proc,string label){
  Console.WriteLine("--- "+label+" ---");
  foreach(IntPtr h in Windows(proc)){
   RECT w,c; GetWindowRect(h,out w); GetClientRect(h,out c);
   int st=GetWindowLong(h,GWL_STYLE), ex=GetWindowLong(h,GWL_EXSTYLE);
   if((w.R-w.L)<200) continue;
   Console.WriteLine(string.Format(
     "hwnd={0} rect=({1},{2}) {3}x{4} client={5}x{6} style=0x{7:X8} ex=0x{8:X8} caption={9} topmost={10}",
     h,w.L,w.T,w.R-w.L,w.B-w.T,c.R-c.L,c.B-c.T,st,ex,
     (st & 0x00C00000)!=0, (ex & 0x00000008)!=0));
  }
 }

 static void Main(string[] a){
  string proc=a[0];
  Dump(proc,"before");
  if(a.Length>1 && a[1]=="altenter"){
   var ws=Windows(proc);
   IntPtr big=IntPtr.Zero; int bw=0;
   foreach(IntPtr h in ws){RECT r;GetWindowRect(h,out r); if(r.R-r.L>bw){bw=r.R-r.L;big=h;}}
   SetForegroundWindow(big);
   System.Threading.Thread.Sleep(800);
   keybd_event(0x12,0,0,IntPtr.Zero);              // ALT down
   keybd_event(0x0D,0,0,IntPtr.Zero);              // ENTER down
   keybd_event(0x0D,0,2,IntPtr.Zero);              // ENTER up
   keybd_event(0x12,0,2,IntPtr.Zero);              // ALT up
   System.Threading.Thread.Sleep(2500);
   Dump(proc,"after alt+enter");
  }
 }
}
