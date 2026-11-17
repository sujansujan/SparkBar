using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace SparkBar;

public sealed class SearchEngineEntry
{
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public bool Pin { get; set; }
    public string GlyphHex { get; set; } = "";
    public string Glyph => !string.IsNullOrEmpty(GlyphHex) && int.TryParse(GlyphHex, System.Globalization.NumberStyles.HexNumber, null, out int cp)
        ? char.ConvertFromUtf32(cp) : "";
    public string FaviconPath { get; set; } = "";
}

public sealed class ThemePalette
{
    public bool IsLight { get; init; }
    public Color BgTop { get; init; }
    public Color BgBottom { get; init; }
    public Color Surface { get; init; }
    public Color HoverTop { get; init; }
    public Color HoverBottom { get; init; }
    public ColorHoverBorder HoverBorder { get; init; }
    public Color Border { get; init; }
    public Color Separator { get; init; }
    public Color Text { get; init; }
    public Color SubText { get; init; }
    public Color Accent { get; init; }
    public Color AccentBgTop { get; init; }
    public Color AccentBgBottom { get; init; }
    public Color AccentRim { get; init; }
    public Color AccentText { get; init; }
    public Color KeycapBg { get; init; }
    public Color KeycapText { get; init; }
    public Color PillFill { get; init; }
    public Color PillBorder { get; init; }
    public Color FaviconTile { get; init; }
}

public readonly record struct ColorHoverBorder(Color Value);

public sealed class ConfigService
{
    public string BaseDir { get; } = AppContext.BaseDirectory;
    public string SearchEnginesFile => Path.Combine(BaseDir, "SearchEngines.txt");
    public string LegacyCustomFile  => Path.Combine(BaseDir, "CustomActions.txt");
    public string FaviconCacheDir   => Path.Combine(BaseDir, "FaviconCache");
    public string PopupOrderFile    => Path.Combine(BaseDir, "ToolbarOrder.txt");
    public string SettingsFile      => Path.Combine(BaseDir, "SparkBarSettings.ini");
    public string ExcludedAppsFile  => Path.Combine(BaseDir, "ExcludedApps.txt");
    public string ErrorLogFile      => Path.Combine(BaseDir, "SparkBarErrors.log");

    public static readonly string[] ActionCatalog =
    {
        "Search engines", "Cut", "Copy", "Append to clipboard", "Paste",
        "Type clipboard", "Calculate & convert", "Open link", "Clean URL",
        "Copy domain", "Save URL as action", "New email", "Open",
        "Show in folder", "Define", "QR code", "Translate", "Word count",
        "Change case / clean up"
    };

    public static readonly Dictionary<string, string> ActionNotes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Search engines"]        = "Default + pinned search engines (right-click Default to switch)",
        ["Cut"]                   = "Shown in editable text fields",
        ["Copy"]                  = "Right-click or Shift+click Copy on toolbar to Append (+Copy)",
        ["Append to clipboard"]   = "Appends selection to clipboard on a new line",
        ["Paste"]                 = "Shown in editable text fields (right-click for Clipboard History)",
        ["Type clipboard"]        = "Shown on manual Ctrl+Alt+P trigger",
        ["Calculate & convert"]   = "Math (= 42), units (km/mi, kg/lb, C/F), hex, timestamps",
        ["Open link"]             = "Shown when selection is a URL",
        ["Clean URL"]             = "Strips utm_, fbclid, gclid tracking params",
        ["Copy domain"]           = "Copies hostname from selected URL",
        ["Save URL as action"]    = "Turns any selected URL into a Search Engine / Web Action",
        ["New email"]             = "Shown when selection is an email address",
        ["Open"]                  = "Shown when selection is a local file path",
        ["Show in folder"]        = "Reveals local file path in Explorer",
        ["Define"]                = "Shown when selection is a single word",
        ["QR code"]               = "Generates scannable QR code popup",
        ["Translate"]             = "Opens selection in Google Translate",
        ["Word count"]            = "Live word/char pill (always shown in Aa menu)",
        ["Change case / clean up"]= "Opens the Aa text transform menu"
    };

    private static readonly HashSet<string> TextDefaultActions = new(StringComparer.OrdinalIgnoreCase)
    {
        "Append to clipboard", "Calculate & convert", "Save URL as action", "Word count", "Change case / clean up"
    };

    public static readonly string[] DefaultExcludedExes =
    {
        "WindowsTerminal.exe", "wt.exe", "cmd.exe", "powershell.exe", "pwsh.exe",
        "conhost.exe", "mintty.exe", "putty.exe", "explorer.exe", "wezterm-gui.exe",
        "alacritty.exe", "kitty.exe", "ConEmu.exe", "ConEmu64.exe", "MobaXterm.exe",
        "mstsc.exe", "SecureCRT.exe", "ttermpro.exe", "Tabby.exe", "Hyper.exe",
        "wsl.exe", "wslhost.exe", "vmconnect.exe", "VirtualBoxVM.exe", "vmplayer.exe"
    };

    public HashSet<string> ExcludedClasses { get; } = new(StringComparer.OrdinalIgnoreCase)
    {
        "ConsoleWindowClass", "CASCADIA_HOSTING_WINDOW_CLASS", "mintty",
        "VirtualConsoleClass", "TscShellContainerClass", "PuTTY"
    };

    public HashSet<string> LineCopyExes { get; } = new(StringComparer.OrdinalIgnoreCase)
    {
        "Code.exe", "Code - Insiders.exe", "Cursor.exe", "Windsurf.exe", "devenv.exe",
        "idea64.exe", "pycharm64.exe", "webstorm64.exe", "rider64.exe", "clion64.exe",
        "goland64.exe", "phpstorm64.exe", "sublime_text.exe"
    };

    public List<SearchEngineEntry> SearchEngines { get; set; } = new();
    public string DefaultSearchEngine { get; set; } = "Google";
    public List<string> PopupOrder { get; set; } = new();
    public Dictionary<string, string> ActionDisplay { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, bool> ActionEnabled { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> ExcludedExes { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public string ThemeMode { get; set; } = "midnight";
    public string PopupSize { get; set; } = "compact"; // mini, compact, normal, large
    public bool TwoLayerPopup { get; set; } = true;
    public bool AnimationsEnabled { get; set; } = true;
    public bool ShowHintBadges { get; set; } = false;
    public bool KeyboardTriggerEnabled { get; set; } = false;
    public bool PauseInFullscreen { get; set; } = true;
    public int PopupTimeoutMs { get; set; } = 6000;
    public int MinDragPx { get; set; } = 8;
    public double CopyTimeoutSec { get; set; } = 0.16;
    public int TypeDelayMs { get; set; } = 5;
    public int MaxChars { get; set; } = 20000;
    public bool UseCopyFallback { get; set; } = true;
    public bool CopyFallbackWhenUiaEmpty { get; set; } = false;
    public int MaxUrlLen { get; set; } = 2000;
    public int QrMaxChars { get; set; } = 900;
    public bool AllowUncPaths { get; set; } = false;
    public bool UseThirdPartyIconServices { get; set; } = true;
    public bool ShowEditWhenUnknown { get; set; } = true;
    public int FadeInMs { get; set; } = 55;
    public int FadeOutMs { get; set; } = 45;
    public int ToastInMs { get; set; } = 65;
    public int ToastOutMs { get; set; } = 100;

    public const string DefineUrl = "https://www.google.com/search?q=define+";
    public const string TranslateUrl = "https://translate.google.com/?sl=auto&tl=en&op=translate&text=";

    public string IconFontFamily { get; } =
        File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "SegIcons.ttf"))
            ? "Segoe Fluent Icons"
            : "Segoe MDL2 Assets";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };
    private readonly ConcurrentDictionary<string, byte> _faviconInFlight = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, BitmapSource> _faviconMemoryCache = new(StringComparer.OrdinalIgnoreCase);

    public ThemePalette Palette { get; private set; }

    private readonly List<string> _clipHistory = new(12);
    public IReadOnlyList<string> ClipboardHistory => _clipHistory;

    public void RecordClipboardItem(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        string trimmed = text.Trim();
        if (trimmed.Length == 0 || trimmed.Length > 4096) return;
        _clipHistory.RemoveAll(s => string.Equals(s, trimmed, StringComparison.Ordinal));
        _clipHistory.Insert(0, trimmed);
        if (_clipHistory.Count > 12)
            _clipHistory.RemoveRange(12, _clipHistory.Count - 12);
    }

    public void ClearClipboardHistory() => _clipHistory.Clear();

    private const string StartupRegPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupRegValueName = "SparkBar";

    public bool IsRunAtStartupEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(StartupRegPath, writable: false);
            return key?.GetValue(StartupRegValueName) is string s && !string.IsNullOrWhiteSpace(s);
        }
        catch { return false; }
    }

    public void SetRunAtStartup(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(StartupRegPath, writable: true);
            if (key == null) return;
            if (enable)
            {
                string exePath = Environment.ProcessPath ?? Path.Combine(BaseDir, "SparkBar.exe");
                key.SetValue(StartupRegValueName, $"\"{exePath}\"");
            }
            else
            {
                key.DeleteValue(StartupRegValueName, throwOnMissingValue: false);
            }
        }
        catch { }
    }

    public ConfigService()
    {
        ReloadAll();
    }

    public void ReloadAll()
    {
        ResetCatalogDefaults();
        SearchEngines = LoadSearchEngines();
        PopupOrder = LoadListFile(PopupOrderFile);
        ExcludedExes = LoadExcludedApps();
        LoadUserSettings();
        UpdatePalette();
    }

    private void ResetCatalogDefaults()
    {
        ActionDisplay.Clear();
        ActionEnabled.Clear();
        foreach (string name in ActionCatalog)
        {
            ActionDisplay[name] = TextDefaultActions.Contains(name) ? "text" : "icon";
            ActionEnabled[name] = !string.Equals(name, "Word count", StringComparison.OrdinalIgnoreCase);
        }
    }

    public void LoadUserSettings()
    {
        DefaultSearchEngine = ReadIni("General", "DefaultSearchEngine", DefaultSearchEngine).Trim();
        AnimationsEnabled = ReadIni("General", "Animations", "1") != "0";
        ShowHintBadges = ReadIni("General", "ShowHintBadges", "0") == "1";
        KeyboardTriggerEnabled = ReadIni("General", "KeyboardTrigger", "0") == "1";
        PauseInFullscreen = ReadIni("General", "PauseInFullscreen", "1") != "0";
        UseCopyFallback = ReadIni("General", "UseCopyFallback", "1") != "0";
        AllowUncPaths = ReadIni("General", "AllowUncPaths", "0") == "1";

        if (int.TryParse(ReadIni("General", "PopupTimeoutMs", PopupTimeoutMs.ToString()), out int to) && to >= 500)
            PopupTimeoutMs = to;
        if (int.TryParse(ReadIni("General", "MinDragPx", MinDragPx.ToString()), out int dp) && dp >= 2)
            MinDragPx = dp;

        string tm = ReadIni("General", "ThemeMode", ThemeMode).Trim().ToLowerInvariant();
        if (tm is "light" or "dark" or "auto" or "midnight" or "slate")
            ThemeMode = tm;

        string ps = ReadIni("General", "PopupSize", PopupSize).Trim().ToLowerInvariant();
        if (ps is "mini" or "compact" or "normal" or "large")
            PopupSize = ps;

        TwoLayerPopup = ReadIni("General", "TwoLayerPopup", "1") != "0";

        foreach (string label in ActionCatalog)
        {
            string disp = ReadIni("Display", label, ActionDisplay[label]).Trim().ToLowerInvariant();
            ActionDisplay[label] = disp == "text" ? "text" : "icon";
            string en = ReadIni("Enabled", label, ActionEnabled[label] ? "1" : "0").Trim();
            ActionEnabled[label] = en == "1";
        }
    }

    public void SaveUserSettings()
    {
        WriteIni("General", "DefaultSearchEngine", DefaultSearchEngine);
        WriteIni("General", "ThemeMode", ThemeMode);
        WriteIni("General", "PopupSize", PopupSize);
        WriteIni("General", "TwoLayerPopup", TwoLayerPopup ? "1" : "0");
        WriteIni("General", "Animations", AnimationsEnabled ? "1" : "0");
        WriteIni("General", "ShowHintBadges", ShowHintBadges ? "1" : "0");
        WriteIni("General", "KeyboardTrigger", KeyboardTriggerEnabled ? "1" : "0");
        WriteIni("General", "PauseInFullscreen", PauseInFullscreen ? "1" : "0");
        WriteIni("General", "UseCopyFallback", UseCopyFallback ? "1" : "0");
        WriteIni("General", "AllowUncPaths", AllowUncPaths ? "1" : "0");
        WriteIni("General", "PopupTimeoutMs", PopupTimeoutMs.ToString());
        WriteIni("General", "MinDragPx", MinDragPx.ToString());

        foreach (string label in ActionCatalog)
        {
            WriteIni("Display", label, ActionDisplay.TryGetValue(label, out var d) ? d : "icon");
            WriteIni("Enabled", label, (ActionEnabled.TryGetValue(label, out var e) && e) ? "1" : "0");
        }
    }

    public string ReadIni(string section, string key, string def = "")
    {
        if (!File.Exists(SettingsFile)) return def;
        var sb = new StringBuilder(1024);
        Native.GetPrivateProfileString(section, key, def, sb, (uint)sb.Capacity, SettingsFile);
        return sb.ToString();
    }

    public void WriteIni(string section, string key, string val)
    {
        try { Native.WritePrivateProfileString(section, key, val ?? "", SettingsFile); } catch { }
    }

    public static void WriteFileAtomic(string path, string content)
    {
        string tmp = path + ".tmp";
        try
        {
            File.WriteAllText(tmp, content, new UTF8Encoding(false));
            File.Move(tmp, path, true);
        }
        catch { }
    }

    public static List<string> LoadListFile(string path)
    {
        var list = new List<string>();
        if (!File.Exists(path)) return list;
        try
        {
            foreach (string raw in File.ReadAllLines(path))
            {
                string line = raw.Trim();
                if (!string.IsNullOrEmpty(line) && !line.StartsWith(';'))
                    list.Add(line);
            }
        }
        catch { }
        return list;
    }

    private HashSet<string> LoadExcludedApps()
    {
        if (!File.Exists(ExcludedAppsFile))
        {
            var def = new HashSet<string>(DefaultExcludedExes, StringComparer.OrdinalIgnoreCase);
            SaveExcludedApps(def);
            return def;
        }
        return new HashSet<string>(LoadListFile(ExcludedAppsFile), StringComparer.OrdinalIgnoreCase);
    }

    public void SaveExcludedApps(IEnumerable<string> exes = null)
    {
        exes ??= ExcludedExes;
        var sb = new StringBuilder("; Process names where SparkBar will never appear (one .exe per line)\n");
        foreach (string exe in exes)
            sb.Append(exe).Append('\n');
        WriteFileAtomic(ExcludedAppsFile, sb.ToString());
    }

    public void SavePopupOrder()
    {
        WriteFileAtomic(PopupOrderFile, string.Join("\n", PopupOrder) + "\n");
    }

    private List<SearchEngineEntry> LoadSearchEngines()
    {
        if (!File.Exists(SearchEnginesFile))
        {
            string defaults =
                "Google\thttps://www.google.com/search?q=\ttrue\tE721\n" +
                "DuckDuckGo\thttps://duckduckgo.com/?q=\tfalse\n" +
                "YouTube\thttps://www.youtube.com/results?search_query=\tfalse\n" +
                "Wikipedia\thttps://en.wikipedia.org/w/index.php?search=\tfalse\n" +
                "GitHub\thttps://github.com/search?q=\tfalse\n" +
                "Google Maps\thttps://www.google.com/maps/search/\tfalse\n";
            WriteFileAtomic(SearchEnginesFile, defaults);
        }

        var engines = new List<SearchEngineEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void ReadFrom(string fPath)
        {
            foreach (string line in LoadListFile(fPath))
            {
                string[] f = line.Split('\t');
                if (f.Length < 2) continue;
                string name = f[0].Trim();
                string url = f[1].Trim();
                if (string.IsNullOrEmpty(name) || seen.Contains(name) || !SmartParser.IsAllowedActionUri(url))
                    continue;
                seen.Add(name);
                var e = new SearchEngineEntry
                {
                    Name = name,
                    Url = url,
                    Pin = f.Length >= 3 && string.Equals(f[2].Trim(), "true", StringComparison.OrdinalIgnoreCase),
                    GlyphHex = (f.Length >= 4 && Regex.IsMatch(f[3].Trim(), @"^[0-9A-Fa-f]{1,6}$")) ? f[3].Trim() : ""
                };
                string fav = EnsureFavicon(e);
                if (!string.IsNullOrEmpty(fav))
                    e.FaviconPath = fav;
                else
                    QueueFavicon(e);
                engines.Add(e);
            }
        }

        ReadFrom(SearchEnginesFile);
        if (File.Exists(LegacyCustomFile))
        {
            int before = engines.Count;
            ReadFrom(LegacyCustomFile);
            try { File.Delete(LegacyCustomFile); } catch { }
            if (engines.Count > before)
                SaveSearchEngines(engines);
        }

        if (engines.Count == 0)
        {
            engines.Add(new SearchEngineEntry
            {
                Name = "Google",
                Url = "https://www.google.com/search?q=",
                Pin = true,
                GlyphHex = "E721"
            });
        }
        return engines;
    }

    public void SaveSearchEngines(IEnumerable<SearchEngineEntry> list = null)
    {
        list ??= SearchEngines;
        var sb = new StringBuilder("; Managed by SparkBar (supports prefix URLs or {selection} anywhere in the URL)\n");
        foreach (var e in list)
        {
            sb.Append(e.Name).Append('\t')
              .Append(e.Url).Append('\t')
              .Append(e.Pin ? "true" : "false");
            if (!string.IsNullOrEmpty(e.GlyphHex))
                sb.Append('\t').Append(e.GlyphHex);
            sb.Append('\n');
        }
        WriteFileAtomic(SearchEnginesFile, sb.ToString());
    }

    public SearchEngineEntry GetDefaultSearchEngine()
    {
        foreach (var e in SearchEngines)
            if (string.Equals(e.Name, DefaultSearchEngine, StringComparison.OrdinalIgnoreCase))
                return e;
        return SearchEngines.Count > 0 ? SearchEngines[0] : null;
    }

    public static string ActionKeyFromTip(string tip)
    {
        if (tip.StartsWith("Search ", StringComparison.OrdinalIgnoreCase)) return "Search engines";
        if (tip.StartsWith("Copy result:", StringComparison.OrdinalIgnoreCase) ||
            tip.StartsWith("Copy conversion:", StringComparison.OrdinalIgnoreCase))
            return "Calculate & convert";
        if (tip.StartsWith("Words:", StringComparison.OrdinalIgnoreCase)) return "Word count";
        return tip;
    }

    public string GetActionDisplay(string tip)
    {
        if (tip.StartsWith("Copy result:", StringComparison.OrdinalIgnoreCase) ||
            tip.StartsWith("Copy conversion:", StringComparison.OrdinalIgnoreCase))
            return "text";
        string key = ActionKeyFromTip(tip);
        return ActionDisplay.TryGetValue(key, out string mode) && string.Equals(mode, "text", StringComparison.OrdinalIgnoreCase)
            ? "text"
            : "icon";
    }

    public bool IsActionEnabled(string tip)
    {
        string key = ActionKeyFromTip(tip);
        return !ActionEnabled.TryGetValue(key, out bool en) || en;
    }

    // ---------------- Favicon Cache ----------------
    public bool TryGetFaviconPaths(SearchEngineEntry e, out string cacheFile, out string host)
    {
        cacheFile = "";
        host = "";
        var m = Regex.Match(e.Url ?? "", @"^https?://(?:[^/@?#]*@)?([A-Za-z0-9.-]+)(?::\d+)?(?:[/?#]|$)", RegexOptions.IgnoreCase);
        if (!m.Success) return false;
        host = m.Groups[1].Value;
        string safe = Regex.Replace(e.Name + "_" + host, @"[^A-Za-z0-9_-]", "_");
        if (safe.Length > 100) safe = safe[..100];
        cacheFile = Path.Combine(FaviconCacheDir, safe + "_hq.png");
        return true;
    }

    public string EnsureFavicon(SearchEngineEntry e)
    {
        return TryGetFaviconPaths(e, out string cacheFile, out _) && IsImageFile(cacheFile) ? cacheFile : "";
    }

    public BitmapSource GetFaviconBitmap(SearchEngineEntry e)
    {
        string path = !string.IsNullOrEmpty(e.FaviconPath) && File.Exists(e.FaviconPath)
            ? e.FaviconPath
            : EnsureFavicon(e);
        if (string.IsNullOrEmpty(path))
        {
            QueueFavicon(e);
            return null;
        }
        e.FaviconPath = path;
        if (_faviconMemoryCache.TryGetValue(path, out var cached))
            return cached;
        try
        {
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.DecodePixelWidth = 32;
            bi.UriSource = new Uri(path, UriKind.Absolute);
            bi.EndInit();
            bi.Freeze();
            _faviconMemoryCache[path] = bi;
            return bi;
        }
        catch { return null; }
    }

    public void ClearFaviconCache()
    {
        _faviconMemoryCache.Clear();
        try { if (Directory.Exists(FaviconCacheDir)) Directory.Delete(FaviconCacheDir, true); } catch { }
        foreach (var e in SearchEngines)
        {
            e.FaviconPath = "";
            QueueFavicon(e);
        }
    }

    public void QueueFavicon(SearchEngineEntry e)
    {
        if (!TryGetFaviconPaths(e, out string cacheFile, out string host) || File.Exists(cacheFile))
            return;
        string failFile = cacheFile + ".fail";
        if (File.Exists(failFile) && (DateTime.UtcNow - File.GetLastWriteTimeUtc(failFile)).TotalDays < 7)
            return;
        if (!_faviconInFlight.TryAdd(cacheFile, 1))
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                Directory.CreateDirectory(FaviconCacheDir);
                var urls = new List<string>();
                if (UseThirdPartyIconServices)
                    urls.Add("https://icon.horse/icon/" + host);
                urls.Add("https://" + host + "/apple-touch-icon.png");
                urls.Add("https://" + host + "/apple-touch-icon-precomposed.png");
                if (UseThirdPartyIconServices)
                    urls.Add("https://www.google.com/s2/favicons?domain=" + host + "&sz=256");

                string tmp = cacheFile + ".tmp";
                foreach (string u in urls)
                {
                    try
                    {
                        byte[] bytes = await Http.GetByteArrayAsync(u).ConfigureAwait(false);
                        if (IsImageBytes(bytes))
                        {
                            await File.WriteAllBytesAsync(tmp, bytes).ConfigureAwait(false);
                            File.Move(tmp, cacheFile, true);
                            e.FaviconPath = cacheFile;
                            return;
                        }
                    }
                    catch { }
                }
                try { await File.WriteAllTextAsync(failFile, "").ConfigureAwait(false); } catch { }
            }
            finally
            {
                _faviconInFlight.TryRemove(cacheFile, out _);
            }
        });
    }

    public static bool IsImageFile(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists || fi.Length < 16) return false;
            Span<byte> b = stackalloc byte[8];
            using var fs = File.OpenRead(path);
            if (fs.Read(b) < 4) return false;
            return (b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47)
                || (b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF)
                || (b[0] == 0x00 && b[1] == 0x00 && b[2] == 0x01 && b[3] == 0x00);
        }
        catch { return false; }
    }

    public static bool IsImageBytes(byte[] b)
    {
        if (b == null || b.Length < 16) return false;
        return (b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47)
            || (b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF)
            || (b[0] == 0x00 && b[1] == 0x00 && b[2] == 0x01 && b[3] == 0x00);
    }

    // ---------------- Themes ----------------
    public static bool IsSystemLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("AppsUseLightTheme") is int v)
                return v == 1;
        }
        catch { }
        return false;
    }

    public ThemePalette UpdatePalette()
    {
        bool light = ThemeMode == "light" || (ThemeMode == "auto" && IsSystemLightTheme());
        if (light)
        {
            Palette = new ThemePalette
            {
                IsLight        = true,
                BgTop          = Hex(0xFF, 0xFF, 0xFF, 0xFF),
                BgBottom       = Hex(0xFF, 0xF8, 0xFA, 0xFC),
                Surface        = Hex(0xFF, 0xF1, 0xF5, 0xF9),
                HoverTop       = Hex(0xFF, 0xE8, 0xF0, 0xFE),
                HoverBottom    = Hex(0xFF, 0xDB, 0xEA, 0xFE),
                HoverBorder    = new(Hex(0xFF, 0x93, 0xC5, 0xFD)),
                Border         = Hex(0xFF, 0xCB, 0xD5, 0xE1),
                Separator      = Hex(0xFF, 0xCB, 0xD5, 0xE1),
                Text           = Hex(0xFF, 0x0F, 0x17, 0x2A),
                SubText        = Hex(0xFF, 0x47, 0x55, 0x69),
                Accent         = Hex(0xFF, 0x25, 0x63, 0xEB),
                AccentBgTop    = Hex(0xFF, 0xEF, 0xF6, 0xFF),
                AccentBgBottom = Hex(0xFF, 0xDB, 0xEA, 0xFE),
                AccentRim      = Hex(0xFF, 0x60, 0xA5, 0xFA),
                AccentText     = Hex(0xFF, 0x1D, 0x4E, 0xD8),
                KeycapBg       = Hex(0xFF, 0xE2, 0xE8, 0xF0),
                KeycapText     = Hex(0xFF, 0x33, 0x41, 0x55),
                PillFill       = Hex(0xFF, 0xF1, 0xF5, 0xF9),
                PillBorder     = Hex(0xFF, 0xCB, 0xD5, 0xE1),
                FaviconTile    = Hex(0xFF, 0xFF, 0xFF, 0xFF)
            };
        }
        else if (ThemeMode == "slate")
        {
            Palette = new ThemePalette
            {
                IsLight        = false,
                BgTop          = Hex(0xFF, 0x1E, 0x22, 0x2D),
                BgBottom       = Hex(0xFF, 0x16, 0x19, 0x22),
                Surface        = Hex(0xFF, 0x22, 0x27, 0x33),
                HoverTop       = Hex(0xFF, 0x2E, 0x35, 0x46),
                HoverBottom    = Hex(0xFF, 0x25, 0x2B, 0x39),
                HoverBorder    = new(Hex(0xFF, 0x3E, 0x46, 0x5E)),
                Border         = Hex(0xFF, 0x3B, 0x44, 0x59),
                Separator      = Hex(0xFF, 0x33, 0x3B, 0x4D),
                Text           = Hex(0xFF, 0xFF, 0xFF, 0xFF),
                SubText        = Hex(0xFF, 0x94, 0xA3, 0xB8),
                Accent         = Hex(0xFF, 0x38, 0xBD, 0xF8),
                AccentBgTop    = Hex(0xFF, 0x17, 0x2B, 0x46),
                AccentBgBottom = Hex(0xFF, 0x12, 0x22, 0x38),
                AccentRim      = Hex(0xFF, 0x3B, 0x82, 0xF6),
                AccentText     = Hex(0xFF, 0x93, 0xC5, 0xFD),
                KeycapBg       = Hex(0xFF, 0x26, 0x2C, 0x3A),
                KeycapText     = Hex(0xFF, 0xCB, 0xD5, 0xE1),
                PillFill       = Hex(0xFF, 0x22, 0x27, 0x33),
                PillBorder     = Hex(0xFF, 0x38, 0x41, 0x52),
                FaviconTile    = Hex(0xFF, 0x22, 0x27, 0x33)
            };
        }
        else
        {
            Palette = new ThemePalette
            {
                IsLight        = false,
                BgTop          = Hex(0xFF, 0x1B, 0x1E, 0x28),
                BgBottom       = Hex(0xFF, 0x12, 0x14, 0x1C),
                Surface        = Hex(0xFF, 0x1B, 0x1E, 0x2B),
                HoverTop       = Hex(0xFF, 0x2A, 0x2F, 0x42),
                HoverBottom    = Hex(0xFF, 0x20, 0x24, 0x34),
                HoverBorder    = new(Hex(0xFF, 0x3E, 0x46, 0x5E)),
                Border         = Hex(0xFF, 0x34, 0x3A, 0x50),
                Separator      = Hex(0xFF, 0x2A, 0x2F, 0x42),
                Text           = Hex(0xFF, 0xF4, 0xF5, 0xF8),
                SubText        = Hex(0xFF, 0x94, 0xA3, 0xB8),
                Accent         = Hex(0xFF, 0x60, 0xA5, 0xFA),
                AccentBgTop    = Hex(0xFF, 0x17, 0x2B, 0x46),
                AccentBgBottom = Hex(0xFF, 0x12, 0x22, 0x38),
                AccentRim      = Hex(0xFF, 0x3B, 0x82, 0xF6),
                AccentText     = Hex(0xFF, 0x93, 0xC5, 0xFD),
                KeycapBg       = Hex(0xFF, 0x20, 0x24, 0x34),
                KeycapText     = Hex(0xFF, 0xCB, 0xD5, 0xE1),
                PillFill       = Hex(0xFF, 0x1B, 0x1E, 0x2B),
                PillBorder     = Hex(0xFF, 0x32, 0x38, 0x4E),
                FaviconTile    = Hex(0xFF, 0x1B, 0x1E, 0x2B)
            };
        }
        return Palette;
    }

    private static Color Hex(byte a, byte r, byte g, byte b) => Color.FromArgb(a, r, g, b);

    public void LogError(Exception ex)
    {
        if (ex == null) return;
        try
        {
            var fi = new FileInfo(ErrorLogFile);
            if (fi.Exists && fi.Length > 262144)
                File.Move(ErrorLogFile, ErrorLogFile + ".old", true);
            string msg = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} | {ex.GetType().Name} | {ex.Message} | {ex.StackTrace?.Replace("\r", "").Replace("\n", " << ")}\n";
            File.AppendAllText(ErrorLogFile, msg, new UTF8Encoding(false));
        }
        catch { }
    }
}
