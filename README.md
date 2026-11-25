# Surface Notes

A minimal handwriting notes app for a Surface Pro with a Surface Pen.

It is a native Windows app built on the Windows Ink engine (the same one Windows uses for its own pen apps), so ink is drawn by the GPU off the main thread. The app does nothing while you're not writing, which keeps CPU use and heat low.

## What it does

- **Notebooks and pages.** Tap the ☰ button to open the side panel. `+` adds a notebook or page. Right-click (or press and hold) a notebook to rename or delete it, or a page to delete it. Deleted items go to the Recycle Bin.
- **Pen** with pressure, 4 colors and 3 sizes. **Highlighter** (yellow when black is selected).
- **Eraser.** Pick the eraser tool, or flip the Surface Pen over and use its eraser end. It erases whole strokes.
- **Lasso.** Pick the lasso tool, or hold the pen's side button while you drag, in any tool. Draw around ink to select it, then drag inside the box to move it. The bin button (or Delete) removes the selection.
- **Undo**: the arrow button or Ctrl+Z.
- **Handwriting to text**: the "A" button converts the selection, or the whole page if nothing is selected. It runs on the device with no internet. You can edit the text and press Copy.
- **Palm rejection.** Fingers and palms never draw. One finger scrolls and two fingers zoom. While the pen hovers over the page, touch scrolling is also switched off so a resting palm can't move the page.
- **Autosave.** Saves 1.5 seconds after you stop writing, when you switch pages, and when the app closes. It reopens on the page you were last on.

Notes are saved here, as one `.isf` ink file per page:
`%LOCALAPPDATA%\Packages\SurfaceNotes.Local_*\LocalState\Notebooks\<notebook>\`

## Install on your Surface (one time)

1. Click Start, type **PowerShell**, right-click **Windows PowerShell** and choose **Run as administrator**.
2. Paste this and press Enter:

   ```
   irm https://malicious-sol.github.io/surface-notes/install.ps1 | iex
   ```

   It trusts the app's signing certificate, removes any copy you built with Visual Studio (your notes are backed up to Documents and copied back), and installs the app.
3. Open **Surface Notes** from the Start menu.

The download page is https://malicious-sol.github.io/surface-notes/.

## Updates

Every push to `main` makes GitHub build a new signed version (see `.github/workflows/build.yml`) and publish it to the download page. The installed app checks for a new version each time you open it and updates in the background, so the next launch runs the new version. Your notes are kept.

The signing key lives only in the repository secrets `SIGNING_PFX_BASE64` and `SIGNING_PFX_PASSWORD`. To create them (once), run this in PowerShell and follow the prompts:

```
irm https://raw.githubusercontent.com/malicious-sol/surface-notes/main/installer/make-signing-key.ps1 | iex
```

The build publishes the matching public certificate as `SurfaceNotes.cer` on the download page.

## Building it yourself with Visual Studio (optional)

1. Turn on Developer Mode: Settings > Privacy & security > For developers.
2. Install Visual Studio Community with the **WinUI application development** workload plus the **Universal Windows Platform tools** optional component.
3. Open `SurfaceNotes.sln`, choose **Release** and **x64**, then **Build > Deploy Solution**.

A copy deployed from Visual Studio doesn't auto-update. Running the install command above replaces it with the auto-updating version.

## If something goes wrong

- **"Running scripts is disabled":** the `irm … | iex` form above isn't affected by that setting; make sure you pasted the whole line.
- **The app won't install or update:** check the latest run under the repository's **Actions** tab.
- **"Handwriting recognition isn't available":** Settings > Time & language > Language & region > your language > ⋯ > Language options > Handwriting, and install it.

## Backing up notes

Copy the `Notebooks` folder at the path above. OneDrive doesn't sync it automatically.
