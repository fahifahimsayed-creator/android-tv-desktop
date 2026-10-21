using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AndroidTVDesktop.Core;

namespace AndroidTVDesktop
{
    sealed partial class MainWindow
    {
        // ====================================================================== Home
        UIElement BuildHome()
        {
            var e = st.Engine;
            var page = new StackPanel();
            page.Children.Add(Ui.H1("Android TV"));
            page.Children.Add(Ui.Sub("Your Google TV emulator, as a television on your monitor."));

            var dot = Ui.Dot(Theme.SubText);
            var stateText = new TextBlock { FontSize = 20, FontWeight = FontWeights.SemiBold };
            var status = Ui.Card(
                Ui.Horizontal(dot, stateText),
                new Border { Height = 12 },
                Ui.Row("AVD", out var avd),
                Ui.Row("TV monitor", out var mon),
                Ui.Row("Emulator", out var emu),
                Ui.Row("Android / ADB", out var adb),
                Ui.Row("Last startup time", out var startup));
            var err = new TextBlock { Foreground = Theme.B(Theme.Bad), Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
            ((StackPanel)status.Child).Children.Add(err);
            page.Children.Add(status);

            Button launch = null, exit = null, toggle = null;
            launch = Ui.Btn("  Launch TV  ", async () => await Busy(launch, () => e.LaunchTvAsync("Launch TV button")), primary: true, tip: "Start (or reuse) Television_4K and go fullscreen on the selected monitor");
            exit = Ui.Btn("Exit TV", async () => await Busy(exit, () => e.ExitTvAsync("Exit TV button")), tip: "Leave TV mode and restore the desktop; the emulator keeps running");
            toggle = Ui.Btn("Fullscreen / window (F11)", async () => await Busy(toggle, () => e.ToggleFullscreenAsync()));
            var settingsBtn = Ui.Btn("Open Android TV settings", async () => MessageText(await e.OpenAndroidSettingsAsync(false)));
            Button netflix = null;
            netflix = Ui.Btn("Netflix (PC)", async () => await Busy(netflix, () => st.OpenNetflixAsync()), tip: "Netflix does not support the emulator. This opens netflix.com fullscreen in Microsoft Edge on the TV monitor (your Edge sign-in is used).");
            page.Children.Add(Ui.Card(Ui.H2("TV"), Ui.Buttons(launch, exit, toggle, settingsBtn, netflix),
                Ui.P($"While the TV is on: {st.Settings.KeyToggleFullscreen} switches fullscreen/window, {st.Settings.KeyExitFullscreen} leaves fullscreen, {st.Settings.KeyEmergencyExit} (left Alt) is the emergency exit that always restores the desktop.", true)));

            page.Children.Add(Ui.Card(Ui.H2("Good to know"),
                Ui.P("The TV interface is the real Android TV system image running in the Android Emulator. App availability and video playback depend on Google Play certification, DRM (Widevine level) and emulator limits; some streaming apps may refuse to play or play only in SD.", true),
                Ui.P("Use the arrow keys, Enter and your mouse to navigate. Text keys type into Android text fields.", true)));

            refreshPage = () =>
            {
                var (t, c) = StateText(e);
                stateText.Text = t; dot.Fill = Theme.B(c);
                avd.Text = $"{e.Avd.Name}" + (e.Avd.Found ? $"  ({e.Avd.LcdWidth}x{e.Avd.LcdHeight}, {e.Avd.Tag})" : "  - NOT FOUND");
                mon.Text = e.Monitor != null ? $"{e.Monitor.Label}  - {e.MonitorReason}" : "No monitor";
                emu.Text = e.Target != IntPtr.Zero ? $"Running (window {Native.Hex(e.Target)}, PID {e.TargetPid})" : "Not running";
                adb.Text = e.AdbReady ? $"Ready ({e.Adb?.Serial}), boot completed" : e.Target != IntPtr.Zero ? "Not checked yet / booting" : "-";
                startup.Text = e.StartupSeconds.HasValue ? $"{e.StartupSeconds:0.0} s" : "-";
                err.Text = e.LastError; err.Visibility = string.IsNullOrEmpty(e.LastError) ? Visibility.Collapsed : Visibility.Visible;
                bool active = e.Machine.IsTvModeActive;
                launch.Content = active ? "  Go fullscreen  " : "  Launch TV  ";
                exit.IsEnabled = active || e.Machine.State == TvState.Faulted;
                toggle.IsEnabled = e.Machine.State == TvState.Fullscreen || e.Machine.State == TvState.Windowed;
            };
            return page;
        }

        static async Task Busy(Button b, Func<Task> work)
        {
            b.IsEnabled = false;
            try { await work(); } catch (Exception ex) { Log.Error("Action failed", ex); MessageBox.Show(ex.Message, AppPaths.AppName); }
            finally { b.IsEnabled = true; }
        }

        void MessageText(string t) => MessageBox.Show(this, t, AppPaths.AppName);

        // ====================================================================== Display
        UIElement BuildDisplay()
        {
            var e = st.Engine; var s = st.Settings;
            var page = new StackPanel();
            page.Children.Add(Ui.H1("Display"));
            page.Children.Add(Ui.Sub("Choose the TV monitor. Fullscreen uses that monitor's real pixel bounds and scaling; your Windows resolution and HDR settings are never changed."));

            var monitors = MonitorService.Enumerate();
            var list = new StackPanel();
            var auto = Ui.Toggle("Automatically use the primary monitor", s.AutoMonitor, v => { st.Update(x => x.AutoMonitor = v); RefreshMonitorList(); });
            void RefreshMonitorList()
            {
                list.Children.Clear();
                foreach (var m in monitors)
                {
                    var rb = new RadioButton { GroupName = "mon", IsEnabled = !st.Settings.AutoMonitor, Margin = new Thickness(0, 6, 0, 6),
                        IsChecked = st.Settings.AutoMonitor ? m.IsPrimary : string.Equals(m.Id, st.Settings.PreferredMonitorId, StringComparison.OrdinalIgnoreCase) };
                    var info = new StackPanel();
                    info.Children.Add(new TextBlock { Text = m.Label, FontWeight = FontWeights.SemiBold });
                    info.Children.Add(new TextBlock { Text = $"{m.GdiName}   position ({m.Bounds.Left},{m.Bounds.Top})   scaling {m.Scale:P0}   HDR: {m.HdrText}" + (m.BitsPerChannel > 0 ? $"   {m.BitsPerChannel}-bit" : ""), Foreground = Theme.B(Theme.SubText), FontSize = 13 });
                    rb.Content = info;
                    var id = m.Id;
                    rb.Checked += (o, a) => { if (!st.Settings.AutoMonitor && st.Settings.PreferredMonitorId != id) st.Update(x => x.PreferredMonitorId = id); };
                    list.Children.Add(rb);
                }
                if (monitors.Count < 2) list.Children.Add(Ui.P("Only one monitor is connected right now. Connect the 2560x1440 display and press Rescan to choose it.", true));
            }
            RefreshMonitorList();
            page.Children.Add(Ui.Card(Ui.H2("TV monitor"), auto, list,
                Ui.Buttons(Ui.Btn("Rescan monitors", () => { monitors = MonitorService.Enumerate(); e.Refresh(); RefreshMonitorList(); }),
                           Ui.Btn("Move TV to selected monitor now", async () => { if (e.IsFullscreen) { await e.ExitFullscreenAsync("move monitor"); await e.EnterFullscreenAsync("move monitor"); } else MessageText("The TV is not fullscreen; it will open on the selected monitor next time."); }))));

            page.Children.Add(Ui.Card(Ui.H2("TV mode"),
                Ui.Toggle("Start in fullscreen", s.StartInFullscreen, v => st.Update(x => x.StartInFullscreen = v)),
                Ui.Toggle("Borderless TV mode (cover the whole monitor, taskbar included)", s.BorderlessTvMode, v => st.Update(x => x.BorderlessTvMode = v)),
                Ui.Toggle("Remember the emulator's normal window position", s.RememberWindowState, v => st.Update(x => x.RememberWindowState = v)),
                BuildFrameToggle()));

            var guestStatus = new TextBlock { Foreground = Theme.B(Theme.SubText), Margin = new Thickness(0, 6, 0, 0) };
            var modes = (GuestResolution[])Enum.GetValues(typeof(GuestResolution));
            string ModeLabel(GuestResolution g)
            {
                switch (g)
                {
                    case GuestResolution.AvdDefault: return $"AVD default ({e.Avd.LcdWidth}x{e.Avd.LcdHeight}) - no change";
                    case GuestResolution.MatchMonitor: return "Match the selected monitor (16:9 fit)";
                    default: return g.ToString().TrimStart('R');
                }
            }
            var resCombo = Ui.Combo(modes, s.GuestDisplayValue, ModeLabel, v => st.Update(x => x.GuestDisplay = v.ToString()));
            var densCombo = Ui.Combo(AppSettings.AllowedDensities, s.GuestDensity, d => d == 0 ? "Automatic (keeps UI size)" : d + " dpi", v => st.Update(x => x.GuestDensity = v));
            page.Children.Add(Ui.Card(Ui.H2("Android (guest) display"),
                Ui.P("Resizing the emulator window never changes Android's own resolution. This setting changes the Android display with 'wm size' / 'wm density' over ADB, which is reversible and leaves the AVD files untouched. It is applied when the TV starts, or with Apply now. 'AVD default' removes only an override this app made.", true),
                new TextBlock { Text = "Resolution", Margin = new Thickness(0, 8, 0, 0) }, resCombo,
                new TextBlock { Text = "Density (UI scale)" }, densCombo,
                guestStatus,
                Ui.Buttons(Ui.Btn("Apply now", async () => guestStatus.Text = await e.ApplyGuestDisplayAsync()),
                           Ui.Btn("Read from Android", async () => guestStatus.Text = await e.ReadGuestDisplayAsync()))));

            var selected = e.Monitor;
            page.Children.Add(Ui.Card(Ui.H2("HDR"),
                Ui.P(selected != null ? $"Selected monitor: {selected.HdrText}." : "No monitor selected."),
                Ui.P("This app only reads the HDR state; it never switches HDR on or off. Windows HDR does not make the emulator HDR-capable: the Android Emulator renders an SDR picture, and HDR video in Android TV apps also needs HDR support in the system image, the GPU pipeline, the app and its DRM. Expect SDR output, which Windows maps onto your HDR desktop.", true)));

            var diag = Ui.Card(Ui.H2("Display diagnostics"), Ui.Row("Fullscreen state", out var fsState2), Ui.Row("Last coverage check", out var cov2), Ui.Row("Emulator window", out var win));
            page.Children.Add(diag);

            refreshPage = () =>
            {
                fsState2.Text = StateText(e).Item1;
                cov2.Text = string.IsNullOrEmpty(e.LastCoverage) ? "-" : e.LastCoverage;
                win.Text = e.Target != IntPtr.Zero ? Native.Rect(e.Target) + (Native.IsTopmost(e.Target) ? " (topmost)" : "") : "-";
                guestStatus.Text = e.GuestDisplayStatus;
            };
            return page;
        }

        // ====================================================================== Controls
        UIElement BuildControls()
        {
            var s = st.Settings;
            var page = new StackPanel();
            page.Children.Add(Ui.H1("Controls"));
            page.Children.Add(Ui.Sub("Keys are only intercepted while the TV (emulator) has focus; other apps are never affected. Click a box and press the new key combination."));

            UIElement KeyRow(string label, string value, Action<string> set, bool allowNone)
            {
                var g = new Grid { Margin = new Thickness(0, 4, 0, 4) };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
                g.ColumnDefinitions.Add(new ColumnDefinition());
                g.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
                var box = new TextBox { Text = value, IsReadOnly = true, Cursor = Cursors.Hand, ToolTip = "Click, then press a key or key combination" };
                box.PreviewKeyDown += (o, a) =>
                {
                    a.Handled = true;
                    var key = a.Key == Key.System ? a.SystemKey : a.Key;
                    if (key == Key.LeftCtrl || key == Key.RightCtrl || key == Key.LeftAlt || key == Key.RightAlt || key == Key.LeftShift || key == Key.RightShift) return;
                    int vk = KeyInterop.VirtualKeyFromKey(key);
                    var chord = new KeyChord { Vk = vk, Ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control), Alt = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt), Shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) };
                    var text = chord.ToString();
                    if (!KeyChord.TryParse(text, out _)) { box.Text = "unsupported key"; return; }
                    set(text);
                    box.Text = text;
                };
                Grid.SetColumn(box, 1); g.Children.Add(box);
                if (allowNone)
                {
                    var none = Ui.Btn("None", () => { set("None"); box.Text = "None"; });
                    none.Margin = new Thickness(10, 0, 0, 0); none.HorizontalAlignment = HorizontalAlignment.Left;
                    Grid.SetColumn(none, 2); g.Children.Add(none);
                }
                return g;
            }
            var keys = Ui.Card(Ui.H2("Keyboard shortcuts"),
                KeyRow("Toggle fullscreen", s.KeyToggleFullscreen, v => st.Update(x => x.KeyToggleFullscreen = v), false),
                KeyRow("Exit fullscreen", s.KeyExitFullscreen, v => st.Update(x => x.KeyExitFullscreen = v), false),
                KeyRow("Emergency exit (any app)", s.KeyEmergencyExit, v => st.Update(x => x.KeyEmergencyExit = v), false),
                KeyRow("Android Back", s.KeyAndroidBack, v => st.Update(x => x.KeyAndroidBack = v), true),
                KeyRow("Android Home", s.KeyAndroidHome, v => st.Update(x => x.KeyAndroidHome = v), true),
                Ui.Buttons(Ui.Btn("Reset to defaults", () =>
                {
                    var d = new AppSettings();
                    st.Update(x => { x.KeyToggleFullscreen = d.KeyToggleFullscreen; x.KeyExitFullscreen = d.KeyExitFullscreen; x.KeyEmergencyExit = d.KeyEmergencyExit; x.KeyAndroidBack = d.KeyAndroidBack; x.KeyAndroidHome = d.KeyAndroidHome; });
                    ShowPage("Controls");
                })),
                Ui.P("Invalid or duplicate combinations are rejected and reset. The emergency exit must include Ctrl or Alt and uses the LEFT Alt key, so AltGr characters still type normally. Back and Home are sent once through ADB (KEYCODE_BACK / KEYCODE_HOME) and never also delivered as a key, so Android does not receive them twice. Mapping Backspace to Back means Backspace cannot delete text in Android text fields; choose another key or None if you type a lot.", true));
            page.Children.Add(keys);

            page.Children.Add(Ui.Card(Ui.H2("Native TV keys (handled by the emulator)"),
                Ui.Mono("Arrow keys   move the focus (remote D-pad)\nEnter        select\nTab          move focus where the app supports it\nSpace        activate / play-pause where supported\nLetters      type into Android text fields\nPage Up/Down scroll lists in many apps")));

            var mouseCombo = Ui.Combo(new[] { MouseRightButton.Normal, MouseRightButton.AndroidBack }, s.MouseRightValue,
                v => v == MouseRightButton.Normal ? "Normal right-click (sent to Android)" : "Android Back", v => st.Update(x => x.MouseRight = v.ToString()));
            page.Children.Add(Ui.Card(Ui.H2("Mouse"),
                Ui.P("Left click, pointer movement and the scroll wheel go straight to the emulator, which turns them into Android touch/pointer and scroll events. Nothing is simulated by screen coordinates.", true),
                new TextBlock { Text = "Right mouse button in fullscreen" }, mouseCombo,
                Ui.Toggle("Keep the pointer on the TV monitor while the TV has focus (released when you leave TV mode or switch apps)", s.ConfinePointerToTv, v => st.Update(x => x.ConfinePointerToTv = v)),
                Ui.Toggle("Move the pointer to the corner when entering fullscreen", s.ParkPointerOnEnter, v => st.Update(x => x.ParkPointerOnEnter = v)),
                Ui.Toggle("Show the emulator side toolbar in TV mode", s.ShowToolbarInTvMode, v => st.Update(x => x.ShowToolbarInTvMode = v),
                          "The toolbar is a separate emulator window docked beside the TV; in fullscreen it usually sits off-screen.")));

            page.Children.Add(BuildPhoneRemoteCard());

            page.Children.Add(Ui.Card(Ui.H2("Emergency exit"),
                Ui.P($"{s.KeyEmergencyExit} works from any app, even if Android stops responding: it restores the emulator window, the taskbar and the pointer and leaves TV mode.", true),
                Ui.Buttons(Ui.Btn("Restore desktop now", () => RestoreDesktop()))));
            return page;
        }

        UIElement BuildPhoneRemoteCard()
        {
            var s = st.Settings;
            var urls = new TextBlock { FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 2) };
            var pin = new TextBlock { FontSize = 26, FontWeight = FontWeights.Bold, Foreground = Theme.B(Theme.Accent) };
            var status = new TextBlock { Foreground = Theme.B(Theme.SubText), Margin = new Thickness(0, 4, 0, 4) };
            var fw = new TextBlock { Foreground = Theme.B(Theme.SubText) };
            var activity = new TextBlock { Foreground = Theme.B(Theme.SubText), FontSize = 12.5 };
            void Fill()
            {
                var r = st.Remote;
                var addrs = PhoneRemoteServer.LanAddresses();
                urls.Text = r != null && r.Running ? string.Join("\n", addrs.Select(a => $"http://{a}:{st.Settings.PhoneRemotePort}")) : "Off";
                pin.Text = st.Settings.PhoneRemoteEnabled ? "PIN  " + st.Settings.PhoneRemotePin : "";
                status.Text = !st.Settings.PhoneRemoteEnabled ? "The phone remote is off." :
                              r != null && r.Running ? "Open the address above in your phone's browser (same Wi-Fi), enter the PIN, and keep the page as a bookmark." :
                              "Could not start: " + (r?.LastError ?? "unknown error");
                fw.Text = PhoneRemoteServer.FirewallRuleExists() ? "Windows Firewall: allowed for devices on your local network." :
                          "Windows Firewall: not allowed yet. Phones cannot connect until you allow it (needs administrator approval).";
            }
            Action<string> onActivity = a => Dispatcher.BeginInvoke(new Action(() => activity.Text = "Last: " + a + "  (" + DateTime.Now.ToString("HH:mm:ss") + ")"));
            var card = Ui.Card(Ui.H2("Phone remote"),
                Ui.P("Control the TV from your phone's web browser: D-pad, OK, Back, Home, volume, play/pause and typing. Works on the same Wi-Fi; no app needed. (The Google TV phone app cannot connect to an emulator: the emulator image has no Android TV Remote Service and sits on a private network.)", true),
                Ui.Toggle("Enable phone remote", s.PhoneRemoteEnabled, v => { st.Update(x => x.PhoneRemoteEnabled = v); Fill(); }),
                urls, pin, status, fw, activity,
                Ui.Buttons(
                    Ui.Btn("New PIN", () => { st.Update(x => x.PhoneRemotePin = PhoneRemoteServer.NewPin()); Fill(); }, tip: "Phones using the old PIN must enter the new one"),
                    Ui.Btn("Allow through Windows Firewall...", () =>
                    {
                        if (!Confirm($"Add a Windows Firewall rule that lets devices on your local network reach port {st.Settings.PhoneRemotePort}?\n\nWindows will ask for administrator approval. You can remove the rule here at any time.")) return;
                        PhoneRemoteServer.SetFirewallRule(true, st.Settings.PhoneRemotePort); Fill();
                    }),
                    Ui.Btn("Remove firewall rule", () => { PhoneRemoteServer.SetFirewallRule(false, 0); Fill(); })));
            if (st.Remote != null) st.Remote.Activity += onActivity;
            card.Unloaded += (o, e) => { if (st.Remote != null) st.Remote.Activity -= onActivity; };
            Fill();
            return card;
        }

        /// <summary>"Show the TV frame (bezel)" + a restart button when the running emulator does not match the setting yet.</summary>
        UIElement BuildFrameToggle()
        {
            var e = st.Engine;
            var panel = new StackPanel();
            var note = new TextBlock { Foreground = Theme.B(Theme.Warn), Margin = new Thickness(52, 0, 0, 4) };
            Button restart = null;
            restart = Ui.Btn("Restart emulator to apply", async () =>
            {
                if (!Confirm("Restart the emulator now to apply the TV frame setting? Android shuts down normally (anything playing stops) and starts again.")) return;
                await Busy(restart, () => e.RestartEmulatorAsync());
                Fill();
            });
            restart.Margin = new Thickness(52, 0, 0, 10); restart.HorizontalAlignment = HorizontalAlignment.Left;
            void Fill()
            {
                bool pending = !e.FrameSettingApplied();
                note.Text = !e.Avd.ShowDeviceFrame && !st.Settings.LaunchFrameless
                    ? "This AVD has no device frame (Android Studio > Device Manager > edit the device > 'Enable device frame'), so nothing changes."
                    : pending ? "The emulator is running the other way - restart it to apply." : "";
                note.Visibility = note.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
                restart.Visibility = pending ? Visibility.Visible : Visibility.Collapsed;
            }
            panel.Children.Add(Ui.Toggle("Show the TV frame (bezel) around the picture", !st.Settings.LaunchFrameless, v => { st.Update(x => x.LaunchFrameless = !v); Fill(); },
                "On: the emulator starts with your AVD's own TV frame; fullscreen fits the whole framed TV on the screen with black around it. " +
                "Off: no frame, and fullscreen fills the entire screen edge to edge. Takes effect when the emulator (re)starts; AVD files are never modified."));
            panel.Children.Add(note);
            panel.Children.Add(restart);
            Fill();
            return panel;
        }

        void RestoreDesktop()
        {
            st.Engine.EmergencyRestore("Restore desktop button");
            _ = st.Engine.ExitTvAsync("Restore desktop button");
            var done = DesktopRestore.Run(st.Settings.AvdName);
            MessageText("Desktop restored:\n\n" + string.Join("\n", done));
        }

        // ====================================================================== System
        UIElement BuildSystem()
        {
            var e = st.Engine; var s = st.Settings;
            var page = new StackPanel();
            page.Children.Add(Ui.H1("System"));
            page.Children.Add(Ui.Sub("Emulator, ADB, network, sign-in start, logs and recovery."));

            var avds = AvdInfo.ListAvds();
            if (!avds.Contains(s.AvdName)) avds.Insert(0, s.AvdName);
            var avdCombo = Ui.Combo(avds.ToArray(), s.AvdName, n => n, v => { st.Update(x => x.AvdName = v); e.Refresh(); ShowPage("System"); });
            var a = e.Avd;
            page.Children.Add(Ui.Card(Ui.H2("Android Virtual Device"), avdCombo,
                Ui.Row("Config", out _, a.Found ? a.ConfigPath : "not found"),
                Ui.Row("Panel", out _, a.Found ? $"{a.LcdWidth} x {a.LcdHeight} @ {a.LcdDensity} dpi" : "-"),
                Ui.Row("System image", out _, a.Found ? $"{a.SystemImage} ({a.Tag}, {a.Abi})" : "-"),
                Ui.Row("Guest RAM", out _, a.Found ? $"{a.RamMb} MB" : "-"),
                Ui.Row("Device frame in AVD", out _, a.Found ? (a.ShowDeviceFrame ? "yes (removed for launches from this app)" : "no") : "-"),
                Ui.P("This app reads the AVD but never edits, wipes or resets it.", true)));

            var sdkBox = new TextBox { Text = s.SdkPath, ToolTip = "Leave empty to auto-detect" };
            page.Children.Add(Ui.Card(Ui.H2("Android SDK"),
                new TextBlock { Text = "SDK folder (empty = auto-detect)" }, sdkBox,
                Ui.Buttons(Ui.Btn("Save path", () => { st.Update(x => x.SdkPath = sdkBox.Text.Trim()); e.Refresh(); ShowPage("System"); })),
                Ui.Row("emulator.exe", out _, (e.Sdk.EmulatorFound ? "OK  " : "MISSING  ") + e.Sdk.EmulatorExe),
                Ui.Row("adb.exe", out _, (e.Sdk.AdbFound ? "OK  " : "MISSING  ") + e.Sdk.AdbExe)));

            var procText = Ui.Mono("");
            page.Children.Add(Ui.Card(Ui.H2("Emulator"),
                Ui.Buttons(
                    Ui.Btn("Close emulator", async () => { if (Confirm("Close the emulator? Android shuts down normally and saves its Quick Boot state.")) await e.CloseEmulatorAsync(); }),
                    Ui.Btn("Restart emulator", async () => { if (Confirm("Restart the emulator now?")) await e.RestartEmulatorAsync(); }),
                    Ui.Btn("Refresh", () => ShowPage("System"))),
                procText));

            var netOut = Ui.Mono("");
            page.Children.Add(Ui.Card(Ui.H2("Network and ADB"),
                Ui.P("If Google TV shows a network error: check that Windows itself is online, then that Android can reach the internet and resolve names, and that Android's clock is correct (a wrong clock breaks Google sign-in).", true),
                Ui.Buttons(
                    Ui.Btn("Check network", async () =>
                    {
                        netOut.Text = "Checking...";
                        var r = await Diagnostics.CheckNetworkAsync(e);
                        netOut.Text = string.Join("\n", r);
                        Log.Info("Network check: " + string.Join(" | ", r));
                    }),
                    Ui.Btn("Open Android network settings", async () => MessageText(await e.OpenAndroidSettingsAsync(true)))),
                netOut));

            var signIn = Ui.Combo(new[] { Shortcuts.SignIn.Nothing, Shortcuts.SignIn.Background, Shortcuts.SignIn.Tv }, Shortcuts.CurrentSignIn(),
                m => m == Shortcuts.SignIn.Nothing ? "Do nothing" : m == Shortcuts.SignIn.Background ? "Start in the background (phone remote ready, TV off)" : "Start the TV (fullscreen)",
                m =>
                {
                    try { Shortcuts.SetSignIn(m, st.ExePath); st.Update(x => x.StartOnSignIn = m == Shortcuts.SignIn.Tv); }
                    catch (Exception ex) { Log.Error("Startup shortcut", ex); MessageText("Could not change the sign-in shortcut: " + ex.Message); }
                });
            page.Children.Add(Ui.Card(Ui.H2("Start-up and background"),
                Ui.Toggle("Keep running in the tray when I close the window (phone remote keeps working)", s.KeepRunningInTray, v => st.Update(x => x.KeepRunningInTray = v),
                          "The window's X hides the app to the tray icon next to the clock. Use the tray icon's Exit (or Quit below) to stop it."),
                new TextBlock { Text = "When I sign in to Windows", Margin = new Thickness(0, 10, 0, 0) }, signIn,
                Ui.Toggle("Launch TV when this app opens", s.LaunchTvOnAppStart, v => st.Update(x => x.LaunchTvOnAppStart = v)),
                Ui.P("The sign-in option adds or removes a shortcut in your Startup folder (shell:startup): no registry changes, no admin rights. The app runs on its own; Claude or any other program does not need to be open.", true),
                Ui.Buttons(Ui.Btn("Quit Android TV Desktop", () => { if (Confirm("Quit the app? The phone remote stops until you open the app again.")) st.Tray?.ExitApp(); }))));

            page.Children.Add(Ui.Card(Ui.H2("Logs and recovery"),
                Ui.Row("Log folder", out _, Log.Directory),
                Ui.Row("Settings file", out _, st.Store.FilePath),
                Ui.Buttons(
                    Ui.Btn("Open log folder", () => Process.Start("explorer.exe", "\"" + Log.Directory + "\"")),
                    Ui.Btn("Export diagnostics...", () => ExportDiagnostics()),
                    Ui.Btn("Restore desktop", () => RestoreDesktop(), tip: "Un-fullscreen the emulator, show the taskbar and toolbar, release the pointer")),
                Ui.P("If this app cannot start, run Restore-Desktop.bat from the install folder (or the Start Menu), or press Ctrl+Shift+Esc and restart Windows Explorer.", true)));

            page.Children.Add(Ui.Card(Ui.H2("Uninstall"),
                Ui.P("Run 'Uninstall Android TV Desktop' from the Start Menu (or uninstall.bat in the install folder). It removes the app files, shortcuts and the sign-in shortcut. Your settings and logs in %APPDATA%\\AndroidTVDesktop and %LOCALAPPDATA%\\AndroidTVDesktop are kept unless you choose to delete them. The Android emulator and AVD are never touched.", true)));

            refreshPage = () =>
            {
                var procs = EmulatorProcesses.ForAvd(st.Settings.AvdName);
                procText.Text = procs.Count == 0 ? "No emulator process for this AVD." :
                    string.Join("\n", procs.Select(p => $"{p.Name} PID {p.Pid}: {p.WorkingSetMb} MB working set, {p.PrivateMb} MB private, responding={p.Responding}"));
            };
            return page;
        }

        bool Confirm(string q) => MessageBox.Show(this, q, AppPaths.AppName, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

        void ExportDiagnostics()
        {
            try
            {
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Android TV Desktop diagnostics");
                var zip = Diagnostics.Export(Diagnostics.BuildReport(st.Engine, st.Settings), st.Settings.ToJson(), dir);
                Process.Start("explorer.exe", "/select,\"" + zip + "\"");
            }
            catch (Exception ex) { Log.Error("Export diagnostics", ex); MessageText("Export failed: " + ex.Message); }
        }

        // ====================================================================== Diagnostics
        UIElement BuildDiagnostics()
        {
            var page = new StackPanel();
            page.Children.Add(Ui.H1("Diagnostics"));
            page.Children.Add(Ui.Sub("What the app sees right now, plus the live log. Nothing is uploaded."));
            var report = Ui.Mono("Collecting...");
            var logBox = new TextBox { IsReadOnly = true, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12, Height = 300, TextWrapping = TextWrapping.NoWrap,
                                       VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
            void Fill() { report.Text = Diagnostics.BuildReport(st.Engine, st.Settings); }
            page.Children.Add(Ui.Card(Ui.H2("Report"), report,
                Ui.Buttons(Ui.Btn("Refresh", Fill), Ui.Btn("Copy", () => Clipboard.SetText(report.Text)), Ui.Btn("Export diagnostics...", ExportDiagnostics))));
            page.Children.Add(Ui.Card(Ui.H2("Live log"), logBox));
            logBox.Text = string.Join(Environment.NewLine, Log.Recent(300));
            logBox.ScrollToEnd();
            Action<string> onLine = line => Dispatcher.BeginInvoke(new Action(() => { if (currentPage == "Diagnostics") { logBox.AppendText(Environment.NewLine + line); logBox.ScrollToEnd(); } }));
            Log.LineWritten += onLine;
            page.Unloaded += (o, a) => Log.LineWritten -= onLine;
            Dispatcher.BeginInvoke(new Action(Fill), System.Windows.Threading.DispatcherPriority.Background);
            return page;
        }

        // ====================================================================== About
        UIElement BuildAbout()
        {
            var page = new StackPanel();
            page.Children.Add(Ui.H1(AppPaths.AppName));
            page.Children.Add(Ui.Sub("Version " + AppPaths.Version));
            page.Children.Add(Ui.Card(Ui.H2("What it is"),
                Ui.P("A launcher and controller that turns your existing Android Studio AVD (Television_4K, Google TV) into a television on the monitor you choose. It starts or reuses the real Android Emulator, waits for Android, and shows it borderless and fullscreen, measured and verified on screen, with keyboard and mouse mappings and guaranteed desktop restoration."),
                Ui.P("Built with C# and WPF on .NET Framework 4.8.1 (part of Windows 11): no extra runtime, low memory use, direct Win32 window management.", true)));
            page.Children.Add(Ui.Card(Ui.H2("Limitations"),
                Ui.P("- Google TV runs in the Android Emulator; app availability depends on Google Play certification and emulator compatibility.", true),
                Ui.P("- Protected video (DRM/Widevine) may be limited to SD or refused by some streaming apps.", true),
                Ui.P("- HDR output is not supported by the emulator; HDR monitors show an SDR picture.", true),
                Ui.P("- The TV picture is slightly overscanned (about 1% per edge) so no desktop pixels are visible, like a real TV.", true)));
            page.Children.Add(Ui.Card(Ui.H2("Privacy and safety"),
                Ui.P("No telemetry, no network calls except the checks you start (ping, ADB). No administrator rights, no registry changes, no changes to the Windows shell or security settings. Your AVD, apps and Google account data are never modified.", true)));
            return page;
        }

        void ShowPage(string page) { foreach (var rb in navButtons) if ((string)rb.Tag == page) { if (rb.IsChecked == true) Show(page); else rb.IsChecked = true; } }
    }
}
