using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;

[assembly: System.Reflection.AssemblyTitle("Manager Agent Setup")]
[assembly: System.Reflection.AssemblyProduct("Manager Agent")]
[assembly: System.Reflection.AssemblyDescription("Installs the Manager Agent mod for Football Manager")]
[assembly: System.Reflection.AssemblyVersion(ManagerAgentSetup.SetupInfo.Version + ".0")]
[assembly: System.Reflection.AssemblyFileVersion(ManagerAgentSetup.SetupInfo.Version + ".0")]

namespace ManagerAgentSetup
{
    internal static class Program
    {
        /// <summary>
        /// Arguments (all optional):
        ///   --game "folder"   preselect a game folder
        ///   --auto install|repair|uninstall   continue an action (used after asking for admin rights)
        ///   --demo ready|working|repair|done|error [--hold seconds]   show a sample state (for screenshots)
        ///   --snapshot file.png --demo …   render the window to a PNG (Windows only)
        /// </summary>
        [STAThread]
        private static int Main(string[] args)
        {
            string game = Arg(args, "--game"), auto = Arg(args, "--auto");
            string snapshot = Arg(args, "--snapshot"), demo = Arg(args, "--demo"), hold = Arg(args, "--hold");

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.ThreadException += (s, e) => Fatal(e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (s, e) => Fatal(e.ExceptionObject as Exception);

            bool demoMode = snapshot != null || demo != null;
            var form = new MainForm(demoMode ? null : game, demoMode ? null : auto);
            if (demoMode && snapshot == null)
            {
                // Show a demo state for a few seconds (for screenshots taken from outside).
                form.Demo(demo);
                int seconds;
                if (!int.TryParse(hold, out seconds)) seconds = 8;
                var t = new Timer { Interval = seconds * 1000 };
                t.Tick += (s, e) => { t.Stop(); form.Close(); };
                form.Shown += (s, e) => t.Start();
            }
            else if (snapshot != null)
            {
                form.Demo(demo ?? "ready");
                form.Shown += (s, e) =>
                {
                    Application.DoEvents();
                    using (var bmp = new Bitmap(form.Width, form.Height))
                    {
                        form.DrawToBitmap(bmp, new Rectangle(0, 0, form.Width, form.Height));
                        bmp.Save(snapshot, ImageFormat.Png);
                    }
                    form.Close();
                };
            }
            Application.Run(form);
            return 0;
        }

        private static string Arg(string[] args, string name)
        {
            for (int i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
            return null;
        }

        private static void Fatal(Exception ex)
        {
            try { Log.Write("Unexpected error: " + ex); } catch { }
            MessageBox.Show("Something unexpected went wrong:\n\n" + (ex != null ? ex.Message : "unknown error") +
                            "\n\nA log was saved to:\n" + Log.FilePath, "Manager Agent Setup", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
