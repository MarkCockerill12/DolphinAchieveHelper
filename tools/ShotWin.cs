using System;using System.Collections.Generic;using System.Diagnostics;using System.Drawing;
using System.Runtime.InteropServices;using System.Text;
class ShotWin{
 [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr p);
 [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
 [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
 [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
 [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h,int i);
 [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);
 delegate bool EnumProc(IntPtr h, IntPtr p);
 [StructLayout(LayoutKind.Sequential)] struct RECT{public int L,T,R,B;}
 static void Main(string[] a){
  string proc=a[0]; string outp=a[1];
  var pids=new List<uint>();
  foreach(Process p in Process.GetProcessesByName(proc)) pids.Add((uint)p.Id);
  if(pids.Count==0){Console.WriteLine("process not running");return;}
  var found=new List<IntPtr>();
  EnumWindows(delegate(IntPtr h, IntPtr l){
    uint pid; GetWindowThreadProcessId(h,out pid);
    if(pids.Contains(pid)) found.Add(h);
    return true;},IntPtr.Zero);
  int n=0;
  foreach(IntPtr h in found){
    RECT r; GetWindowRect(h,out r);
    int w=r.R-r.L,ht=r.B-r.T;
    Console.WriteLine("hwnd="+h+" vis="+IsWindowVisible(h)+" rect="+r.L+","+r.T+" "+w+"x"+ht+" ex=0x"+GetWindowLong(h,-20).ToString("X"));
    if(w<=0||ht<=0||!IsWindowVisible(h)) continue;
    using(Bitmap bmp=new Bitmap(w,ht)){
      using(Graphics g=Graphics.FromImage(bmp)){
        IntPtr dc=g.GetHdc();
        bool ok=PrintWindow(h,dc,2); // PW_RENDERFULLCONTENT
        g.ReleaseHdc(dc);
        Console.WriteLine("  PrintWindow="+ok);
      }
      string f=outp.Replace(".png","_"+(n++)+".png");
      bmp.Save(f,System.Drawing.Imaging.ImageFormat.Png);
      Console.WriteLine("  saved "+f);
    }
  }
 }
}
