# Surface Notes

A minimal handwriting notes app for a Surface Pro with a Surface Pen.

It is a native Windows app built on the Windows Ink engine (the same one Windows uses for its own pen apps), so ink is drawn by the GPU off the main thread. The app does nothing while you're not writing, which keeps CPU use and heat low.

## What it does

- **Notebooks are folders you choose.** Tap ☰ to open the side panel, then `+` next to Notebooks:
  - **New notebook…** asks for a name and where to save it, for example a folder in OneDrive. It remembers the last place you used.
  - **Open a notebook folder…** adds a notebook that already exists, such as one synced from another PC. Pick a folder that holds several notebooks and it offers to add them all.
  
  Notebooks can live in different places. The panel shows where each one is kept. Press and hold (or right-click) a notebook to rename it, show it in File Explorer, or remove it from the list. Removing never deletes anything: delete the folder in File Explorer if you want it gone.
- **Pages.** `+` next to Pages (or Ctrl+N) adds a page. Press and hold a page to delete it (it goes to the Recycle Bin).
- **Pen** with pressure, 4 colors and 3 sizes, remembered between launches. **Highlighter** (yellow when black is selected).
- **Eraser.** Pick the eraser tool, or flip the Surface Pen over and use its eraser end. It erases whole strokes.
- **Lasso.** Pick the lasso tool, or hold the pen's side button while you drag, in any tool. Draw around ink to select it, drag inside the box to move it, and tap outside it to let go. The bin button (or Delete) removes the selection.
- **Undo**: the arrow button or Ctrl+Z.
- **Handwriting to text**: the "A" button converts the selection, or the whole page if nothing is selected. It runs on the device with no internet. You can edit the text and press Copy.
- **Palm rejection.** Fingers and palms never draw. One finger scrolls and two fingers zoom. While the pen is near the screen, and for a second after, touch scrolling is switched off so a resting palm can't move the page.
- **Autosave.** Saves 3 seconds after you stop writing (at least every 20 seconds while you keep writing), and right away when you switch pages or apps, or close the app. It reopens on the page you were last on in each notebook.

## How notes are stored

Each notebook is a plain folder, and each page is one file in it:

```
OneDrive\School\Biology\
    2026-11-25 141830.gif
    2026-11-26 091502.gif
```

A page file is a GIF picture of the page with the full pen data (Windows Ink, with pressure) saved inside it. File Explorer, OneDrive and any image viewer show it as a picture, and Surface Notes reads the ink back exactly. Pages are named by when they were made, so they stay in order, and nothing else is kept in the folder, so there is no shared file for two PCs to fight over.

**OneDrive and other sync.** Pages are written whole (to a temporary file that then replaces the page), so OneDrive never uploads half a page, and a page that hasn't changed is never rewritten. While a notebook is open the app watches its folder, so pages added or changed on another PC show up on their own. If a page was changed somewhere else while you were also writing on it, both versions are kept: the other one stays, and yours is saved next to it as "Page N (copy from <this PC>)". OneDrive's own conflict copies show up the same way. If a page can't be read (for example a OneDrive file that isn't downloaded while you're offline), it is left untouched instead of being saved over.

Tip: in File Explorer, right-click your notes folder in OneDrive and choose **Always keep on this device**, so every page opens instantly, even offline.

**Notebooks from versions before 1.1** were kept inside the app (`%LOCALAPPDATA%\Packages\SurfaceNotes.Local_*\LocalState\Notebooks`). They still open as before, marked "inside the app". The first launch of 1.1 offers to move them to a folder you pick, and you can do it any time later: press and hold the notebook and choose **Move to a folder…**. Each page is copied and checked before the original is removed.

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

Every push to `main` makes GitHub build a new signed version (see `.github/workflows/build.yml`) and publish it to the download page. Pull requests are only compiled, as a check, and never published. The version is `1.1.<build number>`; change `VERSION_PREFIX` in the workflow for a new major or minor version. Each time you open the app it checks for a new version; if there is one, Windows shows a short "updating" window and then opens the new version. Your notes are kept.

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

Notebooks in OneDrive are backed up by OneDrive (including its version history). For a notebook kept elsewhere, copy its folder.

## Troubleshooting log

Problems (a page that couldn't be saved or opened, for example) are written to `%LOCALAPPDATA%\Packages\SurfaceNotes.Local_*\LocalState\log.txt`.
