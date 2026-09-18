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
    public static void Save(RunCardMetrics run, string modVersion, bool complete)
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
                Complete = complete,
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
    /// Recover totals for a run already in progress, or null if there are none.
    ///
    /// Needed because resuming a saved run rebuilds the whole RunState from
    /// disk and mints new CardModel objects, so anything held in memory is
    /// gone. Without this, quitting to the menu mid-run would silently reset a
    /// run's card stats to zero.
    /// </summary>
    public static RunCardMetrics? Load(long startTime)
    {
        string? path = PathFor(startTime);
        if (path == null || !File.Exists(path))
        {
            return null;
        }

        try
        {
            CardStatsFile? file = JsonSerializer.Deserialize<CardStatsFile>(File.ReadAllText(path));
            if (file == null || file.Schema != SchemaVersion || file.StartTime != startTime)
            {
                return null;
            }

            var run = new RunCardMetrics { StartTime = file.StartTime, Seed = file.Seed };
            foreach (CardStatsEntry entry in file.Cards)
            {
                Apply(entry, run.For(new CardKey(entry.Id, entry.UpgradeLevel)));
            }
            Apply(file.Unattributed, run.Unattributed);
            return run;
        }
        catch (Exception exception)
        {
            MainFile.Logger.Error($"[DataExporter] Could not load card stats: {exception}");
            return null;
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
