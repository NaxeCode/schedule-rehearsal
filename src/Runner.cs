using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;
using Quartz;

namespace Rehearsal;

public sealed class RecordOccurrence : IJob
{
    public ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken = default)
    {
        var ledger = (Ledger)context.Scheduler.Context["ledger"]!;
        ledger.Record(new Fire(context.Trigger.Key.Name,
            Format.Utc(context.ScheduledFireTimeUtc ?? throw new InvalidOperationException("Missing Quartz due time.")),
            Format.Utc(context.FireTimeUtc), context.FireInstanceId, context.Scheduler.SchedulerInstanceId));
        return ValueTask.CompletedTask;
    }
}

sealed record ReviewRow(Occurrence Occurrence, string Stored, string? Target, string Decision);
sealed record Plan(string Scenario, string Policy, long Revision, string ManifestHash, string OldRulesHash, string NewRulesHash, ReviewRow[] Rows)
{
    internal string Fingerprint() => Format.Hash(Format.Write(this));
}

sealed class Runner(Ledger ledger, Manifest manifest, IScheduler scheduler, FakeTimeProvider clock)
{
    static readonly JobKey Job = new("record-occurrence");
    readonly Occurrence[] occurrences = Rules.Expand(manifest);

    ITrigger Trigger(string id, string utc) => TriggerBuilder.Create(clock).WithIdentity(id).ForJob(Job)
        .StartAt(Format.Time(utc)).WithSimpleSchedule(s => s.WithMisfireInstruction(SimpleTriggerMisfireInstruction.IgnoreMisfires)).Build();

    async Task<string?> Pending(string id)
    {
        var trigger = await scheduler.GetTrigger(new TriggerKey(id));
        return trigger?.NextFireTimeUtc is { } due ? Format.Utc(due) : null;
    }

    internal static async Task<T> WithScheduler<T>(Ledger ledger, Manifest input, string role, DateTimeOffset start,
        Func<Runner,Task<T>> action)
    {
        var clock = new FakeTimeProvider(start);
        await using var factory = QuartzSchedulerBuilder.Create(q =>
        {
            q.ConfigureScheduler(options =>
            {
                options.InstanceName = "schedule-rehearsal";
                options.InstanceId = $"{role}-{Guid.NewGuid():N}";
            });
            q.UseTimeProvider(clock);
            q.UsePersistentStore(store =>
            {
                store.UseSqlite(SqliteFactory.Instance, ledger.Connection);
                store.ProvisionSchema();
            });
        }).Build();
        var scheduler = await factory.GetScheduler();
        scheduler.Context["ledger"] = ledger;
        try { return await action(new Runner(ledger, input, scheduler, clock)); }
        finally { await scheduler.Shutdown(waitForJobsToComplete: true); }
    }

    internal async Task<object> Seed()
    {
        await scheduler.AddJob(JobBuilder.Create<RecordOccurrence>().WithIdentity(Job).StoreDurably().Build());
        foreach (var row in occurrences) await scheduler.ScheduleJob(Trigger(row.Id, row.Original));
        var history = occurrences.Where(x => x.Day == manifest.HistoryDay).Select(x => (x.Id, x.Original));
        await Dispatch(history);
        ledger.Change("UPDATE input SET ready=1 WHERE ready=0");
        return await Inspect();
    }

    internal async Task<Plan> Preview(long revision, string policy)
    {
        if (policy is not ("intent" or "keep" or "wall")) throw new ArgumentException("Policy must be intent, keep, or wall.");
        var past = ledger.Fires().ToDictionary(x => x.Id);
        var rows = new List<ReviewRow>();
        foreach (var occurrence in occurrences)
        {
            if (past.TryGetValue(occurrence.Id, out var fire))
            {
                rows.Add(new(occurrence, fire.Scheduled, fire.Scheduled, "protect"));
                continue;
            }
            var stored = await Pending(occurrence.Id) ?? throw new InvalidDataException($"Missing persisted trigger: {occurrence.Id}");
            string? target = policy switch
            {
                "keep" => stored,
                "wall" when occurrence.Intent == "fixed" => Rules.MoveWallClock(stored, manifest),
                _ => occurrence.Proposed
            };
            if (target != null && past.Values.Any(f => Format.Time(f.Scheduled) >= Format.Time(target))) target = null;
            string decision = target switch { null => "blocked", _ when stored == target => "keep", _ => "move" };
            rows.Add(new(occurrence, stored, target, decision));
        }
        return new(manifest.Id, policy, revision, Format.Hash(Format.Write(manifest)), Rules.Hash(manifest.PreviousVersion), Rules.Hash(manifest.CandidateVersion), rows.ToArray());
    }

    internal async Task<object> Inspect()
    {
        var triggers = new SortedDictionary<string,string?>();
        foreach (var row in occurrences) triggers[row.Id] = await Pending(row.Id);
        return new { worker = scheduler.SchedulerInstanceId, triggers, effects = ledger.Fires(), attempts = ledger.Fires(true) };
    }

    internal async Task<object> Apply(Plan plan, bool interrupt)
    {
        var state = ledger.State();
        if (state.Revision != plan.Revision || Format.Hash(Format.Write(state.Manifest)) != plan.ManifestHash ||
            plan.OldRulesHash != Rules.Hash(manifest.PreviousVersion) || plan.NewRulesHash != Rules.Hash(manifest.CandidateVersion))
            throw new InvalidOperationException("Inputs changed after approval.");
        var effects = ledger.Fires().ToDictionary(x => x.Id);
        var pending = new Dictionary<string,string>();
        var changes = new List<string>();
        var alreadyApplied = new List<string>();
        // Validate the entire observed state before making a new write. A completed one-shot may
        // be absent. A missing unfired trigger is a conflict, never permission to recreate work.
        foreach (var row in plan.Rows)
        {
            string id = row.Occurrence.Id;
            var due = await Pending(id);
            if (effects.TryGetValue(id, out var fire))
            {
                if (fire.Scheduled != row.Target || due != null)
                    throw new InvalidOperationException($"Conflicting execution or pending trigger for {id}.");
                continue;
            }
            if (row.Decision == "protect" || row.Target == null || due == null || (due != row.Stored && due != row.Target))
                throw new InvalidOperationException($"Unreviewed trigger state for {id}.");
            pending.Add(id, row.Target);
            if (row.Decision == "move" && due == row.Target) alreadyApplied.Add(id);
            if (due != row.Target) changes.Add(id);
        }
        foreach (var id in changes)
        {
            var rescheduled = await scheduler.RescheduleJob(new TriggerKey(id), Trigger(id, pending[id]));
            if (rescheduled == null || Format.Utc(rescheduled.Value) != pending[id])
                throw new InvalidOperationException($"Failed to persist approved instant for {id}.");
            if (interrupt)
            {
                // A parent can terminate this process, without a graceful shutdown, at this exact durable boundary.
                Console.WriteLine(Format.Write(new { checkpoint = "trigger-committed", id, after = pending[id], worker = scheduler.SchedulerInstanceId }));
                Console.Out.Flush();
                await Task.Delay(Timeout.InfiniteTimeSpan);
            }
        }
        if (interrupt) throw new InvalidOperationException("No pending migration to interrupt.");
        await Dispatch(pending.Select(kv => (kv.Key, kv.Value)));
        return new { changed = changes, alreadyApplied, observed = await Inspect() };
    }

    async Task Dispatch(IEnumerable<(string Id, string Due)> entries)
    {
        var groups = entries.GroupBy(x => x.Due).OrderBy(g => Format.Time(g.Key)).ToArray();
        if (groups.Length == 0) return;
        await scheduler.Start();
        foreach (var group in groups)
        {
            var due = Format.Time(group.Key);
            if (due < clock.GetUtcNow()) throw new InvalidOperationException("Clock would move backward.");
            clock.SetUtcNow(due);
            var remaining = group.Select(x => x.Id).ToHashSet();
            var deadline = Stopwatch.StartNew();
            while (remaining.Count > 0)
            {
                foreach (var fire in ledger.Fires())
                    if (remaining.Contains(fire.Id) && await Pending(fire.Id) == null) remaining.Remove(fire.Id);
                if (remaining.Count == 0) break;
                if (deadline.Elapsed > TimeSpan.FromSeconds(12))
                    throw new TimeoutException($"Quartz did not finish: {string.Join(", ", remaining)}.");
                await Task.Delay(15);
            }
        }
        await scheduler.Standby();
    }
}
