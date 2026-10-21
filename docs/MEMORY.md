# Project memory (facts learned the hard way)

Measured on the reference PC: Windows 11, 1920×1080 monitor at 125 % scaling (no HDR),
Android Emulator 37.2.12, AVD `Television_4K` (3840×2160 @ 640 dpi, `google-tv`, 2 GB guest RAM,
`showDeviceFrame=yes`, skin `tv_4k`), 8 GB host RAM.

- **The emulator window is already `WS_POPUP`.** There's no title bar to strip. The visible "border" is
  Qt's transparent margin and the device frame.
- **Qt paints the picture inset.** In a 1920×1080 window the picture is at (60,39) 1798×1041:
  A=0.0313, B=0.0361, C=0.9677, D=1.0. The picture's top edge sits about 40 px down and its bottom edge
  reaches the window's bottom.
- **Qt sets its own window region (mask)** a few hundred ms after every resize, replacing any region
  we set.
- **With the `tv_4k` skin**, Qt snaps the window to the skin's aspect ratio (1798×1080) about 0.5 s after an
  external resize. `-no-skin` is ignored by emulator 37. `-skin 3840x2160` gives a frameless, exact 16:9
  window without changing the guest resolution or `config.ini`.
- **The toolbar** (`Qt653QWindowToolSaveBits`, title "Emulator") is owned by the main window, so it is
  always drawn above it.
- **qemu creates about 60 hidden helper windows** (NVOpenGLPbuffer, DummyWin, IME). Pick the main window by
  title, visibility, owner and process; never use `MainWindowHandle`.
- **Before the TV title appears**, the main window's title is just "Emulator". A qemu stuck in that state
  (Not Responding, about 10 MB working set) happened once with about 700 MB free host RAM. It's an emulator
  problem; the app times out cleanly and says so.
- **The Windows 11 taskbar drops below** a foreground topmost window that covers the monitor. Hiding the
  taskbar was never needed in testing.
- **Antivirus software** (e.g. Microsoft Defender) can flag the GUI test scripts (synthetic keys plus screen capture).
  That is why the live GUI test harness is not part of the public repository.
- **PowerShell 5 quirks:** `.Count` on a single CIM object can be blank (use `@(...).Length`). An unbalanced
  `(` inside a regex nested in `"$(...)"` breaks the parser. PowerShell cannot call C# methods with `out`
  enum parameters (return a struct instead).
- **MSBuild from VS 18 Insiders hangs** on this PC, and there's no .NET SDK, so build with Roslyn `csc`
  directly (`build.ps1`).
- **`DataContractJsonSerializer` skips field initializers.** Defaults must be applied in `[OnDeserializing]`.
