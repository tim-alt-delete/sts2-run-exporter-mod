# Findings — the state model, and the bugs that came out of getting it wrong

Companion to [PLAN.md](PLAN.md). PLAN.md records *what the game does*, with
file:line citations. This file records *how to reason about it*: the mental
model the mod is built on, the three bugs that came from holding a wrong version
of that model, and the rules that fall out of them.

Read this before changing anything about when the mod reads or writes state.
Every bug so far has been in that seam, not in the metric collection.

**Status:** the model below was confirmed in a real game on 2026-09-18. The
replay double-count that motivated it is gone. See §2.1.

---

## 1. The central idea: two timelines that must not diverge

The mod maintains a running tally. The game maintains a run. Both persist to
disk. **They are two independent timelines, and every bug so far has been them
getting out of step.**

The game's timeline is authoritative and the mod cannot influence it. So the
only workable design is for the mod's timeline to be *slaved* to the game's:
whenever the game commits, the mod commits; whatever the game discards, the mod
discards.

State is therefore in exactly one of two conditions, and confusing them is the
whole bug class:

| | Where it lives | Survives what |
|---|---|---|
| **Committed** | `{start_time}.cardstats.json` on disk | anything |
| **Provisional** | the tracker's in-memory `RunCardMetrics` | nothing you can rely on |

Provisional state is not "recent" state. It is state that **may never have
happened** from the game's point of view, because the player can rewind the game
to its last commit at any moment by quitting.

### The two facts that make this counter-intuitive

Both verified in the decompiled source, both easy to assume wrongly:

1. **"Save and Quit" does not save.** It returns to the main menu without
   calling `SaveRun`, and does not exit the process. Your run is restored from
   whatever was written when you entered the current map point.
2. **The game never saves during combat.** `SaveRun` has four call sites: run
   start, travelling to a map point, event rooms, and combat *end*. There is
   also nowhere to put combat state — `CombatRoom.ToSerializable` carries no
   piles, no monster HP, no turn number.

Together: **a combat you quit out of always restarts from the beginning, and the
game has no memory of your first attempt.** If the mod does, it is wrong.

The mod's own process surviving a quit-to-menu is what makes this dangerous. It
is not an obvious "reload" boundary — nothing is torn down, so stale state
quietly persists across what the player experiences as a restart.

---

## 2. The three bugs

All three are the same mistake wearing different clothes: **treating provisional
state as committed.**

### 2.1 Replayed card plays counted twice

*Found by playing. The only one a user would ever notice. **Fixed and confirmed
fixed in a real game.***

`EnsureRun` reloaded from disk only when `start_time` changed. `start_time` is
stable for a run's whole life, including across a resume — so resuming never
triggered a reload, and the tracker kept plays from a combat attempt the game
had already thrown away. Replaying the combat counted them again.

**Why it was missed:** the original design reasoned "reload when the run
changes", which is the right rule for a *different* question (don't let run A's
totals leak into run B). It answers run identity, not commit state. Two
different questions, one check.

**The fix is a reframing, not a patch.** Stop asking "is this a new run?" and
start asking "what has actually been committed?" — which makes the answer
unconditional: reload from disk at the start of every combat, always.

**What the confirmation bought.** Re-testing the exact scenario — play cards,
Save and Quit mid-combat, resume, replay the combat — no longer inflates
`copies_played`. That validates more than the one fix: it is direct evidence
that combat start is the right discard boundary and that `SaveManager.Saved` is
the right commit point. Had either been wrong, the count would still have
doubled. The model in §1 can now be treated as established rather than inferred.

### 2.2 `complete` was never `true`

*Found by reading the code, not by playing.*

`FlushRun(tracker, bool complete = false)` had a default, and nothing ever
passed `true`. Every sidecar ever written claimed its run was unfinished.

**Why it was missed:** the flag was designed before the writer was, and the
writer was built against the flush path (combat end), which is a place where the
run is by definition *not* complete. Nothing connected the two. A default
parameter made the gap silent — with a required parameter the compiler would
have asked.

**Second-order finding:** fixing it revealed that dying loses the fatal combat
entirely. Death reaches `RunManager.OnEnded` via `CreatureCmd.Kill` with no
intervening `SaveRun`, so without a run-end write the last combat — often the
most interesting one — was never committed. A dead flag was hiding real data
loss.

### 2.3 A finished run could be reopened as unfinished

*Found by reasoning while fixing the other two. Never observed.*

Once flushing moved to `SaveManager.Saved`, this became reachable: finishing run
A marks it complete, then starting run B saves at character select and fires
`Saved` — while the tracker still points at A. A's finished file gets rewritten
with `complete: false`.

**Why it is worth recording despite never happening:** it was found by walking
every lifecycle path against the new design rather than by testing the happy
one. The window is small and the symptom (a completed run quietly marked
partial) is one nobody would connect back to starting a new run. It would have
lived for a long time.

---

## 3. Rules that fall out of this

Applied to any future change in this area.

1. **Disk is the only committed state.** In-memory totals are provisional until
   the *game* has saved. Never persist anything on a schedule of the mod's own
   choosing.
2. **Commit when the game commits.** `SaveManager.Saved` fires after the game's
   run save reaches disk. Writing earlier — as the old `AfterCombatEnd` flush
   did, 23 lines ahead of `SaveRun` — creates a window where the mod has
   recorded something the game has not.
3. **Discard at every restart boundary, not every identity change.** Combat
   start is the restart boundary. Run change is a different question.
4. **Distinguish "absent" from "broken".** Reloading on every combat made
   `Load`'s failure modes load-bearing: "no file yet" must start fresh, but
   "file unreadable" must *not*, or one failed read zeroes a run and the next
   write makes it permanent. Losing real data is worse than a rare double-count.
5. **Terminal states must be terminal.** A run marked complete refuses further
   writes. Without that, any later write path can reopen it.
6. **Patches and hooks must never throw.** A hook that throws takes combat with
   it; a postfix that throws takes the end of the run. Every entry point from
   the game is wrapped. No metric is worth a crash.
7. **Prefer required parameters over defaults for correctness-critical flags.**
   Bug 2.2 was a default silently doing the wrong thing forever.

---

## 4. On verification, and what it cannot tell you

Worth being honest about, because the gap is wide.

**What was verified here:** compilation against the real game assemblies; the
accumulator and wire format exercised directly; the emitted JSON accepted,
stored and rendered by the dashboard, joined against real archived decks; the
dashboard suite end to end.

**What none of that could catch:** all three bugs above. Every one lives in the
interaction between the mod's lifecycle and the game's, and there is no way to
exercise that without a running game.

So: **"all checks passed" means the plumbing is self-consistent. It does not
mean the mod is correct.** The checklist in [TESTING.md](TESTING.md) §4 is the
part that establishes correctness, and it can only be run by a human playing.

This is not a gap to be closed by writing more tests here. It is a property of
modding a closed-source game with no headless mode, and the right response is to
keep the runtime checklist short, specific, and actually run after each change.

**This played out exactly as described.** The automated checks passed before the
bugs existed, while the bugs existed, and after they were fixed — they never
moved. Both the discovery of 2.1 and the confirmation of its fix came from a
human playing the game and reporting what happened. Budget for that round trip;
it is not optional, and it is the only step that has ever changed the answer.

### A note on how bug 2.1 got diagnosed

The user reported the symptom and, when given an explanation, pushed back:
*"I know for a fact I saved and quit mid-combat, so your assumption is wrong."*

The explanation happened to be right, but the correct response was not to
restate it — it was to go back to the source and produce the evidence
(`NPauseMenu.cs:301-320`, the four `SaveRun` call sites, `CombatRoom.ToSerializable`),
then ask for the one observable that would discriminate: *did the combat restart
from the beginning, or resume where you left off?*

That question settles it in one round trip, costs nothing if the model was
right, and prevents fixing the wrong thing if it was wrong. **When a user's
account of behaviour conflicts with your model of it, find the cheapest
observable that distinguishes them.** Arguing from memory is how you ship a
confident wrong fix.

---

## 5. Where the next bug probably is

Ranked by likelihood.

1. **Multiplayer.** Everything is filtered to the local player, but the "damage
   with no dealer and no card source" bucket will catch another player's Poison.
   Untested; nobody has run this in multiplayer.
2. **An unknown `SaveRun` call site during combat.** The whole commit-in-lockstep
   design assumes there is none. Four are known and none are in combat. One
   replay test has now passed, which is evidence for the assumption but not
   proof — a call site on a path that test did not cross would still be
   invisible. If inflation ever reappears, check this first.
3. **Powers and status effects.** The largest *known* gap, but it under-reports
   rather than corrupting — see PLAN.md, Future work.
4. **Card plays that never close.** `CardModel.cs` returns early when the owner
   dies mid-play, so `AfterCardPlayed` is skipped and the in-flight stack is left
   open. It is cleared at combat start, but a long chain of nested auto-plays
   ending in death has never been exercised.
5. **A game update renaming a hook.** The mod uses the sanctioned subscription
   API rather than patching, so this is lower risk than it would otherwise be —
   but `RunManager.OnEnded` *is* patched, and that is the fragile part.

---

## 6. Things that look like bugs and are not

Recorded so they do not get "fixed".

- **Damage lower than the card's printed value.** `damage_unblocked` is damage
  actually dealt, after every modifier. A Bash under Shrink reporting 10 across
  two plays instead of 16 is correct. "Was this card good *for this run*" wants
  what happened, not the card's face value.
- **A killing blow reporting less than full damage.** It is clamped to the
  target's remaining HP, with the remainder in `overkill`.
- **The mod absent from BaseLib's mod settings screen.** That screen lists mods
  which register configuration. This one has none. The log line and the sidecar
  are the signals that it loaded.
- **`complete: false` on an abandoned run.** Abandoning from the main menu
  bypasses `RunManager` entirely and no mod code runs. The flag is accurate.
- **Saves moving to a `modded/` folder.** Any loaded mod triggers this,
  regardless of `affects_gameplay`. Game behaviour, not ours.

---

## 7. BaseLib changes the on-disk run format

Worth knowing for anything that consumes `.run` files, including the dashboard.

Loading BaseLib — which this mod requires — makes the game write

```json
"save_dict_List[BaseLib.Abstracts.CardModifier+ModifierSave]": {
  "BaseLibCardModifiers": []
}
```

onto **every card**, in `players[].deck` and in every `card_choices` entry. It
appeared 140 times in the first modded run recorded, every one an empty
placeholder, because that run used no card modifiers. The key is a C# generic
type name, so it contains dots, and `+` for the nested type.

This broke dashboard uploads outright. Its field-name guard rejected any key
containing a dot, so **every run recorded with this mod installed was refused**,
with an error the user could do nothing about. Fixed on the dashboard side by
guarding only what BSON actually rejects — see `dashboard/docs/PLAN.md`, Field
names.

The general lesson, and the reason this is in FINDINGS rather than a footnote:
**adding a dependency changed the format of data produced by a system neither we
nor the dependency owns.** Nothing in the mod writes that key, nothing in the
mod reads it, and it surfaced two repos away. When adding a mod dependency, ask
what it does to the game's own save files, not just what API it offers.
