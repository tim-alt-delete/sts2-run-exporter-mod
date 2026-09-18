using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Godot;
using MegaCrit.Sts2.Core.Saves;

namespace DataExporter.DataExporterCode;

// ---- wire format -----------------------------------------------------------
// Explicit DTOs rather than serialising the in-memory types: CardKey is a
// record struct and would become an unusable dictionary key in JSON, and the
// file is a contract with the dashboard that should not move every time an
// internal field is renamed.

public sealed class CardStatsFile
{
    /// <summary>Bump when the shape changes incompatibly. The dashboard rejects unknown versions.</summary>
    [JsonPropertyName("schema")] public int Schema { get; set; } = SidecarStore.SchemaVersion;

    [JsonPropertyName("mod_version")] public string ModVersion { get; set; } = "";

    /// <summary>Joins to <c>{start_time}.run</c>. The game treats this as the run's primary key.</summary>
    [JsonPropertyName("start_time")] public long StartTime { get; set; }

    [JsonPropertyName("seed")] public string? Seed { get; set; }

    /// <summary>
    /// False until the run actually ended. Abandoning from the main menu never
    /// reaches the mod at all, so a sidecar can legitimately stay incomplete;
    /// the dashboard shows those as partial rather than as final totals.
    /// </summary>
    [JsonPropertyName("complete")] public bool Complete { get; set; }

    [JsonPropertyName("cards")] public List<CardStatsEntry> Cards { get; set; } = new();

    [JsonPropertyName("unattributed")] public CardStatsEntry Unattributed { get; set; } = new();
}

public sealed class CardStatsEntry
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("upgrade_level")] public int UpgradeLevel { get; set; }
    [JsonPropertyName("copies_played")] public int CopiesPlayed { get; set; }
    [JsonPropertyName("damage_unblocked")] public int DamageUnblocked { get; set; }
    [JsonPropertyName("damage_blocked")] public int DamageBlocked { get; set; }
    [JsonPropertyName("overkill")] public int Overkill { get; set; }
    [JsonPropertyName("kills")] public int Kills { get; set; }
    [JsonPropertyName("block_gained")] public int BlockGained { get; set; }
    [JsonPropertyName("energy_spent")] public int EnergySpent { get; set; }
    [JsonPropertyName("energy_gained")] public int EnergyGained { get; set; }
    [JsonPropertyName("cards_drawn")] public int CardsDrawn { get; set; }
}

/// <summary>
/// What happened when a sidecar was read.
///
/// "Not there" and "could not be read" must not be collapsed into one result.
/// The tracker reloads from disk at the start of every combat, so treating a
/// transient read failure as "no data yet" would reset a run's totals to zero
/// and the next flush would overwrite a perfectly good file with them.
/// </summary>
public enum LoadOutcome
{
    /// <summary>No sidecar exists. Correct for the first combat of a run.</summary>
    Missing,

    /// <summary>A sidecar was read and parsed.</summary>
    Loaded,

    /// <summary>A sidecar exists but could not be used. Keep whatever is in memory.</summary>
    Failed,
}

/// <summary>
/// Reads and writes the sidecar file that carries per-card metrics out of the
/// game.
///
/// It is written into the same <c>saves/history/</c> folder as the game's own
/// <c>{start_time}.run</c>, so uploading a save folder to the dashboard picks
/// it up with no extra instruction from the user.
/// </summary>
public static class SidecarStore
{
    public const int SchemaVersion = 1;

    private const string Extension = ".cardstats.json";

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    /// <summary>
    /// Absolute path for a run's sidecar, or null if the save system is not
    /// ready (no profile chosen yet).
    ///
    /// Goes through SaveManager.GetProfileScopedPath rather than building the
    /// path by hand, which means it automatically lands in the <c>modded/</c>
    /// tree: loading any mod flips UserDataPathProvider.IsRunningModded, and
    /// GetProfileDir prefixes accordingly. Note OS.GetUserDataDir() is *not*
    /// the right base — that is the Godot user root, not the profile's saves.
    /// </summary>
    public static string? PathFor(long startTime)
    {
        try
        {
            string saves = ProjectSettings.GlobalizePath(
                SaveManager.Instance.GetProfileScopedPath("saves"));
            return Path.Combine(saves, "history", $"{startTime}{Extension}");
        }
        catch (Exception exception)
        {
            MainFile.Logger.Error($"[DataExporter] Could not resolve save path: {exception}");
            return null;
        }
    }

    /// <summary>
    /// Write the run's totals. Never throws: a failed write must not interrupt
    /// a run, and the data is re-derivable on the next flush anyway.
    /// </summary>
    public static void Save(RunCardMetrics run, string modVersion)
    {
        if (run.StartTime <= 0)
        {
            return;   // no run identity yet, so nothing that could be joined
        }
        string? path = PathFor(run.StartTime);
        if (path == null)
        {
            return;
        }

        try
        {
            var file = new CardStatsFile
            {
                ModVersion = modVersion,
                StartTime = run.StartTime,
                Seed = run.Seed,
                Complete = run.IsComplete,
                Unattributed = ToEntry(new CardKey("", 0), run.Unattributed),
            };
            foreach (KeyValuePair<CardKey, CardTotals> pair in run.Cards)
            {
                if (!pair.Value.IsEmpty)
                {
                    file.Cards.Add(ToEntry(pair.Key, pair.Value));
                }
            }
            // Stable order so re-flushing an unchanged run produces an
            // identical file, which makes diffing two uploads meaningful.
            file.Cards.Sort(static (a, b) =>
            {
                int byId = string.CompareOrdinal(a.Id, b.Id);
                return byId != 0 ? byId : a.UpgradeLevel.CompareTo(b.UpgradeLevel);
            });

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            // Write-then-rename. A crash partway through a direct write would
            // leave truncated JSON that the dashboard would reject for the rest
            // of the run; rename is atomic, so the file is either the old one
            // or the new one.
            string temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(file, Options));
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception exception)
        {
            MainFile.Logger.Error($"[DataExporter] Could not save card stats: {exception}");
        }
    }

    /// <summary>
    /// Recover totals for a run already in progress.
    ///
    /// Called at the start of every combat, not just when the run changes.
    /// Anything accumulated but not yet written is deliberately dropped: the
    /// game restarts a combat you quit out of, and replaying it would otherwise
    /// count those cards twice. Disk is the only committed state.
    ///
    /// Resuming also rebuilds the whole RunState from the save file and mints
    /// new CardModel objects, so nothing in memory survives a real reload
    /// anyway.
    /// </summary>
    public static LoadOutcome Load(long startTime, out RunCardMetrics? run)
    {
        run = null;
        string? path = PathFor(startTime);
        if (path == null)
        {
            return LoadOutcome.Failed;
        }
        if (!File.Exists(path))
        {
            return LoadOutcome.Missing;
        }

        try
        {
            CardStatsFile? file = JsonSerializer.Deserialize<CardStatsFile>(File.ReadAllText(path));
            if (file == null || file.Schema != SchemaVersion || file.StartTime != startTime)
            {
                MainFile.Logger.Error(
                    $"[DataExporter] Ignoring unusable card stats at {path}.");
                return LoadOutcome.Failed;
            }

            var loaded = new RunCardMetrics
            {
                StartTime = file.StartTime,
                Seed = file.Seed,
                IsComplete = file.Complete,
            };
            foreach (CardStatsEntry entry in file.Cards)
            {
                Apply(entry, loaded.For(new CardKey(entry.Id, entry.UpgradeLevel)));
            }
            Apply(file.Unattributed, loaded.Unattributed);
            run = loaded;
            return LoadOutcome.Loaded;
        }
        catch (Exception exception)
        {
            MainFile.Logger.Error($"[DataExporter] Could not load card stats: {exception}");
            return LoadOutcome.Failed;
        }
    }

    private static CardStatsEntry ToEntry(CardKey key, CardTotals totals) => new()
    {
        Id = key.Id,
        UpgradeLevel = key.UpgradeLevel,
        CopiesPlayed = totals.CopiesPlayed,
        DamageUnblocked = totals.DamageUnblocked,
        DamageBlocked = totals.DamageBlocked,
        Overkill = totals.Overkill,
        Kills = totals.Kills,
        BlockGained = totals.BlockGained,
        EnergySpent = totals.EnergySpent,
        EnergyGained = totals.EnergyGained,
        CardsDrawn = totals.CardsDrawn,
    };

    private static void Apply(CardStatsEntry entry, CardTotals totals)
    {
        totals.CopiesPlayed = entry.CopiesPlayed;
        totals.DamageUnblocked = entry.DamageUnblocked;
        totals.DamageBlocked = entry.DamageBlocked;
        totals.Overkill = entry.Overkill;
        totals.Kills = entry.Kills;
        totals.BlockGained = entry.BlockGained;
        totals.EnergySpent = entry.EnergySpent;
        totals.EnergyGained = entry.EnergyGained;
        totals.CardsDrawn = entry.CardsDrawn;
    }
}
