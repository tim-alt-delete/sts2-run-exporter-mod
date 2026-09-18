# Building and testing DataExporter

How to build the mod, load it, confirm it is actually working, and get its data
into the dashboard. The end-user section at the bottom is the short version for
someone who just wants to use it.

For *why* the mod is shaped the way it is, read [PLAN.md](PLAN.md). For the
state model behind the save/reload behaviour these checks exercise, and the bugs
that came out of getting it wrong, read [FINDINGS.md](FINDINGS.md).

---

## 1. Prerequisites

| | |
|---|---|
| **.NET SDK** | 9 or 10. A .NET 10 SDK builds the `net9.0` target fine (`apt install dotnet-sdk-10.0` on Ubuntu). |
| **Slay the Spire 2** | Installed via Steam. `Sts2PathDiscovery.props` finds it automatically. |
| **BaseLib** | Required dependency. Download `BaseLib.dll`, `.pck` and `.json` from [github.com/Alchyr/BaseLib-StS2](https://github.com/Alchyr/BaseLib-StS2) releases into `mods/BaseLib/`. |
| **MegaDot 4.5.1** | Only for `dotnet publish`. Skippable while iterating — see below. |

`Directory.Build.props` is gitignored because it holds machine-specific paths. A
fresh clone needs one only to publish:

```xml
<Project>
    <PropertyGroup>
        <GodotPath>/Users/tim/Applications/MegaDot.app/Contents/MacOS/Godot</GodotPath>
    </PropertyGroup>
</Project>
```

`sts2-decompiled/` and `lib/` are gitignored too. Neither is needed to build.

---

## 2. Build

```bash
cd DataExporter
dotnet build -c Release      # dll + pdb + json  -> mods/DataExporter/
dotnet publish -c Release    # the above, plus the .pck via MegaDot
```

Both copy straight into the game's mods folder. On macOS:

```
~/Library/Application Support/Steam/steamapps/common/Slay the Spire 2/
  SlayTheSpire2.app/Contents/MacOS/mods/DataExporter/
```

To build with no game installed, supply the paths directly:

```bash
dotnet build -c Release \
  /p:Sts2DataDir=<folder holding sts2.dll and 0Harmony.dll> \
  /p:ModsPath=/tmp/mods/
```

`ModsPath` matters because `CopyToModsFolderOnBuild` copies there
unconditionally and fails if the folder is missing.

### You do not need `publish` to test

The game loads the DLL independently of the `.pck` (`ModManager.cs:718-745`),
and this mod's entire `.pck` payload is one icon. But `DataExporter.json` says
`"has_pck": true`, so a missing `.pck` logs an error on every launch — noisy,
harmless.

While iterating, either publish, or set `"has_pck": false` and use `dotnet
build` alone. The only cost is no icon on the mods screen; the lookup is guarded
by `ResourceLoader.Exists` and falls back to nothing
(`NModInfoContainer.cs:75-83`). Set it back to `true` before releasing.

---

## 3. Confirm it loaded

Launch the game and start a run.

**The mod does not appear in BaseLib's mod settings screen.** That is expected —
that screen only lists mods which register configuration, and this one has none.
It is not a sign of failure.

Two reliable signals instead:

1. **The log.** `[DataExporter] v0.1.0 initialized.` in
   `~/Library/Application Support/SlayTheSpire2/steam/<steamid>/logs/`
2. **The file.** After your first combat ends, a sidecar appears at
   `~/Library/Application Support/SlayTheSpire2/steam/<steamid>/modded/profile1/saves/history/<start_time>.cardstats.json`

Note `modded/` — see [section 6](#6-the-modded-save-tree).

---

## 4. Verify it is correct

Loading is not working. These six checks are the difference.

### 4.1 The numbers match

Play a combat with a character whose cards you can do arithmetic on. Ironclad
basics: Strike 6 damage / 1 energy, Defend 5 block / 1 energy, Bash 8 damage /
2 energy.

Open the sidecar. `energy_spent` should equal plays × cost exactly, and
`block_gained` should equal plays × block exactly. Those are the two that
cannot be distorted by anything else.

### 4.2 Damage is *actual*, not printed

`damage_unblocked` records what the enemy really lost, after every modifier.
A Bash played while Shrink is on you deals less than 8, and the file will say
so. **That is correct and deliberate** — "was this card good *for this run*"
wants what actually happened, not the card's printed value.

Do not treat a low number here as a bug until you have ruled out debuffs on you,
buffs on the enemy, and a killing blow clamped to the target's remaining HP.

### 4.3 Unattributed behaves

`unattributed.damage_unblocked` should be `0` after a plain attack-only fight,
and become non-zero once Poison or Thorns deals damage. If it is non-zero after
a Strike-only fight, attribution is broken.

### 4.4 Replayed combats do not double-count

This is the regression check for the bug fixed in `fix: discard card plays the
game never committed`. **Confirmed passing 2026-09-18** — re-run it after any
change to when the mod reads or writes state, because that seam has produced
every bug so far.

1. Delete the `.cardstats.json` for the run first.
2. Start a combat, play exactly 2 Strikes.
3. Pause → **Save and Quit**.
4. Continue the run. *The combat restarts from the beginning* — this is normal,
   the game has no mid-combat save.
5. Play 2 Strikes again and finish the fight.

`copies_played` must be **2**, not 4.

### 4.5 A finished run is marked complete

Win, die, or give up from the pause menu, then check the sidecar says
`"complete": true`. Dying is the interesting case: it reaches `RunManager.OnEnded`
without the game saving, so the fatal combat is only captured by the run-end
patch.

Abandoning from the **main menu** is the one path that legitimately leaves
`"complete": false` — it bypasses `RunManager` entirely and no mod code runs.

### 4.6 Draws and energy are attributed

Play a card that draws (Ironclad: Pommel Strike) or gives energy. `cards_drawn`
should rise for *that* card, and not for the start-of-turn hand.

---

## 5. Into the dashboard

MongoDB must be running first — there is no in-memory mode, so the app will
refuse to start without it.

```bash
cd ../dashboard
docker compose up -d                 # required, first
flask --app app run --debug          # http://127.0.0.1:5000
```

Register, then upload your saves folder:

```
~/Library/Application Support/SlayTheSpire2/steam/<steamid>/modded/profile1/saves
```

A **Card utility** table appears on that run's detail page. Runs with no sidecar
render exactly as before.

To run the dashboard's own checks:

```bash
docker compose up -d
python test_dashboard.py
```

---

## 6. The modded save tree

**Loading any mod redirects all save data into a `modded/` subfolder.**
`ModManager.IsRunningModded()` returns true if *any* mod is loaded, regardless
of the manifest's `affects_gameplay` flag (`ModManager.cs:915-922`), and
`UserDataPathProvider.GetProfileDir` prefixes `modded/` accordingly.

Consequences:

- Runs played with this mod are tagged **modded** in the dashboard. The default
  overview shows vanilla only — use `?tree=modded` or `?tree=all`.
- Existing vanilla runs are untouched and stay where they are.
- On the upload page, tick **"Treat all of these as modded runs"** if you pick a
  `saves` folder from *inside* `modded/`. At that depth the browser no longer
  reports the `modded` path segment, so it cannot be detected automatically.

There is no way around this short of not running mods. It is the game's
behaviour, not the dashboard's.

---

## 7. Traps

Things that cost time to work out once already.

- **"Save and Quit" does not save.** It returns to the main menu without calling
  `SaveRun` (`NPauseMenu.cs:301-320`), and it does not exit the process. Your run
  is restored from whatever was written when you entered the current map point.
- **The game never saves during combat.** `SaveRun` has exactly four call sites:
  run start, travelling to a map point, event rooms, and combat *end*. So a
  combat you quit out of always restarts from the beginning.
- **`dotnet build` failing with thousands of errors** in `sts2-decompiled/`
  means the csproj's `Compile Remove` entries are missing. The Godot SDK globs
  `**/*.cs` and will otherwise compile the entire decompiled game into the mod.
- **Sidecars from an older build may hold inflated counts.** Totals are
  cumulative and rewritten on each save, but a run already inflated stays that
  way. Delete the `.cardstats.json` for in-progress test runs after updating.

---

# For end users

1. Install [BaseLib](https://github.com/Alchyr/BaseLib-StS2) into
   `Slay the Spire 2/.../mods/BaseLib/`.
2. Put the `DataExporter` folder next to it in `mods/`.
3. Play. The mod records what each card did and writes it beside your run
   history automatically. There is nothing to configure.
4. Upload your `saves` folder to the dashboard as usual — the card data comes
   along with it.

**Two things to know:**

- Running any mod moves your saves into a `modded/` folder, so these runs appear
  under the dashboard's *modded* filter rather than the default view.
- Damage from Poison, Thorns and relics arrives with no card attached to it, so
  it cannot be credited to a card. That amount is shown separately as
  **unattributed**. When it is a large share of your damage, read the per-card
  numbers as a lower bound.
