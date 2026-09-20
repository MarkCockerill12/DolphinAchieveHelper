using System;using System.Drawing;using System.Runtime.InteropServices;using System.Windows.Forms;
class Shot{
 [DllImport("user32.dll")] static extern IntPtr GetDesktopWindow();
 [DllImport("user32.dll")] static extern IntPtr GetWindowDC(IntPtr h);
 [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
 [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr d,int x,int y,int w,int h,IntPtr s,int sx,int sy,int rop);
 [STAThread] static void Main(string[] a){
  Rectangle b=Screen.PrimaryScreen.Bounds;
  IntPtr dw=GetDesktopWindow(), src=GetWindowDC(dw);
  using(Bitmap bmp=new Bitmap(b.Width,b.Height))
  {
   using(Graphics g=Graphics.FromImage(bmp)){
    IntPtr dst=g.GetHdc();
    BitBlt(dst,0,0,b.Width,b.Height,src,b.X,b.Y,0x00CC0020|0x40000000);
    g.ReleaseHdc(dst);
   }
   bmp.Save(a[0],System.Drawing.Imaging.ImageFormat.Png);
  }
  ReleaseDC(dw,src);
  Console.WriteLine("saved "+a[0]);
 }
}
