# Testing FolderMap

## 1. Automated tests

From the FolderMap folder:

```
dotnet test tests/FolderMap.Tests
```

- **Every test should pass.** A failure means something is broken.
- **`KnownWeaknessTests.cs` (class `FixedWeaknessTests`)** holds regression tests for the weaknesses found in the 2026-09-25 review. They're all fixed, so if one of these fails, that problem has come back.
- To run just those:
  `dotnet test tests/FolderMap.Tests --filter "Category=Regression"`

The tests create and delete their own temporary folders and never touch your real files.
They don't test Recycle, because that would put files in your real Recycle Bin.

## 2. Stress test checklist (by hand)

Work through these with the app running. Write the result next to each one.

- **Expected** is what should happen.
- **✅** marks a weakness found in review that has since been fixed. Check that the fix works on your PC.

### Scanning

| # | Try this | Expected |
|---|---|---|
| S1 | Scan `C:\` **without** admin rights | Finishes. The status bar says how many folders couldn't be read. No crash. |
| S2 | Scan `C:\` as admin (right-click the .exe → Run as administrator) | Finishes. Note the time and the memory use in Task Manager. |
| S3 | Type `C:` (no backslash) and press Scan ✅ | Scans the whole C: drive. |
| S4 | Press Cancel partway through a rescan ✅ | Stops within a second or two, even inside a huge folder. The previous scan stays on screen. |
| S5 | Scan your OneDrive folder with some files set to "online only" | Sizes show. **No files download** (watch the OneDrive icon). |
| S6 | Scan a USB stick, then unplug it and try Open / Rename | A clear error message, no crash. |
| S7 | Scan a network drive or NAS folder, if you have one | Works, just slower. |
| S8 | Scan a folder with a path longer than 260 characters (a deep `node_modules` is ideal) | The files are counted. |
| S9 | Compare the size of `C:\Windows` with what Explorer's Properties says | Expect the app to show **more**. Windows uses hard links, which get counted twice (known). |

### Browsing and charts

| # | Try this | Expected |
|---|---|---|
| B1 | Click a big folder with no code in it, e.g. `C:\Windows` ✅ | Responds instantly. "Analyse code" enables a moment later if there's code inside. |
| B2 | Expand `C:\Windows\WinSxS` in the tree ✅ | Opens quickly and shows the largest 500 plus a "… smaller folders" line. |
| B3 | Zoom the treemap to 64× on a big folder, then drag around quickly | Stays smooth. |
| B4 | Same in the sunburst | Stays smooth. |
| B5 | Resize the window from full screen down to its smallest size | Everything stays usable. The tree hides behind "Folders". |
| B6 | Move the window between monitors with different scaling | Text and charts stay sharp and sized correctly. |

### Search, largest files and duplicates

| # | Try this | Expected |
|---|---|---|
| D1 | Type `99999999999999` in "At least (MB)" and press Search ✅ | The box caps at 100,000,000. No crash. |
| D2 | Type `99999999` in "Modified in the last N days" ✅ | The box caps at 36,500 (100 years). The results are sensible. |
| D3 | Search `*` on a whole-drive scan | Shows the largest 5,000 and the total count. No freeze longer than a few seconds. |
| D4 | Run Duplicates on a big photo folder | Progress in the status bar. Cancel works. |
| D5 | In Duplicates, recycle one copy of a pair ✅ | The pair leaves the list, so the last copy can't be recycled from there by mistake. |

### File operations (use a test folder with copies of files!)

| # | Try this | Expected |
|---|---|---|
| F1 | Rename `Photo.JPG` → `Photo.jpg` ✅ | Renames. |
| F2 | Rename to `CON`, `a:b`, a name ending in `.` or a space ✅ | A clear explanation, and the file is untouched. |
| F3 | Move a folder into one of its own subfolders | Refused with a message. |
| F4 | Move a folder to another drive | Refused with a message (known limitation). |
| F5 | Rename or move a file that's open in Word or Excel | A clear "in use" error. |
| F6 | **Recycle a file on a USB stick or network drive** ✅ | FolderMap warns that the drive may have no Recycle Bin, then **Windows asks** before deleting permanently. Say no, and the file stays. Test with a throwaway file. |
| F7 | Recycle a file bigger than the Recycle Bin's size limit ✅ | Windows asks "delete permanently?" first. It never happens silently. |
| F8 | Delete a file in Explorer, then act on it in FolderMap without rescanning ✅ | A message saying it isn't there any more and suggesting a rescan. |
| F9 | Double-click a `.bat`, `.ps1` or `.py` file in the contents list ✅ | It asks "Run this?" first. Cancel does nothing. |

### Code map

| # | Try this | Expected |
|---|---|---|
| C1 | Analyse the FolderMap project itself | A sensible map. `MainViewModel` is well connected. |
| C2 | Analyse a large project (1,000+ files) | Progress messages. It finishes in a minute or two, and closing the window cancels it. |
| C3 | Double-click a Python or JavaScript dot **with VS Code not on PATH** ✅ | Opens in Notepad for reading. It never runs the script. |
| C4 | Analyse a folder containing a minified or bundled `.js` file ✅ | No hang. The file is skipped and noted in the status bar. |
| C5 | Open two Code map windows, then close the main window | No crash. |
| C6 | A file named `100%TEMP% done.py`, double-clicked with VS Code installed ✅ | Opens the right file (VS Code is launched directly, not through cmd.exe). |
| C7 | Select a method in the Code map | Its code appears under "Code · lines x–y of File" with line numbers. Very long methods are cut at 150 lines. |
| C8 | Switch the list to "Possibly unused" on the FolderMap project | Short list. Button click handlers (used from XAML), `[RelayCommand]` methods, overrides and tests are **not** in it. |
| C9 | Select `MainWindow.OnRecycleClick` → Start a path here → select `FileOps.EnsureExists` → Find path to selected | Shows 2 steps: OnRecycleClick calls Recycle, which calls EnsureExists. The graph shows only that chain. Clear path restores the map. (Calls made through generated code, like `[RelayCommand]` commands, can't be followed.) |
| C10 | Find a path between two unrelated things | A clear "nothing connects them" message. Nothing breaks. |
| C11 | Analyse a Java project (e.g. a Maven or Minecraft-mod folder) | Classes, methods and fields appear. Constructors show as teal dots. `target/` and `.gradle/` are skipped. `@Override` methods aren't listed as unused. |
| C12 | Analyse a Kotlin / Android project | `fun`s, `val`/`var` properties and `companion object` members show up under their class. `onCreate`-style methods aren't listed as unused. |

## 3. Known limitations (by design)

- **Hard links are counted once per link**, so `C:\Windows` looks bigger than in Explorer. Fixing this means opening every file during the scan, which would make scans several times slower.
- **Memory:** about 200–300 bytes per file, so roughly 1 GB for 4 million files. That's fine for most PCs; very large drives may need a 64-bit build with plenty of RAM.
- **Recycle** needs the normal 64-bit build of the app (the default on 64-bit Windows).
