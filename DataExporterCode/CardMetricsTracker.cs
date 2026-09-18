using System.Collections.Generic;
using System.Threading.Tasks;
using BaseLib.Abstracts;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.ValueProps;

namespace DataExporter.DataExporterCode;

/// <summary>
/// Records per-card utility metrics for the local player, for a whole run.
///
/// The game constructs this, not us: ModelDb.Init walks every AbstractModel
/// subtype including those in mods and calls Activator.CreateInstance on each
/// (ModelDb.cs:389). Hence the public parameterless constructor, and hence
/// never calling `new CardMetricsTracker()` — that throws DuplicateModelException.
/// Get the instance with <see cref="ModelDb.Singleton{T}"/>.
///
/// Because it is created once at startup rather than once per run, per-run
/// state must be reset explicitly. See <see cref="BeginRun"/>.
///
/// HookType.Combat, never Combat and Run. RunState yields its own subscribers
/// and then delegates to the combat state's (RunState.cs:584-595), so a model
/// receiving both is dispatched twice for the 33 hooks that go through
/// runState.IterateHookListeners(combatState) — silently doubling every number.
/// Combat-only still receives AfterCombatEnd, via that same delegation.
/// </summary>
public sealed class CardMetricsTracker : CustomSingletonModel
{
    public CardMetricsTracker() : base(HookType.Combat)
    {
    }

    /// <summary>
    /// Card plays currently resolving, innermost last.
    ///
    /// A stack rather than a single field because auto-play effects (Mayhem,
    /// Hellraiser) play a card from inside another card's OnPlay, so plays
    /// nest. Draws and energy gains belong to whatever is on top.
    ///
    /// Brackets can be left open: CardModel.cs:1932-1963 returns early when the
    /// owner dies, skipping AfterCardPlayed. Cleared at combat start rather
    /// than assumed balanced.
    /// </summary>
    private readonly List<CardPlay> _inFlight = new();

    private RunCardMetrics _run = new();

    /// <summary>Totals so far this run. Never null.</summary>
    public RunCardMetrics Current => _run;

    /// <summary>Set when something has been recorded since the last flush.</summary>
    public bool IsDirty { get; private set; }

    /// <summary>
    /// Start recording a fresh run, or adopt totals recovered from a sidecar
    /// after the player resumed a saved run.
    /// </summary>
    public void BeginRun(RunCardMetrics run)
    {
        _run = run;
        _inFlight.Clear();
        IsDirty = false;
    }

    public void MarkFlushed() => IsDirty = false;

    // ---- attribution helpers -------------------------------------------------

    /// <summary>
    /// Is this the local player? Metrics are single-player-scoped: in
    /// multiplayer, blending both players' cards would make the per-card
    /// averages meaningless.
    /// </summary>
    private static bool IsLocal(Player? player) =>
        player != null && LocalContext.NetId.HasValue && player.NetId == LocalContext.NetId.Value;

    /// <summary>
    /// The owner of a card, or null if it cannot be read.
    ///
    /// CardModel.Owner calls AssertMutable and throws on canonical models
    /// (CardModel.cs:333). Live combat cards are always mutable, but a hook
    /// throwing would take down combat, and no metric is worth that.
    /// </summary>
    private static Player? OwnerOf(CardModel? card) =>
        card is { IsMutable: true } ? card.Owner : null;

    private static CardKey KeyFor(CardModel card) =>
        new(card.Id.ToString(), card.CurrentUpgradeLevel);

    /// <summary>The card play currently resolving, or null if none is.</summary>
    private CardPlay? Innermost =>
        _inFlight.Count > 0 ? _inFlight[_inFlight.Count - 1] : null;

    // ---- hooks ---------------------------------------------------------------

    public override Task BeforeCombatStart()
    {
        // Nothing should still be in flight between combats. If something is,
        // an owner died mid-play and its AfterCardPlayed never ran.
        _inFlight.Clear();
        MainFile.EnsureRun(this);
        return Task.CompletedTask;
    }

    public override Task BeforeCardPlayed(CardPlay cardPlay)
    {
        if (IsLocal(OwnerOf(cardPlay.Card)))
        {
            _inFlight.Add(cardPlay);
        }
        return Task.CompletedTask;
    }

    public override Task AfterCardPlayed(PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        int index = _inFlight.LastIndexOf(cardPlay);
        if (index < 0)
        {
            return Task.CompletedTask;   // not ours, or never pushed
        }

        // Drop this play and anything nested inside it that never closed.
        _inFlight.RemoveRange(index, _inFlight.Count - index);

        CardTotals totals = _run.For(KeyFor(cardPlay.Card));
        totals.CopiesPlayed++;
        // Resources.EnergySpent, not EnergyValue: an auto-played 3-cost card
        // has EnergyValue 3 and EnergySpent 0 (ResourceInfo.cs).
        totals.EnergySpent += cardPlay.Resources.EnergySpent;
        IsDirty = true;
        return Task.CompletedTask;
    }

    public override Task AfterDamageGiven(
        PlayerChoiceContext choiceContext, Creature? dealer, DamageResult result,
        ValueProp props, Creature target, CardModel? cardSource)
    {
        // Outgoing damage only. Damage the player receives is not a card's doing.
        if (target.IsPlayer)
        {
            return Task.CompletedTask;
        }

        CardTotals totals;
        if (cardSource != null)
        {
            Player? owner = OwnerOf(cardSource);
            if (!IsLocal(owner))
            {
                return Task.CompletedTask;   // another player's card
            }
            totals = _run.For(KeyFor(cardSource));
        }
        else if (IsLocal(dealer?.Player) || dealer == null)
        {
            // No card to blame. Thorns names the player as dealer; PoisonPower
            // passes null for both dealer and cardSource (PoisonPower.cs:64),
            // so null is counted rather than dropped, at the cost of also
            // catching another player's poison in multiplayer.
            totals = _run.Unattributed;
        }
        else
        {
            return Task.CompletedTask;   // a monster dealing damage to a monster
        }

        totals.DamageUnblocked += result.UnblockedDamage;
        totals.DamageBlocked += result.BlockedDamage;
        totals.Overkill += result.OverkillDamage;
        if (result.WasTargetKilled)
        {
            totals.Kills++;
        }
        IsDirty = true;
        return Task.CompletedTask;
    }

    public override Task AfterBlockGained(
        Creature creature, decimal amount, ValueProp props, CardModel? cardSource)
    {
        if (!IsLocal(creature.Player))
        {
            return Task.CompletedTask;
        }

        CardTotals totals;
        if (cardSource != null)
        {
            if (!IsLocal(OwnerOf(cardSource)))
            {
                return Task.CompletedTask;
            }
            totals = _run.For(KeyFor(cardSource));
        }
        else
        {
            totals = _run.Unattributed;   // relic or power granted it
        }

        totals.BlockGained += (int)amount;
        IsDirty = true;
        return Task.CompletedTask;
    }

    public override Task AfterCardDrawn(
        PlayerChoiceContext choiceContext, CardModel card, bool fromHandDraw)
    {
        // The draw hook carries no cause, so the in-flight play is the cause.
        // fromHandDraw is the start-of-turn hand, which no card is responsible
        // for; a draw with nothing in flight is likewise not a card's doing.
        if (fromHandDraw)
        {
            return Task.CompletedTask;
        }
        CardPlay? cause = Innermost;
        if (cause == null)
        {
            return Task.CompletedTask;
        }

        _run.For(KeyFor(cause.Card)).CardsDrawn++;
        IsDirty = true;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Read-only observer. There is no AfterEnergyGained hook, and this is the
    /// only place the gained amount is visible to a listener, so the modifier
    /// hook is used to watch it. The value is returned untouched — this must
    /// never change gameplay.
    /// </summary>
    public override decimal ModifyEnergyGain(Player player, decimal amount)
    {
        if (IsLocal(player) && amount > 0)
        {
            CardPlay? cause = Innermost;
            if (cause != null)
            {
                // Nothing in flight means turn-start energy, which is not a
                // card's doing, so it is dropped rather than bucketed.
                _run.For(KeyFor(cause.Card)).EnergyGained += (int)amount;
                IsDirty = true;
            }
        }
        return amount;
    }

    public override Task AfterCombatEnd(CombatRoom room)
    {
        // No flush here. The game saves the run a few lines after dispatching
        // this hook (CombatManager.cs:985 then :1008), and writing first left a
        // window where a crash in between committed a combat the game had not.
        // MainFile flushes on SaveManager.Saved instead, so the two files move
        // together.
        _inFlight.Clear();
        return Task.CompletedTask;
    }
}
