using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AndroidTVDesktop.Core;

namespace AndroidTVDesktop
{
    sealed partial class MainWindow : Window
    {
        readonly AppState st;
        readonly ContentControl content = new ContentControl();
        readonly Dictionary<string, Func<UIElement>> pages = new Dictionary<string, Func<UIElement>>();
        readonly List<RadioButton> navButtons = new List<RadioButton>();
        TextBlock footerState, footerMonitor;
        System.Windows.Shapes.Ellipse footerDot;
        Action refreshPage = () => { };
        string currentPage = "Home";

        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int value, int size);

        public MainWindow(AppState state)
        {
            st = state;
            Title = AppPaths.AppName;
            Width = 1100; Height = 760; MinWidth = 900; MinHeight = 600;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = Theme.B(Theme.Bg);
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
            try
            {
                using (var s = typeof(MainWindow).Assembly.GetManifestResourceStream("AndroidTVDesktop.ico"))
                    if (s != null) Icon = BitmapFrame.Create(s, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            }
            catch { }
            SourceInitialized += (s, e) =>
            {
                // Dark title bar (Windows 11 immersive dark mode) and rounded corners.
                var h = new WindowInteropHelper(this).Handle;
                int on = 1; DwmSetWindowAttribute(h, 20, ref on, 4);
                int round = 2; DwmSetWindowAttribute(h, 33, ref round, 4);
            };

            pages["Home"] = BuildHome;
            pages["Display"] = BuildDisplay;
            pages["Controls"] = BuildControls;
            pages["System"] = BuildSystem;
            pages["Diagnostics"] = BuildDiagnostics;
            pages["About"] = BuildAbout;

            var root = new Grid();
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(232) });
            root.ColumnDefinitions.Add(new ColumnDefinition());

            // ---- navigation rail
            var nav = new DockPanel { Background = Theme.B(Theme.Surface) };
            var brand = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(22, 22, 12, 26) };
            if (Icon != null) brand.Children.Add(new Image { Source = Icon, Width = 30, Height = 30, Margin = new Thickness(0, 0, 10, 0) });
            brand.Children.Add(new TextBlock { Text = "Android TV\nDesktop", FontSize = 15, FontWeight = FontWeights.SemiBold, LineHeight = 18 });
            DockPanel.SetDock(brand, Dock.Top);
            nav.Children.Add(brand);

            var footer = new StackPanel { Margin = new Thickness(22, 10, 16, 18) };
            footerDot = Ui.Dot(Theme.SubText);
            footerState = new TextBlock { Text = "TV off", FontSize = 13 };
            footer.Children.Add(Ui.Horizontal(footerDot, footerState));
            footerMonitor = new TextBlock { FontSize = 12, Foreground = Theme.B(Theme.SubText), Margin = new Thickness(18, 4, 0, 0) };
            footer.Children.Add(footerMonitor);
            footer.Children.Add(new TextBlock { Text = "v" + AppPaths.Version, FontSize = 12, Foreground = Theme.B(Theme.SubText), Margin = new Thickness(18, 10, 0, 0) });
            DockPanel.SetDock(footer, Dock.Bottom);
            nav.Children.Add(footer);

            var navList = new StackPanel();
            foreach (var (key, glyph) in new[] { ("Home", ""), ("Display", ""), ("Controls", ""), ("System", ""), ("Diagnostics", ""), ("About", "") })
            {
                var rb = new RadioButton
                {
                    GroupName = "nav", Tag = key, Style = (Style)Application.Current.Resources["Nav"],
                    Content = Ui.Horizontal(new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 16, Margin = new Thickness(0, 0, 14, 0), Foreground = Theme.B(Theme.SubText) },
                                            new TextBlock { Text = key, Foreground = null })
                };
                ((TextBlock)((StackPanel)rb.Content).Children[1]).SetBinding(TextBlock.ForegroundProperty, new System.Windows.Data.Binding("Foreground") { Source = rb });
                rb.Checked += (s, e) => Show((string)rb.Tag);
                navButtons.Add(rb);
                navList.Children.Add(rb);
            }
            nav.Children.Add(navList);
            root.Children.Add(nav);

            var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(36, 28, 36, 20), Content = content };
            Grid.SetColumn(scroll, 1);
            root.Children.Add(scroll);
            Content = root;

            st.Engine.StatusChanged += () => Dispatcher.BeginInvoke(new Action(UpdateStatus));
            Loaded += (s, e) => { navButtons[0].IsChecked = true; UpdateStatus(); };
            Closing += OnClosing;
        }

        void Show(string page)
        {
            currentPage = page;
            refreshPage = () => { };
            content.Content = pages[page]();
            refreshPage();
        }

        void UpdateStatus()
        {
            var e = st.Engine;
            var (text, color) = StateText(e);
            footerState.Text = text; footerDot.Fill = Theme.B(color);
            footerMonitor.Text = e.Monitor != null ? $"{e.Monitor.Bounds.Width}x{e.Monitor.Bounds.Height}" + (e.Monitor.IsPrimary ? " primary" : "") : "";
            try { refreshPage(); } catch (Exception ex) { Log.Error("Page refresh", ex); }
        }

        internal static (string, Color) StateText(TvEngine e)
        {
            switch (e.Machine.State)
            {
                case TvState.Starting: return ("Starting emulator...", Theme.Warn);
                case TvState.WaitingForBoot: return ("Waiting for Android...", Theme.Warn);
                case TvState.Entering: return ("Entering fullscreen...", Theme.Warn);
                case TvState.Fullscreen: return ("TV mode - fullscreen", Theme.Good);
                case TvState.Windowed: return ("TV mode - window", Theme.Accent);
                case TvState.Exiting: return ("Exiting...", Theme.Warn);
                case TvState.Faulted: return ("Error - see Home", Theme.Bad);
                default: return (e.Target != IntPtr.Zero ? "TV off (emulator running)" : "TV off", Theme.SubText);
            }
        }

        public void ShowFromTray()
        {
            Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
        }

        bool closingConfirmed;
        async void OnClosing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (!st.ExitRequested && st.Settings.KeepRunningInTray)
            {
                // Window X = hide to the tray; the phone remote, TV and shortcuts keep running.
                e.Cancel = true;
                SaveRemembered();
                Hide();
                Log.Info("Window closed to the tray; app keeps running (phone remote " + (st.Remote?.Running == true ? "on" : "off") + ").");
                st.Tray?.ShowHiddenTip();
                return;
            }
            if (closingConfirmed || !st.Engine.Machine.IsTvModeActive) { SaveRemembered(); return; }
            e.Cancel = true;
            Log.Info("Main window closing while TV mode is active: leaving TV mode first (emulator keeps running).");
            await st.Engine.ExitTvAsync("application closing");
            SaveRemembered();
            closingConfirmed = true;
            // ExitTvAsync may finish synchronously (TV not fullscreen), i.e. while WPF is still inside this Closing
            // event; calling Close() there throws. Queue the real close until after the event has returned.
            Dispatcher.BeginInvoke(new Action(Close), System.Windows.Threading.DispatcherPriority.Background);
        }

        void SaveRemembered()
        {
            if (st.Settings.RememberWindowState && st.Engine.RememberedNormalWindow != st.Settings.LastNormalWindow)
                st.Update(s => s.LastNormalWindow = st.Engine.RememberedNormalWindow);
        }
    }
}
