using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using Refurnish.Inventory;

namespace Refurnish.Automation;

/// <summary>An item standing on the floor under its layout spot, and where it should end up.</summary>
public sealed record LiftJob(string Name, Vector3 From, Vector3 To, float Rotation);

/// <summary>
/// Finishes a full rebuild without ReMakePlace: lifts or sinks the items the game would only place
/// on the floor (floating roofs, sunk pillars, raised partitions) into their exact layout spot.
///
/// For each item, in the furnishing window's Rotate mode, with place anywhere on: find it in the
/// room, select it with the game's own function and check the game really selected it, set its
/// position and rotation, set it down with the game's own function, then check it's at its spot.
/// One item at a time. Stops if Rotate mode is left, you leave the room, or several in a row fail.
/// </summary>
public sealed unsafe class Lifter
{
    private enum Step { Idle, Next, AfterSelect, Verify }

    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(0.6);
    private static readonly TimeSpan SelectWait = TimeSpan.FromSeconds(0.2);
    private static readonly TimeSpan VerifyWait = TimeSpan.FromSeconds(0.6);
    private const int MaxFailuresInARow = 5;
    private const float Match = 0.05f;

    private readonly Plugin plugin;
    private readonly List<LiftJob> jobs = new();
    private readonly List<string> log = new();
    private Step step = Step.Idle;
    private int index;
    private DateTime at;
    private nint instance;
    private ulong houseId;
    private int failuresInARow;

    public bool Running => step != Step.Idle;
    public int Done { get; private set; }
    public int Failed { get; private set; }
    public int Total => jobs.Count;
    public string? StopReason { get; private set; }
    public string? Summary { get; private set; }

    public Lifter(Plugin plugin) => this.plugin = plugin;

    public List<string> RecentLog(int n) => log.Skip(Math.Max(0, log.Count - n)).ToList();

    /// <summary>Why it can't start right now, or null.</summary>
    public string? CanStart(ulong house)
    {
        if (Running) return "Already running.";
        if (plugin.Scanner.CurrentHouseId != house || house == 0) return "Go inside this room first.";
        if (plugin.UiPlacer.Running || plugin.Executor.Running || plugin.UiLearner.Active || plugin.Learner.Active)
            return "Another Refurnish action is running.";
        if (!plugin.Editor.Resolve()) return plugin.Editor.Error;
        if (!plugin.Editor.InRotateMode) return "Switch the furnishing window to Rotate mode (the circular-arrow button).";
        return null;
    }

    public void Start(ulong house, IEnumerable<LiftJob> toLift)
    {
        var problem = CanStart(house);
        if (problem != null) { StopReason = problem; return; }
        if (!plugin.Anywhere.Enable())
        {
            StopReason = plugin.Anywhere.Error ?? "Couldn't switch on place anywhere, which lifting needs.";
            return;
        }

        jobs.Clear();
        jobs.AddRange(toLift);
        log.Clear();
        houseId = house;
        index = 0;
        Done = 0;
        Failed = 0;
        failuresInARow = 0;
        StopReason = null;
        Summary = null;
        at = DateTime.UtcNow;
        step = Step.Next;
        Log($"Finishing heights for {jobs.Count} item(s). Place anywhere is on until this ends.");
    }

    public void Stop(string reason)
    {
        if (step == Step.Idle) return;
        step = Step.Idle;
        plugin.Anywhere.Disable();
        StopReason = reason;
        Log($"Stopped: {reason}");
    }

    public void Update()
    {
        if (step == Step.Idle) return;
        if (plugin.Scanner.CurrentHouseId != houseId) { Stop("You left the room."); return; }
        if (!plugin.Editor.InRotateMode) { Stop("The furnishing window left Rotate mode."); return; }
        if (DateTime.UtcNow < at) return;

        switch (step)
        {
            case Step.Next: StepNext(); break;
            case Step.AfterSelect: StepAfterSelect(); break;
            case Step.Verify: StepVerify(); break;
        }
    }

    private void StepNext()
    {
        if (index >= jobs.Count) { Finish(); return; }
        var job = jobs[index];

        // Already where it belongs (for example after an earlier run): nothing to do.
        if (FindObject(job.To) != null)
        {
            Log($"{job.Name}: already at its spot.");
            Done++;
            Advance();
            return;
        }

        var obj = FindObject(job.From);
        if (obj == null) { Fail(job, "couldn't find it on the floor where it was placed."); return; }
        if (obj->SharedGroupLayoutInstance == null) { Fail(job, "the game has no layout object for it."); return; }

        instance = (nint)obj->SharedGroupLayoutInstance;
        if (!plugin.Editor.Select(instance)) { Stop("Couldn't select furniture."); return; }
        step = Step.AfterSelect;
        at = DateTime.UtcNow + SelectWait;
    }

    private void StepAfterSelect()
    {
        var job = jobs[index];

        // Only change anything if the game really selected this exact item.
        if (plugin.Editor.Selected != instance)
        {
            Fail(job, "the game didn't select it, so it wasn't moved.");
            return;
        }

        var sg = (FFXIVClientStructs.FFXIV.Client.LayoutEngine.Group.SharedGroupLayoutInstance*)instance;
        sg->Transform.Position = job.To;
        sg->Transform.Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, job.Rotation);
        if (!plugin.Editor.Place(instance)) { Stop("Couldn't set the furniture down."); return; }

        step = Step.Verify;
        at = DateTime.UtcNow + VerifyWait;
    }

    private void StepVerify()
    {
        var job = jobs[index];
        var landed = LiveFurniture.Read()?.Values.Any(f => Vector3.Distance(f.Position, job.To) < Match) ?? false;
        if (landed)
        {
            Done++;
            failuresInARow = 0;
            Log($"{job.Name}: moved into place.");
            Advance();
        }
        else
        {
            Fail(job, "it didn't end up at its spot.");
        }
    }

    private void Advance()
    {
        index++;
        step = Step.Next;
        at = DateTime.UtcNow + Interval;
    }

    private void Fail(LiftJob job, string why)
    {
        Failed++;
        Log($"{job.Name}: {why}");
        if (++failuresInARow >= MaxFailuresInARow)
        {
            Stop($"{MaxFailuresInARow} items in a row couldn't be moved, so something is wrong. ReMakePlace's Apply can finish the rest.");
            return;
        }
        Advance();
    }

    private void Finish()
    {
        step = Step.Idle;
        plugin.Anywhere.Disable();
        Summary = Failed == 0
            ? $"Done. All {Done} item(s) are at their spots."
            : $"Done. {Done} moved into place; {Failed} couldn't be (ReMakePlace's Apply can finish those).";
        Log(Summary);
    }

    /// <summary>The placed furniture object nearest to a point, if one is within a few centimetres.</summary>
    private static GameObject* FindObject(Vector3 near)
    {
        var hm = HousingManager.Instance();
        if (hm == null || hm->IndoorTerritory == null) return null;
        var array = &hm->IndoorTerritory->FurnitureManager.ObjectManager.ObjectArray;

        GameObject* best = null;
        var bestDist = float.MaxValue;
        foreach (var p in array->Objects)
        {
            var o = p.Value;
            if (o == null || o == array->TempObject) continue;
            var d = Vector3.Distance(new Vector3(o->Position.X, o->Position.Y, o->Position.Z), near);
            if (d < Match && d < bestDist) { best = o; bestDist = d; }
        }
        return best;
    }

    private void Log(string line)
    {
        log.Add($"{DateTime.Now:HH:mm:ss} {line}");
        if (log.Count > 500) log.RemoveAt(0);
    }
}
