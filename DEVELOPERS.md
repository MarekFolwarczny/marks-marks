# Developer guide

## Platform and build

Mark´s Marks is a standalone Windows application built with C# and .NET Framework
4.5+ (4.8 recommended). WinForms provides the editor and controls; WPF MediaElement,
hosted through ElementHost, provides video playback using Windows codecs.

Run these commands in Windows PowerShell from the repository directory:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\tests\test.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\tests\test.ps1 -Suite Regression
```

The build uses the system `csc.exe`, regenerates the multi-resolution icon, embeds
graphics, and produces `MarksMarks.exe` and `MarksMarks.xml`. XML documentation is
for development; the EXE runs independently. Keep C# sources in UTF-8 and PowerShell
scripts in UTF-8 with a BOM for Windows PowerShell 5.1.

The assembly version is `1.0.1.0`. `Program.WindowTitle` derives the visible
`Mark´s Marks v1.0.1` caption from it. Instance activation uses the same caption;
change the assembly version when preparing another release.

## Responsibilities

| File | Responsibility |
| --- | --- |
| `Program.cs` | STA entry point, instance activation, storage selection, main UI, tray, overlay, preferences, and save status |
| `NotesStore.cs` | Models, validation, file commits, save cache, storage copying, recovery journal, and attachments |
| `NotesTextBox.cs` | Tab indentation, attachment pasting, and native editor scrolling |
| `BrandControls.cs` | Project list, custom scrollbars, drawn toolbar icons, input handling, and accessibility |
| `LayeredOverlay.cs` | Premultiplied-alpha logo and temporary Win32/GDI resources |
| `ImageViewer.cs` | Owned bitmap, pointer-centered zoom, pan, navigation, and modification callbacks |
| `VideoViewer.cs` | MediaElement lifecycle, timeline, zoom/pan, navigation, and playback restoration |
| `VideoFormats.cs` | Supported extensions and basic container header validation |
| `ProjectArchive.cs` | Validated project ZIP import/export and staging ownership |
| `UiText.cs` | English caption keys, Czech translation data, and local language preference |
| `tests/Tests.cs` | Isolated UI, storage, language, archive, and attachment checks |
| `tests/RegressionTests.cs` | Interruption recovery, damaged media, path limits, copy rollback, preferences, cache, and layout |

## Storage contract

`NotesState` owns projects, the selected Id, and overlay visibility/coordinates.
Each `Project` has a GUID Id, user Name, stable Folder component, and in-memory Text.
`ShouldSerializeText` reads legacy inline XML text while new indices exclude it.
Notes are stored as UTF-8 in each note folder's `notes.txt`.

The index is `notes.xml`, with attachment directories `images` and `videos`.
Load recovers pending journals before migrating legacy filenames. Migration
preflights destination collisions and path lengths, then renames files and
directories without replacement; interrupted moves resume on the next Load.
Notes, references, attachment names, and independent backups remain unchanged.
When copying an original legacy source, Load(false) retains its layout and CopyFrom
creates English paths at the destination. The preference directory remains
`%LOCALAPPDATA%\ProjektovePoznamky` so existing settings continue to work.
Renaming a note group changes its display name while keeping its folder stable.

New folder components use at most 64 characters and can be shortened further to
reserve space for attachments, backups, and journal paths. Names remain unique
without case sensitivity. `CheckFilePath` rejects paths exceeding 259 UTF-16 code
units before moving or creating the destination. User display names are preserved.

Storage assumes **one writer**. Text/index caches describe the last successful
Load or Save. Unchanged files are neither read nor written; failures leave changed
files eligible for retry. An external edit requires closing/reopening the app.
Saving is atomic per file, not a transaction across every changed project.

`WriteAtomic` writes and flushes a temporary file, backs up the existing target,
then commits through `MoveFileEx` with replace/write-through flags. Invalid UTF-8
is rejected without replacing the damaged input. Recoverable index problems preserve
the original under a unique name before using its backup.

## Journal and cleanup

Each project deletion or attachment deletion/rename records a unique `.operace-*`
directory containing:

```text
operace.xml  # validated StoreOperation intent
pred         # original index or UTF-8 note
projekt/     # staged project folder for project deletion
priloha      # staged attachment when applicable
dokonceno    # commit marker written after saving the operation
```

The order is: save existing data, snapshot it, persist intent, move files, save the
changed model, synchronize the index backup for project deletion, commit, then
clean up. Load recovers operations before reading the model. Missing commitment
means rollback; existing commitment means cleanup only.

Rollback validates the snapshot and path components before modifying data. On
collision or invalid recovery data, retain the journal and remaining copies and
report the failure. Never infer ownership or delete a remaining copy to make an
error disappear. Legacy unknown staging directories are preserved for manual recovery.

Cleanup removes payload before metadata and the marker. IOException or access
failure during **committed cleanup** sets CleanupPending and allows normal loading
and saving. The UI shows a localized saved status. Save, Ctrl+S, or the next launch
retries cleanup without rewriting unchanged notes or replacing their backups.
Uncommitted rollback failure remains an error.

`operationActive` protects internal Save calls from recovery of their own operation.
Rollback invalidates only affected cached data; committed cleanup preserves it.
Storage copying uses a unique `.copy-*` staging area and records only folders it
owns. Ordinary failures remove those folders while preserving source/foreign files.
An interrupted process can leave staging for manual inspection.

## Attachments and ZIP

Image loading creates an independent bitmap and releases the file. The caller or
canvas disposes the bitmap it owns. Video changes close MediaElement before moving
files; renaming/cancellation restores playback position, pause, zoom, and pan.
After deleting the current attachment, select a valid remaining index. If the next
file is damaged, clear old pixels/playback and retain a safe deletable selection.

Rename callbacks update exact image references in both languages and Video references.
User text, filenames, and project names are never translation keys. Files must belong
to the selected project's validated media folder before modification.

ZIP format **3** uses `project.xml`, `notes.txt`, `images/`, and `videos/`.
Import also accepts legacy format **1** (text/images) and **2** (images/videos),
normalizing their paths before committing them to English storage. Mixed layouts
and mismatched format versions are rejected. These are archive format numbers,
independent of the application release number. Export excludes local
preferences, Ids, and backups. Import allocates a fresh project, preserves attachment
names, and does not replace existing projects.

Validate paths, duplicate entries, XML without DTDs/external entities, declared
sizes, actual copied sizes, PNG dimensions/decoding, and video container headers.
Limits: 1 GiB uncompressed data, 32 MiB note text, 10,000 PNGs, 1,000 videos, PNG
sides up to 30,000 pixels and at most 100 million pixels. Flat staging filenames
avoid unnecessarily extending the destination path. Codec playback remains a
Windows responsibility. Archive/media operations currently run synchronously.

## Localization and UI ownership

English is the default. UI call sites use English keys; Czech text is translation
data in UiText and language-specific test fixtures. T also accepts known Czech
captions for compatibility. Only tagged control captions are translated; clear Tag
for controls showing user filenames. Menus retain their keys in Tag. Window owners
unsubscribe from LanguageChanged when disposed.

Project sorting uses cs-CZ, including its alphabetic rules. Changing UI language does
not translate user text or change the sorting culture. Native dialogs and system
errors use the Windows language. Overlay coordinates are clamped to an available
screen, while negative coordinates on other monitors remain valid.

## Validation and release

Tests initialize WinForms on STA and run independent suites in separate processes.
They use generated PNG/AVI fixtures in GUID directories under `tests/output`.
User storage and clipboard contents are not modified. An interactive Windows desktop
is required. Errors produce nonzero exit codes instead of blocking exception dialogs.

The suite covers recovery interruption points, invalid archives, rollback ownership,
Unicode/path limits, bilingual references, native media release, save retries,
accessibility, control contrast, and consistent sidebar button sizes.
Physical power loss, every Windows codec, network filesystem behavior, and every
DPI/monitor combination are outside this local test coverage.

Before releasing, build and run the full suite, update the assembly version and
Changelog, then attach the EXE and ZIP to the matching GitHub release tag.
Ship the MIT license with the ZIP. Generated binaries, test output, and private
backups are excluded from Git history. The first public release is version 1.0.0;
older development reports are not part of this repository.
