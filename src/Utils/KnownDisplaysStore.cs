using System.IO;
using System.Text.Json;
using BrightnessTrayAppWpf.Models;

namespace BrightnessTrayAppWpf.Utils;

/// <summary>
/// Persistent registry of every unique display the app has ever enumerated, keyed by EDIDKey.
/// Extracted from <see cref="AppSettings.KnownDisplays"/> so monitor enumeration no longer drags
/// the entire settings XML through a write on every refresh;
/// the registry grows without bound (disconnected monitors are never removed)
/// and was the largest contributor to settings-file churn.
///
/// Entries themselves are still <see cref="KnownDisplayEntry"/> instances so the type is shared with the legacy
/// XML element; only the persistence path differs.
/// </summary>
public sealed class KnownDisplaysStore(string path)
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true,
    };

    private readonly Lock _gate = new();
    private List<KnownDisplayEntry> _entries = [];

    public KnownDisplaysStore() : this(GetDefaultPath()) { }

    /// <summary>
    /// Snapshot of the current entries.
    /// Safe to enumerate without holding the store's internal lock -
    /// mutations replace the list reference rather than mutating in place when the file is reloaded.
    /// </summary>
    public IReadOnlyList<KnownDisplayEntry> Entries
    {
        get
        {
            lock (_gate) return _entries.ToList();
        }
    }

    /// <summary>
    /// Path of the JSON file. Sits next to settings.xml under %LocalAppData%\&lt;app&gt;\.
    /// </summary>
    public static string GetDefaultPath()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string appFolder = Path.Combine(appData, Program.ApplicationName);
        Directory.CreateDirectory(appFolder);
        return Path.Combine(appFolder, "displays.json");
    }

    /// <summary>
    /// Loads <c>displays.json</c> if present.
    /// First-run migration: when the JSON file is absent and <paramref name="legacy"/> contains entries
    /// (still living inside settings.xml from before this extraction),
    /// copies them across and writes the JSON file so subsequent loads find it on disk.
    /// </summary>
    public void Load(IEnumerable<KnownDisplayEntry>? legacy = null)
    {
        lock (_gate)
        {
            if (TryReadFromDisk(out List<KnownDisplayEntry> loaded))
            {
                _entries = loaded;
                return;
            }

            // First run after extraction: migrate from AppSettings.KnownDisplays so users upgrading
            // from an older build don't lose their accumulated history
            // (and, crucially, their sticky WasEverDDCCapable flags -
            // DDCRecoveryService keys candidate selection off those).
            List<KnownDisplayEntry> seed = legacy?
                .Where(e => !string.IsNullOrEmpty(e.EDIDKey))
                .Select(Clone)
                .ToList() ?? [];

            _entries = seed;

            if (seed.Count > 0) SaveLocked();
        }
    }

    /// <summary>
    /// Adds a new entry by EDIDKey if absent;
    /// otherwise refreshes <c>OriginalName</c> and <c>EDIDSerial</c> on the existing entry
    /// from non-empty values in <paramref name="entry"/>.
    /// Returns true when the in-memory list actually changed
    /// (caller may use this to decide whether to <see cref="Save"/>;
    /// <see cref="RegisterMany"/> already auto-saves).
    /// </summary>
    public bool Register(KnownDisplayEntry? entry)
    {
        if (entry == null) return false;

        if (string.IsNullOrEmpty(entry.EDIDKey)) return false;

        lock (_gate) return RegisterLocked(entry);
    }

    /// <summary>
    /// Bulk-register variant. Saves once at the end if anything changed.
    /// </summary>
    public void RegisterMany(IEnumerable<KnownDisplayEntry>? entries)
    {
        if (entries == null) return;

        bool changed = false;
        lock (_gate)
        {
            foreach (KnownDisplayEntry e in entries)
            {
                if (string.IsNullOrEmpty(e.EDIDKey)) continue;

                if (RegisterLocked(e)) changed = true;
            }

            if (changed) SaveLocked();
        }
    }

    /// <summary>
    /// Stamps <c>WasEverDDCCapable = true</c> for the entry matching <paramref name="edidKey"/>.
    /// No-op if the key is unknown or the flag is already set. Saves on transition.
    /// Returns true when a flag actually flipped.
    /// </summary>
    public bool MarkDDCCapable(string edidKey)
    {
        if (string.IsNullOrEmpty(edidKey)) return false;

        lock (_gate)
        {
            KnownDisplayEntry? entry = _entries.FirstOrDefault(e => e.EDIDKey == edidKey);
            if (entry == null) return false;

            if (entry.WasEverDDCCapable) return false;

            entry.WasEverDDCCapable = true;
            SaveLocked();
            return true;
        }
    }

    /// <summary>
    /// Persists the current in-memory list to JSON.
    /// </summary>
    public void Save()
    {
        lock (_gate) SaveLocked();
    }

    /// <summary>
    /// Returns the entry for <paramref name="edidKey"/>, or null.
    /// Returned object is the live instance; callers must not mutate fields they don't own.
    /// </summary>
    public KnownDisplayEntry? Find(string edidKey)
    {
        if (string.IsNullOrEmpty(edidKey)) return null;

        lock (_gate) return _entries.FirstOrDefault(e => e.EDIDKey == edidKey);
    }

    private bool RegisterLocked(KnownDisplayEntry incoming)
    {
        KnownDisplayEntry? existing = _entries.FirstOrDefault(e => e.EDIDKey == incoming.EDIDKey);
        if (existing == null)
        {
            _entries.Add(new KnownDisplayEntry
            {
                EDIDKey = incoming.EDIDKey,
                OriginalName = incoming.OriginalName,
                EDIDSerial = incoming.EDIDSerial,
                WasEverDDCCapable = incoming.WasEverDDCCapable,
            });
            return true;
        }

        bool changed = false;
        if (!string.IsNullOrWhiteSpace(incoming.OriginalName)
            && existing.OriginalName != incoming.OriginalName)
        {
            existing.OriginalName = incoming.OriginalName;
            changed = true;
        }
        if (!string.IsNullOrWhiteSpace(incoming.EDIDSerial)
            && existing.EDIDSerial != incoming.EDIDSerial)
        {
            existing.EDIDSerial = incoming.EDIDSerial;
            changed = true;
        }
        if (incoming.WasEverDDCCapable && !existing.WasEverDDCCapable)
        {
            existing.WasEverDDCCapable = true;
            changed = true;
        }
        return changed;
    }

    private bool TryReadFromDisk(out List<KnownDisplayEntry> loaded)
    {
        loaded = [];
        try
        {
            if (!File.Exists(path)) return false;

            string json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json)) return false;

            List<KnownDisplayEntry>? parsed = JsonSerializer.Deserialize<List<KnownDisplayEntry>>(json, s_jsonOptions);
            loaded = parsed?.Where(e => !string.IsNullOrEmpty(e.EDIDKey)).ToList() ?? [];
            return true;
        }
        catch (Exception ex)
        {
            WpfLog.Log($"KnownDisplaysStore: load failed ({path}): {ex.Message}");
            return false;
        }
    }

    private void SaveLocked()
    {
        try
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            string json = JsonSerializer.Serialize(_entries, s_jsonOptions);
            File.WriteAllText(path, json);
        }
        catch (Exception ex)
        {
            WpfLog.Log($"KnownDisplaysStore: save failed ({path}): {ex.Message}");
        }
    }

    private static KnownDisplayEntry Clone(KnownDisplayEntry src) => new()
    {
        EDIDKey = src.EDIDKey,
        OriginalName = src.OriginalName,
        EDIDSerial = src.EDIDSerial,
        WasEverDDCCapable = src.WasEverDDCCapable,
    };
}
