using System.Collections.Generic;

namespace DataExporter.DataExporterCode;

/// <summary>
/// Identifies a card for aggregation purposes.
///
/// Duplicates are deliberately indistinguishable: five Strikes in a deck share
/// one entry, and a per-copy average is <c>total / copies_played</c>. The game
/// offers no per-instance card id anyway (see docs/PLAN.md, "Card identity"),
/// so anything finer would have to be invented and persisted.
///
/// Upgrade level is part of the key because an upgraded card has different
/// numbers. Merging Strike with Strike+ produces an average that describes
/// neither.
/// </summary>
/// <param name="Id">Full model id, e.g. <c>CARD.STRIKE</c>.</param>
/// <param name="UpgradeLevel">0 for unupgraded. Some cards upgrade past 1.</param>
public readonly record struct CardKey(string Id, int UpgradeLevel);

/// <summary>
/// Running totals for one <see cref="CardKey"/> over a whole run.
///
/// Everything is a simple sum. The dashboard divides to get per-play averages,
/// so nothing here needs to know how many copies exist.
/// </summary>
public sealed class CardTotals
{
    /// <summary>Number of individual card plays, not number of copies owned.</summary>
    public int CopiesPlayed { get; set; }

    /// <summary>Damage that got through the target's block.</summary>
    public int DamageUnblocked { get; set; }

    /// <summary>Damage the target's block absorbed. Not wasted, just not HP loss.</summary>
    public int DamageBlocked { get; set; }

    /// <summary>Damage dealt past 0 HP. High values suggest overkill, not value.</summary>
    public int Overkill { get; set; }

    /// <summary>Targets that died to this damage.</summary>
    public int Kills { get; set; }

    /// <summary>Block gained by the local player.</summary>
    public int BlockGained { get; set; }

    /// <summary>Energy paid to play it. Zero for auto-plays, which cost nothing.</summary>
    public int EnergySpent { get; set; }

    /// <summary>Energy the card handed back while it was resolving.</summary>
    public int EnergyGained { get; set; }

    /// <summary>Cards drawn while it was resolving. Excludes the start-of-turn hand draw.</summary>
    public int CardsDrawn { get; set; }

    public bool IsEmpty =>
        CopiesPlayed == 0 && DamageUnblocked == 0 && DamageBlocked == 0 && Overkill == 0 &&
        Kills == 0 && BlockGained == 0 && EnergySpent == 0 && EnergyGained == 0 && CardsDrawn == 0;
}

/// <summary>
/// Everything recorded for one run, and the shape written to the sidecar file.
/// </summary>
public sealed class RunCardMetrics
{
    /// <summary>Unix epoch seconds. Matches the game's own <c>{start_time}.run</c> filename.</summary>
    public long StartTime { get; set; }

    /// <summary>The run's seed string, carried so the dashboard can sanity-check the join.</summary>
    public string? Seed { get; set; }

    public Dictionary<CardKey, CardTotals> Cards { get; } = new();

    /// <summary>
    /// Outgoing damage and block that no card could be blamed for: Poison ticks,
    /// Thorns, relic procs. <see cref="MegaCrit.Sts2.Core.Models.Powers.PoisonPower"/>
    /// deals damage with both dealer and cardSource null, so there is genuinely
    /// nothing to attribute it to.
    ///
    /// This exists so the size of the gap is visible. Without it, a deck built
    /// around damage-over-time would silently report almost no damage and the
    /// per-card comparison would quietly be a lie.
    /// </summary>
    public CardTotals Unattributed { get; } = new();

    public CardTotals For(CardKey key)
    {
        if (!Cards.TryGetValue(key, out CardTotals? totals))
        {
            totals = new CardTotals();
            Cards[key] = totals;
        }
        return totals;
    }

    public void Clear()
    {
        Cards.Clear();
        StartTime = 0;
        Seed = null;
        Unattributed.CopiesPlayed = 0;
        Unattributed.DamageUnblocked = 0;
        Unattributed.DamageBlocked = 0;
        Unattributed.Overkill = 0;
        Unattributed.Kills = 0;
        Unattributed.BlockGained = 0;
        Unattributed.EnergySpent = 0;
        Unattributed.EnergyGained = 0;
        Unattributed.CardsDrawn = 0;
    }
}
