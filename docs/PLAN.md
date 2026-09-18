# DataExporter — plan and context

This file is the starting point for a new session. It records what exists, why
it is shaped the way it is, what is planned next, and the domain knowledge that
was expensive to work out. Read it instead of rediscovering all of this.

Repo: `DataExporter/` is its own git repository. The parent
`slay-the-spire-mod/` directory is not a git repo and also holds the dashboard
(`dashboard/`, its own repo `tim-alt-delete/sts2-run-data`) and the decompiled
game source (`DataExporter/sts2-decompiled/`, gitignored).

---

## 1. What this is

A Slay the Spire 2 mod that records **per-card utility metrics** during a run
and writes them beside the game's own `.run` history file, so the dashboard can
answer "was this card good for this run, and by what metric?".

The metrics, aggregated per `(card id, upgrade level)` for a whole run:

- times played
- damage dealt, split blocked / unblocked (plus overkill and kills)
- block gained
- energy spent and energy gained
- cards drawn

**Why a mod is needed at all:** the `.run` files the game writes contain
end-of-run summaries only — final deck, relics, map path, per-floor player
stats. No per-card damage, block or energy figures exist anywhere on disk.
The only place that information exists is in memory during combat.

**What is deliberately not a mod's job:** the final deck. It is already in the
`.run` file at `players[].deck`, with `id`, `current_upgrade_level` and
`enchantment` per card. The mod does not duplicate it.

---

## 2. Current state

Nothing is implemented yet beyond the mod template. This section describes the
design that research settled on; section 4 tracks what has actually shipped.

### Building

Needs a real game install and the .NET SDK. Nothing about the build is
Godot-editor-specific until you need the `.pck`.

```
dotnet build -c Release
```

Path discovery is automatic via `Sts2PathDiscovery.props`. To build without a
game install, supply the two paths directly:

```
dotnet build -c Release \
  /p:Sts2DataDir=<folder holding sts2.dll and 0Harmony.dll> \
  /p:ModsPath=/tmp/mods/
```

`ModsPath` matters because the `CopyToModsFolderOnBuild` target copies the
output there unconditionally, and fails if the folder does not exist.

The `.pck` is only produced on `dotnet publish`, and needs MegaDot 4.5.1 at the
path in `Directory.Build.props` (gitignored, machine-specific). The mod's whole
`.pck` payload is one PNG, so `build` is enough for everything except a real
in-game load.

### Files

| | |
|---|---|
| `DataExporterCode/MainFile.cs` | `[ModInitializer]` entry point, Harmony setup, logger |
| `DataExporterCode/RunStats.cs` | placeholder win/loss counter from the template |
| `DataExporter.json` | mod manifest; declares the BaseLib dependency |
| `DataExporter.csproj` | references `sts2.dll` + `0Harmony.dll`, pulls BaseLib from NuGet |
| `sts2-decompiled/` | decompiled game source, gitignored, the authority for everything below |
| `lib/` | game assemblies to compile against, gitignored |

---

## 3. Domain knowledge

All file:line references are into `sts2-decompiled/sts2/` unless stated.

### The game has a first-class hook system

`MegaCrit.Sts2.Core.Hooks/Hook.cs` holds ~80 gameplay hooks. Each dispatches to
"hook listeners" — the models that care about combat: relics, powers, cards in
combat piles, enchantments. Every hook has a matching `virtual` method on
`MegaCrit.Sts2.Core.Models/AbstractModel.cs` that does nothing by default.

**Mods can inject their own listeners.** `MegaCrit.Sts2.Core.Modding/ModHelper.cs:100,120`
exposes `SubscribeForRunStateHooks` / `SubscribeForCombatStateHooks`. These are
really called: `Combat/CombatState.cs:489` and `Runs/RunState.cs:584`. Mod
subscribers are yielded *after* the `Contains(item)` filter, so unlike game
models they are never filtered out.

This means **no Harmony patching is needed to collect the data.**

### The hooks carry the causing card directly

This is the finding that makes the whole feature cheap. No inference needed:

| Metric | Hook (`AbstractModel.cs`) | Attribution |
|---|---|---|
| card played | `AfterCardPlayed(ctx, cardPlay)` `:463` | `cardPlay.Card` |
| damage | `AfterDamageGiven(ctx, dealer, result, props, target, cardSource)` `:567` | **`cardSource`** |
| block | `AfterBlockGained(creature, amount, props, cardSource)` `:321` | **`cardSource`** |
| energy spent | `AfterEnergySpent(card, amount)` `:686` | **`card`** |
| cards drawn | `AfterCardDrawn(ctx, card, fromHandDraw)` `:396` | none — see bracketing |
| energy gained | *no hook exists* | none — see bracketing |
| combat over | `AfterCombatEnd(room)` `:506` | — |

`DamageResult` (`Entities.Creatures/DamageResult.cs`) already splits exactly the
way we need: `BlockedDamage`, `UnblockedDamage`, `TotalDamage`, `OverkillDamage`,
`WasTargetKilled`, each with worked examples in its doc comments.

Energy spent is also available without a hook, as
`cardPlay.Resources.EnergySpent` (`Entities.Cards/ResourceInfo.cs`). Note it
differs from `EnergyValue`: an auto-played 3-cost card has `EnergyValue` 3 and
`EnergySpent` 0.

### Bracketing, for the two metrics with no card source

`Models/CardModel.cs:1929-1959` shows the play sequence:

```
Hook.BeforeCardPlayed          -> push
History.CardPlayStarted
OnPlay / enchantment / affliction   <- draws and energy gains happen here
History.CardPlayFinished
Hook.AfterCardPlayed           -> pop
```

Anything drawn or gained between the push and the pop belongs to the card on
top of the stack.

**It must be a stack, not a single field.** Auto-play effects (Mayhem,
Hellraiser) play a card from inside another card's `OnPlay`, so plays nest.

**Brackets can be left open.** There are early `return`s inside the loop when
`Owner.Creature.IsDead`, which skip `AfterCardPlayed`. The stack must be
cleared at combat start rather than assumed balanced.

### Trap: subscribing to both hook types double-counts everything

`Runs/RunState.cs:584-595` yields the run-state subscribers, then delegates to
`childCombatState.IterateHookListeners()` and yields the combat-state
subscribers too. A model subscribed to **both** is therefore yielded twice for
any hook dispatched through `runState.IterateHookListeners(combatState)` — which
is 33 of the hooks in `Hook.cs`.

BaseLib documents this in the obsolete-constructor message on
`CustomSingletonModel`:

> *"Use the constructor receiving a HookType instead. A singleton receiving both
> types of hooks will receive some hooks twice, so this constructor is being
> replaced."*

**Subscribe combat-only.** Every hook this mod needs still arrives, including
`AfterCombatEnd`, which goes through `runState.IterateHookListeners(combatState)`
(`Hooks/Hook.cs:329`) and so reaches combat subscribers via that delegation.

This failure mode produces numbers that are exactly double — plausible, not
crashy. It would not have been caught by looking at output.

### BaseLib 3.4.7

Declared dependency, pulled from NuGet. Wiki at
`https://alchyr.github.io/BaseLib-Wiki/`. The `.nupkg` ships `BaseLib.xml`,
which is the full documented API surface — read that rather than guessing.

What is useful here:

- **`BaseLib.Abstracts.CustomSingletonModel`** — *"Model that will passively
  receive hooks at all times."* Exactly the tracker pattern.
  ```csharp
  public abstract class CustomSingletonModel : MegaCrit.Sts2.Core.Models.SingletonModel, ICustomModel
  {
      public enum HookType { None, Combat, Run }
      protected CustomSingletonModel(HookType hookType);
      [Obsolete] protected CustomSingletonModel(bool receiveCombatHooks, bool receiveRunHooks);
      public virtual IEnumerable<AbstractModel> RunSubModels(RunState);
      public virtual IEnumerable<AbstractModel> CombatSubModels(CombatState);
  }
  ```
- **`BaseLib.Utils.SavedSpireField<TTarget,TValue>`** — *"A SpireField whose
  value will automatically be saved and loaded. Only functions on model types
  that support SavedProperty, so mainly just cards and relics."* See future work.
- `BaseLib.Config.*` — attribute-driven mod settings UI, if a toggle is ever wanted.

What is **not** useful, despite the names: `HookUtils` and `BaseLibHooks` are
for *defining your own* hook interfaces and dispatching them, not for
subscribing to MegaCrit's existing hooks.

BaseLib has no run-end hook either.

### Models are auto-instantiated — never `new` one

`Models/ModelDb.cs:389` `Init()` walks `AllAbstractModelSubtypes`, which
includes `ReflectionHelper.GetSubtypesInMods<AbstractModel>()`, and calls
`Activator.CreateInstance(type)` on every one.

Consequences for any model this mod declares:

- it must be **non-abstract** with a **public parameterless constructor**
- the game constructs it; constructing it yourself throws
  `DuplicateModelException` from `AbstractModel.cs:71-77`
- retrieve the instance with `ModelDb.Singleton<T>()` (`ModelDb.cs:632`)
- it is created **once at startup**, not once per run, so per-run state must be
  reset explicitly

### There is no run-end hook

Verified absent from `Hook.cs` and from BaseLib. `RunManager` exposes only
`RunStarted`, `RoomEntered`, `RoomExited`, `ActEntered` (`Runs/RunManager.cs:211-217`).

Everything funnels through `RunManager.OnEnded` (`Runs/RunManager.cs:1494`),
which writes the `.run` at `:1532` via
`RunHistoryUtilities.CreateRunHistoryEntry` → `SaveManager.SaveRunHistory` →
`Saves.Managers/RunHistorySaveManager.cs:52`.

Three run-end paths exist, and one of them bypasses `RunManager` entirely:

| Path | Route |
|---|---|
| win | `RunManager.WinRun()` `:1217` → `OnEnded(true)` |
| death | `Commands/CreatureCmd.cs:460` → `OnEnded(false)` |
| abandon, in run | `RunManager.AbandonInternal()` `:1383` → kills players → death path |
| **abandon, from main menu** | `Nodes.Screens.MainMenu/NMainMenu.cs:749` — **no `RunManager`, no hooks, no live state** |

This is why the mod flushes after **every combat** instead of once at run end:
it needs no Harmony patch, survives crashes and force-quits, and is the only
approach that captures the main-menu abandon path.

### `start_time` is the run's primary key, and is awkward to reach

The `.run` filename is `{StartTime}.run` (`RunHistorySaveManager.cs:56`), a Unix
epoch **seconds** `long`. The game itself treats it as the run's primary key —
`SaveManager.cs:611-624` matches `current_run.save`'s `start_time` against
`{value}.run`.

It is preserved verbatim across save/load (`RunManager.cs:297`), so it is stable
for the whole run.

But **`RunState` and `IRunState` do not expose it.** It lives only on the private
`RunManager._startTime` (`:52`). Options, in order of preference:

1. `AccessTools.FieldRefAccess` on the private field — Harmony is already a
   dependency, one line, read once per run and cached.
2. `RunManager.Instance.ToSave(null).StartTime` — public (`:624`) but serializes
   the entire run tree. Fine once per run, not per combat.

The seed *is* reachable: `IRunState.Rng` → `RunRngSet.StringSeed`
(`Runs/RunRngSet.cs:21`). Note the JSON `seed` is `StringSeed`, the original
input string, not the numeric `Seed`.

### Where to write the sidecar

Beside the `.run` file, so a dashboard folder-upload sweeps it up with no extra
instruction:

```csharp
var dir = ProjectSettings.GlobalizePath(
    SaveManager.Instance.GetProfileScopedPath("saves"));   // SaveManager.cs:247
var path = Path.Combine(dir, "history", $"{startTime}.cardstats.json");
```

That pattern is lifted from `DevConsole.ConsoleCommands/OpenConsoleCmd.cs:45`.

Do **not** use `OS.GetUserDataDir()` — that is the Godot user root, not the
profile's save tree, and is what the template's `RunStats.cs` placeholder gets
wrong.

**Loading any mod redirects saves into a `modded/` subfolder.**
`UserDataPathProvider.IsRunningModded` is set from `ModManager.IsRunningModded()`
at `Helpers/OneTimeInitialization.cs:60`, and `GetProfileDir` prefixes
`modded/` (`Saves/UserDataPathProvider.cs:41-44`). Going through
`GetProfileScopedPath` inherits this automatically. The dashboard already
distinguishes modded and vanilla trees.

Full resolved shape:
```
user://{steam|editor|default}/{userId}/[modded/]profile{N}/saves/history/
```

### Save, quit and resume destroys in-memory state

`RunState.FromSerializable` (`Runs/RunState.cs:291`) → `Player.FromSerializable`
(`Entities.Players/Player.cs:358`) → `CardModel.FromSerializable`
(`Models/CardModel.cs:2216`) mints **all-new** `CardModel` objects. Quitting to
the menu also nulls `RunManager.State` (`RunManager.cs:1484`).

Two consequences:

- any accumulator keyed on `CardModel` object identity is lost
- mod statics survive a quit-to-menu (the process lives), so they are *stale*
  rather than empty — state must be reset on `RunManager.RunStarted`

The mod therefore reloads its own sidecar on resume, keyed by `start_time`.

There is also **no mid-combat save**. `Saves/SerializableRun.cs:16-117` has no
combat state, no piles, no powers, no turn number. Saves happen at room
boundaries only.

### Card identity

`ModelId` is a `record` of `Category` + `Entry` (`Models/ModelId.cs:7-25`), safe
as a dictionary key.

`Entry` is **UPPER_SNAKE_CASE of the C# class name**, not lowercase: class
`Strike` → `"STRIKE"`, full id `"CARD.STRIKE"`; `AdaptiveStrike` →
`"CARD.ADAPTIVE_STRIKE"`. Derived from `ModelDb.GetEntry` → `StringHelper.Slugify`
(`Helpers/StringHelper.cs:90`). Confirmed independently by a migration comment
at `Saves.Migrations.SerializableRuns/SerializableRunV15ToV16.cs:8`.

Upgrades are an **`int`**, not a bool: `CardModel.CurrentUpgradeLevel`
(`:764`), `MaxUpgradeLevel` (`:781`, virtual — some cards upgrade more than
once), `IsUpgraded` (`:783`). Serialized as `current_upgrade_level`, omitted
when 0.

**There is no per-instance card identifier.** Not on `CardModel`, not on
`AbstractModel`, not on `SerializableCard` — whose `Equals`/`GetHashCode`
(`Saves.Runs/SerializableCard.cs:73-94`) compare only id, upgrade level and
enchantment, so two copies are genuinely indistinguishable. Near misses that do
not work: `NetDeckCard.DeckIndex` is a positional index, `NetCombatCard` ids are
cleared every combat, `FloorAddedToDeck` collides, `DeckVersion` is a live
object reference only.

Related: cards in combat are **clones** of the deck instances.
`Player.PopulateCombatState` (`Player.cs:802-812`) calls `state.CloneCard` and
sets `cardModel.DeckVersion` back to the deck original. Irrelevant while
aggregating by id, but it matters the moment anything is keyed on the instance.

---

## 4. Phases

### Phase 1 — research (done)

Established that the feature is feasible without Harmony patching, that six of
the seven needed hooks carry the causing card directly, and that BaseLib
provides the exact base class for the job. All of section 3.

### Phase 2 — the tracker (planned)

`CardMetricsTracker : CustomSingletonModel(HookType.Combat)` overriding
`BeforeCardPlayed`, `AfterCardPlayed`, `AfterDamageGiven`, `AfterBlockGained`,
`AfterCardDrawn`, `ModifyEnergyGain` (as a read-only observer returning its
input unchanged — there is no `AfterEnergyGained`) and `AfterCombatEnd`.

Filter to the local player via `LocalContext.GetMe` (`Context/LocalContext.cs:28`)
so multiplayer does not blend both players' cards.

### Phase 3 — the sidecar (planned)

Write `{start_time}.cardstats.json` into `saves/history/` after every combat,
temp-file-then-rename so a crash cannot leave a half-parsed file. Reload on
resume.

```json
{
  "schema": 1,
  "mod_version": "0.1.0",
  "start_time": 1789424859,
  "seed": "3J6ZXDRGZE",
  "complete": false,
  "cards": [
    {
      "id": "CARD.STRIKE", "upgrade_level": 0, "copies_played": 12,
      "damage_unblocked": 84, "damage_blocked": 18, "overkill": 4, "kills": 2,
      "block_gained": 0, "energy_spent": 12, "energy_gained": 0, "cards_drawn": 0
    }
  ],
  "unattributed": { "damage_unblocked": 31, "damage_blocked": 0, "block_gained": 9 }
}
```

`complete` stays false until the final flush. A run abandoned from the main menu
never gets one, and that flag is how the dashboard says "partial" instead of
silently presenting partial totals as final.

`unattributed` is what keeps the comparison honest — see the next section.

### Phase 4 — dashboard ingest (planned)

In `dashboard/`: a `CARDSTATS_KIND` in `uploads.classify()` for
`*.cardstats.json` (which currently returns `None` and is ignored, so the change
is purely additive), a `validate_cardstats()`, a `card_stats` collection keyed
`(user_id, start_time)` and upserted, and a sortable table on the existing run
detail page.

Join at render, not at ingest: a sidecar uploaded mid-run arrives before its
`.run` exists, and should light up later rather than be rejected as an orphan.

---

## 5. Future work

### Attributing damage from powers and status effects

**This is the biggest known gap.** `cardSource` is null for damage dealt by
Poison ticks, Thorns and relic procs, so a card that applies Poison is credited
with applying it but not with the damage it causes over the following turns.
Those land in the `unattributed` bucket.

The consequence is not neutral: it makes damage-over-time builds look bad
against exactly the "was this card good?" question the project exists to answer.

Doing it properly means tracking which card applied a given `PowerModel` and
crediting that card when the power later deals damage. The game maintains no
such provenance, and it is ambiguous when two cards stack the same power.
`AfterPowerAmountChanged` / `BeforePowerAmountChanged` plus the bracketing stack
is the likely route.

The `unattributed` bucket exists so the size of this gap is visible before
anyone decides it matters.

### Per-copy card tracking

Recorded as impossible in early research; that is true of the **vanilla** game
but wrong in the presence of BaseLib.

`SavedSpireField<TTarget,TValue>` rides the `SavedProperties` /
`SerializableCard.props` channel — the one persistence vector for custom
per-card data — and handles save/load. So minting a durable per-copy id, and
tracking each physical Strike separately, is achievable.

Not scheduled: duplicates are aggregated by choice, and per-copy data multiplies
the sidecar size for a question nobody has asked yet.

### Potions

The same hooks cover potions; `PotionUsedEntry` and `Hook.AfterPotionUsed` exist.
Out of scope, but nearly free once the card path works.

### Path discovery breaks on an arm64 macOS build

`Sts2PathDiscovery.props` hardcodes `data_sts2_macos_x86_64`. Mega Crit ships
macOS x86_64 only today (Apple Silicon runs it under Rosetta). If a native
arm64 or universal build ever ships, discovery silently fails and the build
stops with `Slay the Spire 2 data not found` from the `CheckDependencyPaths`
target. Easy fix, confusing symptom.

Note this is a *path* problem, not an architecture problem: `sts2.dll` and
`0Harmony.dll` are managed MSIL and compile fine from any host architecture.
The csproj already suppresses `MSB3270` for exactly this reason.

### Multiplayer

Metrics are filtered to the local player. Recording both players, and the
interaction between them, is untouched.

---

## 6. Conventions

- **Commits**: lowercase conventional style (`feat:`, `fix:`, `refactor:`).
  Body explains reasoning and any non-obvious constraint discovered.
  Author is `Timothy Pulliam <contact@timothypulliam.com>`. Sandboxes are
  recreated without a git identity, so when `git config user.name` is empty,
  pass it per-commit via `GIT_AUTHOR_NAME` / `GIT_AUTHOR_EMAIL` /
  `GIT_COMMITTER_NAME` / `GIT_COMMITTER_EMAIL` rather than writing to config.
- **The decompiled source is the authority.** Every claim in section 3 carries a
  `file:line`. If something here disagrees with the source, the source wins and
  this file is wrong — fix it.
- **Never commit game code.** `sts2-decompiled/` and `lib/` are gitignored and
  must stay that way.
- **Verification**: compilation can be checked anywhere the two game assemblies
  are available. Anything about runtime behaviour — whether hooks actually fire,
  whether the numbers are right — can only be checked by playing the game, and
  must be.
- **Phases**: built one at a time, each verified, with a review pause between.
