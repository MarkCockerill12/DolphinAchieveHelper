using System;
using System.Drawing;
using System.Windows.Forms;
using DolphinAchiever;

static class ToastTest
{
    static Image Badge(Color c)
    {
        var b = new Bitmap(64, 64);
        using (Graphics g = Graphics.FromImage(b))
        {
            g.Clear(c);
            g.FillEllipse(Brushes.Gold, 12, 12, 40, 40);
        }
        return b;
    }

    [STAThread]
    static void Main(string[] args)
    {
        Application.EnableVisualStyles();
        var mode = args.Length > 0 && args[0] == "always" ? DescriptionMode.Always : DescriptionMode.Hover;
        var w = new ToastWindow { CornerIndex = 3, Descriptions = mode };

        var n = new Notice { Header = "Good Egg Galaxy", HeaderNote = "6 left", Seconds = 600 };
        n.Rows.Add(new NoticeRow { Title = "Sunny Side Up", Corner = "10", Icon = Badge(Color.SteelBlue),
            Body = "Complete \"Purple Coin Omelet\" in Good Egg Galaxy in under 1 minute and 30 seconds." });
        n.Rows.Add(new NoticeRow { Title = "Kalimari Koins", Corner = "10", Missable = true, Icon = Badge(Color.IndianRed),
            Body = "Collect 15 coins during the battle with King Kaliente in Good Egg Galaxy." });
        n.Rows.Add(new NoticeRow { Title = "Up, Up, and Away", Corner = "5", Icon = Badge(Color.SeaGreen),
            Body = "Obtain the star in \"Luigi on the Roof\" without entering the orange pipe." });
        n.Rows.Add(new NoticeRow { Title = "Prehistoric Conservation of Angular Momentum", Corner = "5", Icon = Badge(Color.DarkSlateBlue),
            Body = "Defeat Dino Piranha in Good Egg Galaxy without spinning more than four times." });
        w.Push(n);

        var t = new Timer { Interval = 2000 };
        t.Tick += delegate
        {
            System.IO.File.WriteAllText(
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), "toast_diag.txt"),
                "visible=" + w.Visible + "\r\nbounds=" + w.Bounds + "\r\nerror=" + (w.LastError ?? "(none)"));
        };
        t.Start();
        Application.Run();
    }
}
