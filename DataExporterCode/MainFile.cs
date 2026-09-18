using System;
using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;

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

    /// <summary>
    /// Point the tracker at the run that is actually in progress, called at the
    /// start of every combat.
    ///
    /// Detecting the run here rather than on RunManager.RunStarted keeps this
    /// independent of mod-init ordering, and covers the resume case: the
    /// tracker is a ModelDb singleton created once at startup, so without this
    /// it would happily carry one run's totals into the next.
    /// </summary>
    public static void EnsureRun(CardMetricsTracker tracker)
    {
        long startTime = CurrentStartTime();
        if (startTime <= 0 || tracker.Current.StartTime == startTime)
        {
            return;
        }

        // A sidecar already on disk means this run was resumed: quitting to the
        // menu rebuilds RunState from the save file and drops everything held
        // in memory, so the totals have to come back from the file.
        RunCardMetrics run = SidecarStore.Load(startTime)
                             ?? new RunCardMetrics { StartTime = startTime };
        run.Seed ??= CurrentSeed();
        tracker.BeginRun(run);
    }

    /// <summary>
    /// Persist after every combat.
    ///
    /// Not once at run end, because there is no run-end hook, and because
    /// abandoning from the main menu writes a .run without RunManager ever
    /// being involved (NMainMenu.cs:749) — a run-end write would miss it
    /// entirely. Flushing per combat also survives a crash or force-quit.
    /// </summary>
    public static void FlushRun(CardMetricsTracker tracker, bool complete = false)
    {
        if (!tracker.IsDirty && !complete)
        {
            return;
        }
        SidecarStore.Save(tracker.Current, ModVersion, complete);
        tracker.MarkFlushed();
    }
}
