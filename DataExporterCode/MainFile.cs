using System;
using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;

namespace DataExporter.DataExporterCode;

//You're recommended but not required to keep all your code in this package and all your assets in the DataExporter folder.
[ModInitializer(nameof(Initialize))]
public partial class MainFile : Node
{
    public const string ModId = "DataExporter"; //At the moment, this is used only for the Logger and harmony names.

    public const string ModVersion = "0.1.0";

    public static MegaCrit.Sts2.Core.Logging.Logger Logger { get; } = new(ModId, MegaCrit.Sts2.Core.Logging.LogType.Generic);

    public static void Initialize()
    {
        var assembly = Assembly.GetExecutingAssembly();

        //If you want to use scripts defined in your mod for Godot scenes, uncomment the following line.
        //Godot.Bridge.ScriptManagerBridge.LookupScriptsInAssembly(assembly);

        Harmony harmony = new(ModId);

        harmony.PatchAll(assembly);

        // CardMetricsTracker is not constructed here. ModelDb.Init discovers
        // every AbstractModel subtype in loaded mods and instantiates it
        // (ModelDb.cs:389), and BaseLib's CustomSingletonModel base registers
        // it for combat hooks. Constructing one by hand throws
        // DuplicateModelException.
        Logger.Info($"[DataExporter] v{ModVersion} initialized.");
    }

    // ---- run identity --------------------------------------------------------

    /// <summary>
    /// Reads RunManager's private _startTime.
    ///
    /// start_time is the run's primary key — the game names its own history
    /// file {start_time}.run and matches saves against it (SaveManager.cs:611)
    /// — but neither RunState nor IRunState exposes it. The only public
    /// alternative is ToSave(null).StartTime, which serialises the entire run
    /// tree (every act, map, player and deck) just to read one long.
    /// </summary>
    private static readonly AccessTools.FieldRef<RunManager, long>? StartTimeRef =
        BuildStartTimeRef();

    private static AccessTools.FieldRef<RunManager, long>? BuildStartTimeRef()
    {
        try
        {
            return AccessTools.FieldRefAccess<RunManager, long>("_startTime");
        }
        catch (Exception exception)
        {
            Logger.Error($"[DataExporter] Cannot read run start time, stats will not be saved: {exception}");
            return null;
        }
    }

    /// <summary>The current run's start time, or 0 when no run is in progress.</summary>
    public static long CurrentStartTime()
    {
        try
        {
            if (StartTimeRef == null || !RunManager.Instance.IsInProgress)
            {
                return 0;
            }
            return StartTimeRef(RunManager.Instance);
        }
        catch (Exception exception)
        {
            Logger.Error($"[DataExporter] Could not read run start time: {exception}");
            return 0;
        }
    }

    private static string? CurrentSeed()
    {
        try
        {
            return RunManager.Instance.DebugOnlyGetState()?.Rng.StringSeed;
        }
        catch
        {
            return null;
        }
    }

    // ---- orchestration -------------------------------------------------------

    /// <summary>Whether the flush-on-save subscription has been made yet.</summary>
    private static bool _subscribed;

    /// <summary>
    /// Point the tracker at the run on disk, called at the start of every
    /// combat.
    ///
    /// Reloading unconditionally, rather than only when the run changes, is
    /// what stops replayed cards being counted twice. "Save and Quit" does not
    /// save -- it returns to the main menu without calling SaveRun
    /// (NPauseMenu.cs:301) -- and it does not exit the process, so this
    /// singleton's totals survive. The game then restores the run from the save
    /// written when the map point was entered and restarts the combat. Any card
    /// played before quitting was never committed, so it must be dropped, or
    /// replaying the combat counts it a second time.
    /// </summary>
    public static void EnsureRun(CardMetricsTracker tracker)
    {
        long startTime = CurrentStartTime();
        if (startTime <= 0)
        {
            return;
        }

        SubscribeToSaves(tracker);

        switch (SidecarStore.Load(startTime, out RunCardMetrics? loaded))
        {
            case LoadOutcome.Loaded when loaded != null:
                loaded.Seed ??= CurrentSeed();
                tracker.BeginRun(loaded);
                break;

            case LoadOutcome.Missing:
                // First combat of this run.
                tracker.BeginRun(new RunCardMetrics { StartTime = startTime, Seed = CurrentSeed() });
                break;

            default:
                // The file is there but unreadable. Keeping the totals already
                // in memory risks counting a replayed combat twice; resetting
                // them would have the next flush overwrite a good file with
                // zeroes. Losing real data is the worse outcome, so only start
                // fresh when this is plainly a different run.
                if (tracker.Current.StartTime != startTime)
                {
                    tracker.BeginRun(new RunCardMetrics { StartTime = startTime, Seed = CurrentSeed() });
                }
                break;
        }
    }

    /// <summary>
    /// Flush whenever the game commits its own run save.
    ///
    /// Keeping the two files in step is the point: the sidecar then records
    /// exactly the runs and combats the game itself considers committed, so
    /// neither can get ahead of the other. Flushing at combat end instead ran
    /// 23 lines before the game's SaveRun (CombatManager.cs:985 vs :1008),
    /// leaving a window where a crash committed a combat the game did not.
    ///
    /// Subscribed here rather than in Initialize because touching
    /// SaveManager.Instance constructs it, which reaches into Steam and builds
    /// the cloud save store (SaveManager.cs:200) -- not something to trigger
    /// while mods are still loading.
    /// </summary>
    private static void SubscribeToSaves(CardMetricsTracker tracker)
    {
        if (_subscribed)
        {
            return;
        }
        try
        {
            SaveManager.Instance.Saved += () => FlushRun(tracker);
            _subscribed = true;
        }
        catch (Exception exception)
        {
            Logger.Error($"[DataExporter] Could not subscribe to run saves: {exception}");
        }
    }

    /// <summary>
    /// Write the run's totals out, if there is anything new to write.
    /// </summary>
    public static void FlushRun(CardMetricsTracker tracker)
    {
        RunCardMetrics run = tracker.Current;
        if (run.StartTime <= 0 || run.IsComplete || !tracker.IsDirty)
        {
            return;
        }
        SidecarStore.Save(run, ModVersion);
        tracker.MarkFlushed();
    }

    /// <summary>
    /// The run is over: write the final totals and mark them final.
    ///
    /// Called from a postfix on RunManager.OnEnded, since no run-end hook
    /// exists. That covers dying, which reaches OnEnded through
    /// CreatureCmd.Kill without any SaveRun -- so without this the combat that
    /// killed you is never written at all.
    ///
    /// Marking it complete also protects it. Starting the next run saves at
    /// character select (NCharacterSelectScreen.cs:750) while this object is
    /// still the current one, and that write would otherwise reopen a finished
    /// run and label it unfinished.
    /// </summary>
    public static void CompleteRun(CardMetricsTracker tracker)
    {
        RunCardMetrics run = tracker.Current;
        if (run.StartTime <= 0 || run.IsComplete)
        {
            // OnEnded is called twice on a win (RunManager.cs:1223 then via
            // GuaranteeKillAllPlayers) and returns early the second time, but
            // the postfix still runs.
            return;
        }
        run.IsComplete = true;
        SidecarStore.Save(run, ModVersion);
        tracker.MarkFlushed();
        Logger.Info($"[DataExporter] Run {run.StartTime} finished, {run.Cards.Count} cards recorded.");
    }
}

/// <summary>
/// Marks the run as finished when it ends.
///
/// A Harmony patch rather than a hook because the game has none for this:
/// Hook.cs has no run-end entry and RunManager exposes only RunStarted,
/// RoomEntered, RoomExited and ActEntered.
/// </summary>
[HarmonyPatch(typeof(RunManager), nameof(RunManager.OnEnded))]
public static class RunEndedPatch
{
    private static void Postfix()
    {
        try
        {
            MainFile.CompleteRun(ModelDb.Singleton<CardMetricsTracker>());
        }
        catch (Exception exception)
        {
            // A patch that throws would take the end of the run with it.
            MainFile.Logger.Error($"[DataExporter] Could not finalise card stats: {exception}");
        }
    }
}
