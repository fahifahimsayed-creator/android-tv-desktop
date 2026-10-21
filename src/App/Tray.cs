using System;
using System.Drawing;
using System.Windows.Forms;
using AndroidTVDesktop.Core;

namespace AndroidTVDesktop
{
    /// <summary>
    /// System-tray icon. With "keep running in the tray" on, closing the window only hides it, so the phone remote,
    /// TV monitoring and keyboard shortcuts keep working until the user chooses Exit here (or on the System page).
    /// </summary>
    sealed class Tray : IDisposable
    {
        readonly NotifyIcon icon;
        readonly AppState st;
        readonly Func<MainWindow> win;
        readonly ToolStripMenuItem remoteInfo, launchTv;
        bool tipShown;

        public Tray(AppState state, Func<MainWindow> window)
        {
            st = state; win = window;
            Icon ico = SystemIcons.Application;
            try
            {
                using (var s = typeof(Tray).Assembly.GetManifestResourceStream("AndroidTVDesktop.ico"))
                    if (s != null) ico = new Icon(s, SystemInformation.SmallIconSize);
            }
            catch { }

            var menu = new ContextMenuStrip();
            var open = new ToolStripMenuItem("Open Android TV Desktop", null, (o, e) => win()?.ShowFromTray()) { Font = new Font(SystemFonts.MenuFont, FontStyle.Bold) };
            launchTv = new ToolStripMenuItem("Launch TV", null, async (o, e) => await st.Engine.LaunchTvAsync("tray menu"));
            var netflix = new ToolStripMenuItem("Netflix (PC)", null, async (o, e) => await st.OpenNetflixAsync());
            remoteInfo = new ToolStripMenuItem("Phone remote: off") { Enabled = false };
            var exit = new ToolStripMenuItem("Exit (stops the phone remote)", null, (o, e) => ExitApp());
            menu.Items.AddRange(new ToolStripItem[] { open, launchTv, netflix, new ToolStripSeparator(), remoteInfo, new ToolStripSeparator(), exit });
            menu.Opening += (o, e) => Refresh();

            icon = new NotifyIcon { Icon = ico, Text = "Android TV Desktop", ContextMenuStrip = menu, Visible = true };
            icon.MouseClick += (o, e) => { if (e.Button == MouseButtons.Left) win()?.ShowFromTray(); };
            icon.BalloonTipClicked += (o, e) => win()?.ShowFromTray();
            Refresh();
        }

        void Refresh()
        {
            var r = st.Remote;
            remoteInfo.Text = r != null && r.Running
                ? $"Phone remote: http://{(PhoneRemoteServer.LanAddresses() is var a && a.Count > 0 ? a[0] : "this-pc")}:{st.Settings.PhoneRemotePort}  PIN {st.Settings.PhoneRemotePin}"
                : "Phone remote: off (Controls page)";
            launchTv.Text = st.Engine.Machine.IsTvModeActive ? "TV is on" : "Launch TV";
            launchTv.Enabled = !st.Engine.Machine.IsTvModeActive;
            string tip = "Android TV Desktop" + (r != null && r.Running ? " - phone remote on" : "");
            icon.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip;
        }

        /// <summary>Shown the first time the window is closed to the tray in this session.</summary>
        public void ShowHiddenTip()
        {
            Refresh();
            if (tipShown) return;
            tipShown = true;
            icon.ShowBalloonTip(5000, "Android TV Desktop is still running",
                "The phone remote and TV keep working. Click this icon to open the app; right-click > Exit to quit.", ToolTipIcon.Info);
        }

        public void ExitApp()
        {
            Log.Info("Exit chosen (tray / System page): closing the app for real.");
            st.ExitRequested = true;
            var w = win();
            if (w != null) { if (!w.IsVisible) w.Show(); w.Close(); }
            else System.Windows.Application.Current?.Shutdown();
        }

        public void Dispose() { icon.Visible = false; icon.Dispose(); }
    }
}
