# FolderMap

A Windows desktop app (C# / .NET / Avalonia) for looking through your drives and
folders, seeing where the space goes, and tidying things up.

## What it does

| Tab | What it does |
|---|---|
| **Explorer** | Folder tree sorted by size, plus a **treemap** or **sunburst** chart of the current folder and a sortable list of what's in it. **Scroll to zoom in and out** around the mouse (deeper folder levels appear as you zoom), drag to pan, middle-click to reset. Click to select, double-click to go into a folder, right-click (or click the sunburst centre) to go back up. |
| **Search** | Search the scanned files by name (`invoice`, or wildcards like `IMG_2024*`), extension, minimum size and how recently they were modified. |
| **Largest files** | The 1,000 biggest files. |
| **File types** | How much space each file type uses. |
| **Duplicates** | Finds files with identical contents (it compares sizes first, then hashes). OneDrive files that are online-only are skipped so nothing gets downloaded. |

The bar at the bottom works on whatever is selected in any tab: **Open**, **Show in
Explorer**, **Show in tree**, **Rename**, **Move to…** and **Recycle**. Recycle sends
things to the Recycle Bin and never deletes them permanently.

## Running it

1. Install the **.NET SDK** (8 or newer): https://dotnet.microsoft.com/download
2. Open a terminal in this folder and run:

   ```
   dotnet run -c Release
   ```

   The first run downloads the Avalonia packages, so it takes a minute.

### Build a standalone .exe

```
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

The .exe will be in `bin\Release\net8.0\win-x64\publish\`.

### Opening in an editor

Open the folder in **Visual Studio 2022** or **JetBrains Rider**, or open
`FolderMap.csproj` in **VS Code** with the C# Dev Kit extension. For XAML previews,
install the Avalonia extension for your editor.

## How the code is organised

```
Models/Nodes.cs            FolderNode / FileNode: the in-memory tree with sizes
Services/Scanner.cs        Walks the disk and builds the tree (skips junctions and symlinks)
Services/DuplicateFinder   Groups files by size, then a 64 KB hash, then a full hash
Services/FileOps.cs        Open / reveal / rename / move / recycle
Controls/ZoomableChart     Shared base: wheel zoom, drag-to-pan, click handling
Controls/TreemapControl    Squarified treemap, 3 levels deep (more as you zoom)
Controls/SunburstControl   Ring chart, 5 levels deep
ViewModels/MainViewModel   All app state and commands (CommunityToolkit.Mvvm)
Views/MainWindow.axaml     The UI layout
Views/MainWindow.axaml.cs  Folder pickers, dialogs, selection syncing
```

## Notes and limits

- Sizes are file sizes, not "size on disk". OneDrive online-only files are counted
  at their full size.
- Folders you don't have permission to read are skipped, and the status bar says how
  many. To scan all of `C:\`, run the app as administrator.
- The scan runs through the normal file APIs. Scanning a whole drive with around a
  million files takes about 1–3 minutes. A later version could read the NTFS Master File
  Table directly, like WizTree does, to cut that to seconds.

## Ideas for next versions

- Save and load scans, and compare two scans to see what grew
- Reading the NTFS MFT for near-instant scans
- A force-directed "map" view of how folders connect
- Selecting several files at once for bulk move or recycle
- Rules such as "move every .pdf in Downloads older than 30 days to Documents\Archive"
