using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace ProjektovePoznamky
{
    /// <summary>Static translations and language preferences; UI updates run on the main STA thread.</summary>
    internal static class Ui
    {
        /// <summary>Active cs or en language, defaulting to en.</summary>
        internal static string Language { get; private set; }
        /// <summary>Whether static captions use English rather than Czech.</summary>
        internal static bool English { get { return Language == "en"; } }
        /// <summary>UI-thread language change notification; window owners must unsubscribe during Dispose.</summary>
        internal static event Action LanguageChanged;
        /// <summary>Initialize English before loading the saved preference.</summary>
        static Ui() { Language = "en"; }
        private static readonly Dictionary<string, string> CzechText = new Dictionary<string, string>
        {
            { "Cannot migrate storage because both names exist:\n", "Úložiště nelze převést, protože existují oba názvy:\n" },
            { "Czech", "Čeština" },
            { "Delete project including its note, images and videos", "Smazat projekt včetně poznámky, snímků a videí" },
            { "Export project including images and videos to ZIP", "Exportovat projekt včetně snímků a videí do ZIPu" },
            { "Ctrl+V pastes images or videos  ·  Tab adds a subpoint", "Ctrl+V vloží snímek či video  ·  Tab přidá podbod" },
            { "Images and videos for the current project", "Snímky a videa aktuálního projektu" },
            { "Cannot load attachments: ", "Přílohy nelze načíst: " },
            { "Delete project \"{0}\"?\n\nIts folder, note, all images and videos will be removed.", "Smazat projekt „{0}“?\n\nOdstraní se jeho složka, poznámka, všechny snímky a videa." },
            { "This folder already contains projects. Open them?\n\nYour current notes, images and videos will remain in:\n", "Tato složka už obsahuje projekty. Otevřít je?\n\nSoučasné poznámky, snímky a videa zůstanou zachované v:\n" },
            { "Export project including images and videos", "Exportovat projekt včetně snímků a videí" },
            { "Import project including images and videos", "Importovat projekt včetně snímků a videí" },
            { "The video does not belong to this project.", "Video není v tomto projektu." },
            { "Could not paste attachment.\n\n", "Přílohu se nepodařilo vložit.\n\n" },
            { "Add video", "Přidat video" },
            { "Add video from a file · you can also paste with Ctrl+V", "Přidat video ze souboru · lze také vložit přes Ctrl+V" },
            { "Video files", "Video soubory" },
            { "Could not add video.\n\n", "Video se nepodařilo přidat.\n\n" },
            { "Play {0}", "Přehrát {0}" },
            { "Play video", "Přehrát video" },
            { "Delete video…", "Smazat video…" },
            { "Cannot open video.\n\n", "Video nelze otevřít.\n\n" },
            { "Delete video \"{0}\"?\n\nThe file and its references in the note will be removed.", "Smazat video „{0}“?\n\nSoubor i jeho odkazy v poznámce budou odstraněny." },
            { "Delete video", "Smazat video" },
            { "The project exceeds the archive limit: 1 GiB, 10,000 images, 1,000 videos or 32 MiB of text.", "Projekt překračuje limit archivu: 1 GB, 10 000 snímků, 1 000 videí nebo 32 MB textu." },
            { "The archive contains too many videos.", "Archiv obsahuje příliš mnoho videí." },
            { "Unsupported video format. Use MP4, M4V, MOV, AVI, WMV or MPEG.", "Nepodporovaný formát videa. Použijte MP4, M4V, MOV, AVI, WMV nebo MPEG." },
            { "The video is empty or damaged.", "Video je prázdné nebo poškozené." },
            { "The file does not contain a valid video of this format.", "Soubor neobsahuje platné video daného formátu." },
            { "The project has no videos.", "Projekt neobsahuje žádná videa." },
            { "Cannot play video. The format or codec may not be supported by Windows.\n\n", "Video nelze přehrát. Formát nebo kodek nemusí být podporovaný systémem Windows.\n\n" },
            { "Video; use the mouse wheel to zoom and drag to pan", "Video; kolečkem přiblížíte, tažením přesunete" },
            { "Cannot play video", "Video nelze přehrát" },
            { "Pause", "Pozastavit" },
            { "Play", "Přehrát" },
            { "Unmute", "Zapnout zvuk" },
            { "Mute", "Ztlumit" },
            { "Previous video", "Předchozí video" },
            { "Next video", "Další video" },
            { "Playback position", "Pozice přehrávání" },
            { "Rename image…", "Přejmenovat snímek…" },
            { "Rename video…", "Přejmenovat video…" },
            { "Rename image", "Přejmenovat snímek" },
            { "Rename video", "Přejmenovat video" },
            { "Attachment name (without extension)", "Název přílohy (bez přípony)" },
            { "Could not rename attachment.\n\n", "Přílohu se nepodařilo přejmenovat.\n\n" },
            { "The attachment name must not be empty.", "Název přílohy nesmí být prázdný." },
            { "This name is reserved by Windows.", "Tento název je vyhrazen systémem Windows." },
            { "An attachment with this name already exists in the project.", "Příloha s tímto názvem už v projektu existuje." },
            { "The path is too long. Choose a shorter storage path or attachment name.", "Zvolená cesta je příliš dlouhá. Zvolte kratší cestu úložiště nebo název přílohy." },
            { "Invalid storage recovery record.", "Neplatný záznam obnovy úložiště." },
            { "Storage recovery data is missing.", "Data pro obnovu úložiště chybí." },
            { "New project", "Nový projekt" },
            { "Storage…", "Úložiště…" },
            { "Import project…", "Import projektu…" },
            { "Open notes    Ctrl+Alt+N", "Otevřít poznámky    Ctrl+Alt+N" },
            { "Floating button", "Plovoucí tlačítko" },
            { "Start with Windows", "Spouštět s Windows" },
            { "Change storage…", "Změnit úložiště…" },
            { "Open storage folder", "Otevřít složku úložiště" },
            { "Exit", "Ukončit" },
            { "Language", "Jazyk / Language" },
            { "Change language", "Změnit jazyk / Change language" },
            { "Project scrollbar", "Posouvání projektů" },
            { "Note scrollbar", "Posouvání poznámky" },
            { "Position {0} of {1}", "Pozice {0} z {1}" },
            { "Projects", "Projekty" },
            { "Search projects by name", "Hledat projekt podle názvu" },
            { "Rename project    F2", "Přejmenovat projekt    F2" },
            { "Export project…", "Exportovat projekt…" },
            { "Delete project…", "Smazat projekt…" },
            { "Rename project", "Přejmenovat projekt" },
            { "Rename selected project (F2)", "Přejmenovat vybraný projekt (F2)" },
            { "Rename project · F2", "Přejmenovat projekt · F2" },
            { "Delete project", "Smazat projekt" },
            { "Delete project including its note and images", "Smazat projekt včetně poznámky a snímků" },
            { "Export project", "Exportovat projekt" },
            { "Export project including images to ZIP", "Exportovat projekt včetně snímků do ZIPu" },
            { "Ctrl+V also pastes images  ·  Tab adds a subitem", "Ctrl+V vloží i snímek  ·  Tab přidá podbod" },
            { "Notes for the selected project", "Poznámky k vybranému projektu" },
            { "Images for the current project", "Snímky aktuálního projektu" },
            { "Create a new project", "Vytvořte nový projekt" },
            { "No matching project", "Žádný nalezený projekt" },
            { "Project name", "Název projektu" },
            { "Save", "Uložit" },
            { "Cancel", "Zrušit" },
            { "General", "Obecné" },
            { "Image", "Snímek" },
            { "View {0}", "Zobrazit {0}" },
            { "View image", "Zobrazit snímek" },
            { "Delete image…", "Smazat snímek…" },
            { "Delete image", "Smazat snímek" },
            { "Right-click to rename or delete this attachment.", "Pravým tlačítkem otevřete nabídku přílohy." },
            { "Cannot load", "Nelze načíst" },
            { "Cannot load images: ", "Snímky nelze načíst: " },
            { "Image; scroll to zoom, drag to pan", "Snímek; kolečkem přiblížíte, tažením přesunete" },
            { "Previous image", "Předchozí snímek" },
            { "Next image", "Další snímek" },
            { "Fit", "Přizpůsobit" },
            { "No images", "Žádné snímky" },
            { "The project has no images.", "Projekt neobsahuje žádné snímky." },
            { "Cannot open the image.\n\n", "Snímek nelze otevřít.\n\n" },
            { "Could not paste the image.\n\n", "Snímek se nepodařilo vložit.\n\n" },
            { "Delete image \"{0}\"?\n\nThe file and its references in the note will be removed.", "Smazat snímek „{0}“?\n\nSoubor i jeho odkaz v poznámce budou odstraněny." },
            { "Delete project \"{0}\"?\n\nIts folder, note and all images will be removed.", "Smazat projekt „{0}“?\n\nOdstraní se jeho složka, poznámka a všechny snímky." },
            { "Export project including images", "Exportovat projekt včetně snímků" },
            { "Import project including images", "Importovat projekt včetně snímků" },
            { "Mark´s Marks project (*.zip)|*.zip", "Projekt Mark´s Marks (*.zip)|*.zip" },
            { "Could not export the project.\n\n", "Projekt se nepodařilo exportovat.\n\n" },
            { "Could not import the project.\n\n", "Projekt se nepodařilo importovat.\n\n" },
            { "Select or create a project first.", "Nejprve vyberte nebo vytvořte projekt." },
            { "Save the project first.", "Nejprve je potřeba uložit projekt." },
            { "Saving…", "Ukládání…" },
            { "Saved · ", "Uloženo · " },
            { "Saved; file cleanup pending · ", "Uloženo; úklid souborů čeká · " },
            { "Not saved — press Ctrl+S to retry", "Neuloženo — Ctrl+S zkusí uložit znovu" },
            { "Could not save notes. Your text remains in the open application.\n\n", "Poznámky se nepodařilo uložit. Text zůstává v otevřené aplikaci.\n\n" },
            { "Cannot change startup settings.\n\n", "Automatické spouštění nelze změnit.\n\n" },
            { "Could not save the language preference. The previous language remains active.\n\n", "Jazyk se nepodařilo uložit. Původní jazyk zůstává aktivní.\n\n" },
            { "Mark´s Marks · quick notes", "Mark´s Marks · rychlé poznámky" },
            { "Open project notes", "Otevřít projektové poznámky" },
            { "Notes · Ctrl+Alt+N\nDrag to move. Right-click to open the menu.", "Poznámky · Ctrl+Alt+N\nPřetažením přesunete. Pravé tlačítko otevře nabídku." },
            { "Ctrl+Alt+N is used by another application. Open notes using the floating button or tray icon.", "Ctrl+Alt+N používá jiná aplikace. Poznámky otevřete plovoucím tlačítkem nebo ikonou u hodin." },
            { "Storage was safely recovered. Any damaged files were preserved in the notes folder.", "Úložiště bylo bezpečně obnoveno. Případné poškozené soubory zůstaly zachované ve složce s poznámkami." },
            { "Choose where to store notes. Each project will have its own folder.", "Vyberte úložiště poznámek. Každý projekt v něm bude mít vlastní složku." },
            { "The selected storage is missing its project list. The original folders were preserved:\n", "Ve zvoleném úložišti chybí seznam projektů. Původní složky zůstaly zachované:\n" },
            { "Storage is unavailable. Connect its drive or choose another folder.\n\n", "Úložiště není dostupné. Připojte jeho disk nebo zvolte jinou složku.\n\n" },
            { "This folder already contains projects. Open them?\n\nYour current notes and images will remain in:\n", "Tato složka už obsahuje projekty. Otevřít je?\n\nSoučasné poznámky a snímky zůstanou zachované v:\n" },
            { "Change storage", "Změnit úložiště" },
            { "Could not change storage. The original storage remains active.\n\n", "Úložiště se nepodařilo změnit. Původní úložiště zůstává aktivní.\n\n" },
            { "Cannot load the project list. The original files were preserved in:\n", "Seznam projektů nelze načíst. Původní soubory zůstaly zachované ve složce:\n" },
            { "Cannot load the note; its files were preserved:\n", "Poznámku nelze načíst; soubory zůstaly zachované:\n" },
            { "The note file is not valid UTF-8 text.", "Soubor poznámky není platný text UTF-8." },
            { "The notes file contains invalid data.", "Soubor poznámek má neplatný obsah." },
            { "Two projects cannot share the same folder.", "Dva projekty nesmějí sdílet stejnou složku." },
            { "Invalid project folder.", "Neplatná složka projektu." },
            { "The note contains an invalid Unicode character.", "Poznámka obsahuje neplatný znak Unicode." },
            { "The destination storage already contains projects.", "Cílové úložiště už obsahuje projekty." },
            { "The project is not in this storage.", "Projekt není v tomto úložišti." },
            { "The image is not in this project.", "Snímek není v tomto projektu." },
            { "The project was removed from the list, but some files could not be deleted:\n", "Projekt byl odebrán ze seznamu, ale zbývající soubory se nepodařilo smazat:\n" },
            { "Cannot save the file atomically (Windows ", "Soubor nelze atomicky uložit (Windows " },
            { "The archive must have a .zip extension.", "Archiv musí mít příponu .zip." },
            { "Save the archive outside the exported project's folder.", "Archiv uložte mimo složku exportovaného projektu." },
            { "The project exceeds the archive limit: 1 GiB, 10,000 images or 32 MiB of text.", "Projekt překračuje limit archivu: 1 GB, 10 000 snímků nebo 32 MB textu." },
            { "The archive contains too many files.", "Archiv obsahuje příliš mnoho souborů." },
            { "The archive contains a duplicate path: ", "Archiv obsahuje duplicitní cestu: " },
            { "The archive contains too many images.", "Archiv obsahuje příliš mnoho snímků." },
            { "Invalid folder entry in the archive.", "Neplatná položka složky v archivu." },
            { "Disallowed path in the archive: ", "Nepovolená cesta v archivu: " },
            { "The unpacked project exceeds 1 GiB.", "Rozbalený projekt překračuje 1 GB." },
            { "The archive is not a valid Mark´s Marks project.", "Archiv není platný projekt Mark´s Marks." },
            { "Unsupported version or invalid project name.", "Nepodporovaná verze nebo neplatný název projektu." },
            { "Invalid file size in the archive.", "Neplatná velikost souboru v archivu." },
            { "The archive contains an incomplete file.", "Archiv obsahuje neúplný soubor." },
            { "The archive contains an invalid PNG image.", "Archiv obsahuje neplatný PNG snímek." },
            { "The image dimensions are too large or invalid.", "Snímek má příliš velké nebo neplatné rozměry." },
            { "Invalid image dimensions.", "Neplatné rozměry snímku." }
        };

        /// <summary>Translate a static caption key and substitute format arguments; preserve unknown text.</summary>
        /// <param name="text">Static catalog key, never a user note.</param>
        /// <param name="values">Arguments passed to string.Format.</param>
        internal static string T(string text, params object[] values)
        {
            string translated;
            if (!English && CzechText.TryGetValue(text, out translated)) text = translated;
            else if (English && !CzechText.ContainsKey(text))
            {
                foreach (KeyValuePair<string, string> entry in CzechText)
                    if (entry.Value == text) { text = entry.Key; break; }
            }
            return values.Length == 0 ? text : string.Format(CultureInfo.CurrentCulture, text, values);
        }

        /// <summary>Translate a known static caption from either language into the active one.</summary>
        private static string TranslateStatic(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            if (CzechText.ContainsKey(text)) return T(text);
            foreach (KeyValuePair<string, string> entry in CzechText)
                if (entry.Value == text) return T(entry.Key);
            return text;
        }

        /// <summary>Recursively translate tagged captions, accessibility, and menus while preserving user text.</summary>
        /// <param name="control">Root of the control tree marked for translation.</param>
        /// <param name="tips">Owner&#x27;s tooltip manager, or null.</param>
        /// <remarks>Translate Text only when Tag contains a static key; controls showing filenames must clear Tag.</remarks>
        internal static void Apply(Control control, ToolTip tips = null)
        {
            // Only explicitly tagged captions are translated; project names and note text are user data.
            if (control.Tag is string) control.Text = T((string)control.Tag);
            control.AccessibleName = TranslateStatic(control.AccessibleName);
            control.AccessibleDescription = TranslateStatic(control.AccessibleDescription);
            if (tips != null) tips.SetToolTip(control, TranslateStatic(tips.GetToolTip(control)));
            if (control.ContextMenuStrip != null) ApplyMenu(control.ContextMenuStrip.Items);
            foreach (Control child in control.Controls) Apply(child, tips);
        }

        /// <summary>Retain original menu keys in Tag and translate nested menus recursively.</summary>
        internal static void ApplyMenu(ToolStripItemCollection items)
        {
            foreach (ToolStripItem item in items)
            {
                if (item.Tag == null) item.Tag = item.Text;
                item.Text = T((string)item.Tag);
                ToolStripDropDownItem dropdown = item as ToolStripDropDownItem;
                if (dropdown != null) ApplyMenu(dropdown.DropDownItems);
            }
        }

        /// <summary>Validate cs/en, set UI-thread culture, and notify subscribers after an actual change.</summary>
        /// <remarks>Set CurrentUICulture without changing project sorting or native Windows dialog language.</remarks>
        internal static void SetLanguage(string language)
        {
            ValidateLanguage(language);
            System.Threading.Thread.CurrentThread.CurrentUICulture = CultureInfo.GetCultureInfo(language == "en" ? "en-US" : "cs-CZ");
            if (Language == language) return;
            Language = language;
            if (LanguageChanged != null) LanguageChanged();
        }

        /// <summary>Reject language identifiers other than cs and en.</summary>
        private static void ValidateLanguage(string language)
        {
            if (language != "cs" && language != "en") throw new ArgumentException("Unsupported language.", "language");
        }

        /// <summary>Read the preference or return en when it is absent, invalid, or inaccessible.</summary>
        internal static string ReadLanguage(string settingsDirectory)
        {
            string path = Path.Combine(settingsDirectory, "jazyk.txt");
            try
            {
                if (!File.Exists(path)) path += ".bak";
                if (!File.Exists(path)) return "en";
                string language = File.ReadAllText(path, Encoding.UTF8).Trim();
                return language == "cs" ? "cs" : "en";
            }
            catch (IOException) { return "en"; }
            catch (UnauthorizedAccessException) { return "en"; }
        }

        /// <summary>Validate and save the language outside project storage with the previous value in .bak.</summary>
        internal static void SaveLanguage(string settingsDirectory, string language)
        {
            ValidateLanguage(language);
            NotesStore.WriteAtomic(Path.Combine(settingsDirectory, "jazyk.txt"), Encoding.UTF8.GetBytes(language));
        }
    }
}
