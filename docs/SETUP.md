# Setup guide: from zero to Google TV on your PC

This guide starts from a Windows PC with nothing installed and ends with Google TV running fullscreen. It takes
**about 30–60 minutes**, mostly waiting for downloads (about 5–8 GB). You only do it once.

**You need:**
- Windows 10 (1809 or newer) or Windows 11, **64-bit**.
- **8 GB RAM or more** (16 GB is smoother).
- **About 20 GB free disk space**.
- A CPU with virtualization (any Intel Core / AMD Ryzen from the last ~10 years).
- A Google account (for the Play Store on the TV).

> **Shortcut:** after any step, double-click **`Check-Setup.bat`** (in the downloaded zip, or the repo root).
> It's read-only and lists what is done ✅ and what is still to do, in order.

---

## Step 1: Install Android Studio (includes the Android Emulator)

Android Studio is Google's free official tool. It brings the **Android Emulator**, which runs the real Google TV.

**Option A, one command** (Windows 10/11 with winget). Open *Windows Terminal* or *PowerShell* and run:

```powershell
winget install -e --id Google.AndroidStudio
```

**Option B, website:** download the installer from **https://developer.android.com/studio** and run it with the
default options. Keep "Android Virtual Device" ticked.

Then **open Android Studio once**:
1. The **Setup Wizard** appears. Choose **Standard** → **Next**.
2. Accept the licenses (click each item on the left → *Accept*) → **Finish**.
3. Wait while it downloads the **Android SDK, Emulator and Platform-Tools**, then click **Finish**.

> You don't need to create a project. When you see the *Welcome to Android Studio* window, you're ready.

---

## Step 2: Turn on hardware acceleration (once)

The emulator needs the CPU's virtualization to run at a usable speed.

1. **Windows Hypervisor Platform:** press **Start**, type **"Turn Windows features on or off"** and open it.
   Tick **Windows Hypervisor Platform**, click **OK** and **restart** the PC.
   (Or, in an *Administrator* PowerShell:
   `Enable-WindowsOptionalFeature -Online -FeatureName HypervisorPlatform`, then restart.)
2. **Virtualization in BIOS:** if `Check-Setup.bat` says *CPU virtualization is OFF*, restart into your BIOS/UEFI
   (often **F2**, **F10**, **Del** or **Esc** at boot). Enable **Intel Virtualization Technology (VT-x)** or
   **SVM Mode (AMD-V)**, then save and exit.

---

## Step 3: Create the Google TV 4K emulator

In Android Studio:

1. Open **Device Manager**: the phone-and-tablet icon on the right edge, or **More Actions → Virtual Device Manager**
   on the Welcome screen.
2. Click **+** (*Create Virtual Device*).
3. **Category: TV**, then pick a device:

   | Device | Use it when |
   |---|---|
   | **Television (4K)** — 3840 × 2160 | Sharpest picture on any monitor. Recommended with 8 GB+ RAM. |
   | Television (1080p) — 1920 × 1080 | Lighter; good for slower PCs or 8 GB RAM with many apps open. |

4. **Next.** In *System Image* pick **Google TV**, **API 36 (Android 16), x86_64**. Click the **download arrow**
   next to it, accept the license and wait (about 1.5 GB). If you don't see it, open the **x86 Images** /
   **Other Images** tab.

   > Pick an image labelled **Google TV**, not "Android TV". Google TV has the Play Store and the Google TV home
   > screen. Package id: `system-images;android-36;google-tv;x86_64`.

5. **Next.** Name it, e.g. **Television_4K**. Any name works; the app finds it automatically.
6. Optional, under **Show Advanced Settings**: RAM **2048 MB** (use 4096 MB on a 16 GB PC). Leave the rest as is.
7. **Finish.**

---

## Step 4: First start, and sign in to Google TV

1. In Device Manager, click **▶** next to your new TV. The first boot takes 1–3 minutes.
2. Follow the Google TV setup on screen. Use your keyboard: **arrows + Enter**, and **Esc = Back**.
   - Choose **Set up Google TV**.
   - Sign in with your **Google account**. You need it for the Play Store and your apps. (Some screens show a QR
     code for your phone. Choose *Use your computer/remote* or *Sign in on this TV* instead.)
   - Accept the terms, pick the apps you want and finish.
3. Close the emulator window (**X**). It saves its state, so the next start is fast.

> If the **Play Store** later says *"Authentication is required"*, the emulator's clock is wrong (this happens
> after a saved snapshot). Fix it in Device Manager → **⋮** next to the TV → **Cold Boot Now**.

---

## Step 5: Install Android TV Desktop

1. Download **`AndroidTVDesktop-x.y.z.zip`** from the
   [latest release](https://github.com/fahifahimsayed-creator/android-tv-desktop/releases/latest).
2. Right-click it → **Extract All…**
3. Double-click **`install.bat`**. It installs for your user only, with no admin rights, and creates Desktop and
   Start Menu shortcuts.

   > **Windows SmartScreen** may say *"Windows protected your PC"* because the app isn't code-signed. Click
   > **More info → Run anyway**. You can also build it yourself from the source code (see the README).

4. Optional: run **`Check-Setup.bat`**. Every line should be **[OK]**.

---

## Step 6: Watch TV

1. Open **Android TV Desktop** (Desktop or Start Menu).
2. Press **Launch TV**. The emulator starts (or the running one is reused), Android boots, and Google TV fills the
   whole screen.
3. Controls:

| Key | What it does |
|---|---|
| Arrows / Enter | Navigate / select |
| Backspace | Back |
| Home | Google TV home |
| **F11** | Fullscreen ⇄ window |
| **Esc** | Leave fullscreen |
| **Ctrl + Left Alt + Q** | **Emergency exit**: always gives you your desktop back |

4. **Phone as remote** (optional): **Controls → Phone remote → Enable**, then **Allow through Windows
   Firewall…**. On your phone (same Wi-Fi), open the address shown and type the PIN. Add the page to your home
   screen.
5. **Install more apps:** on the TV, open **Apps → Search for apps** (Play Store). YouTube, Prime Video,
   Crunchyroll, JioHotstar, Spotify and others work. **Netflix** isn't available for emulators, so use the
   **Netflix (PC)** button instead.
6. **Always ready** (optional): **System → When I sign in to Windows → Start in the background**. The phone remote
   is then ready whenever the PC is on.

---

## Setup problems

| Problem | Fix |
|---|---|
| `winget` isn't recognised | Use Option B (website download), or install *App Installer* from the Microsoft Store |
| "HAXM / hypervisor not available" or the emulator is extremely slow | Step 2: Windows Hypervisor Platform + BIOS virtualization, then restart |
| No **Google TV** image in the list | In Device Manager's image list, open the **x86 Images** / **Other Images** tab. Check you're online. Update Android Studio (Help → Check for Updates) |
| The emulator closes or hangs while starting | Not enough free RAM. Close browsers and games, or use the **Television (1080p)** device with 2048 MB RAM |
| Black screen / "Android is starting…" for a long time | First boot takes a few minutes. If it never finishes: Device Manager → ⋮ → **Wipe Data** (this resets the TV and you sign in again) |
| Play Store: "Authentication is required" | Device Manager → ⋮ → **Cold Boot Now** (fixes the TV clock) |
| "Device isn't compatible" for an app | That app doesn't allow emulators (Netflix, some banking/DRM apps). Use the PC version if there is one |
| Android TV Desktop can't find the emulator | Run `Check-Setup.bat`. Make sure the SDK is in `%LOCALAPPDATA%\Android\Sdk`, or set the path on the **System** page |
| Screen stuck on the TV | **Ctrl + Left Alt + Q**, or Start Menu → **Restore desktop (emergency)** |
