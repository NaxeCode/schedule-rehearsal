using Rehearsal;
using System.Globalization;

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;

try
{
    if (args.Length < 2)
        throw new ArgumentException("Usage: init DB MANIFEST | preview DB [intent|keep|wall] | edit-local DB HH:mm | approve DB HASH [intent|keep|wall] | interrupt DB | resume DB | inspect DB");
    var verb = args[0];
    var path = Path.GetFullPath(args[1]);
    // Serialize supported operator commands across processes, including the gap between two Quartz APIs.
    using var lease = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    var db = new Ledger(path);
    if (verb == "init" && args.Length == 3)
    {
        var input = Format.Read<Manifest>(File.ReadAllText(args[2]));
        var rows = Rules.Expand(input);
        using (File.Open(path, FileMode.CreateNew, FileAccess.Write)) { }
        db.Create(input);
        var start = rows.Min(r => Format.Time(r.Original)).AddSeconds(-1);
        Console.WriteLine(Format.Write(await Runner.WithScheduler(db, input, "seed", start, r => r.Seed())));
        return 0;
    }
    if (!File.Exists(path)) throw new FileNotFoundException("Initialize a new rehearsal database first.");
    var state = db.State();
    if (!state.Ready) throw new InvalidOperationException("Initialization did not finish. Initialize a fresh file.");
    var manifest = state.Manifest;
    var frozen = state.Approved;
    if (verb == "edit-local" && args.Length == 3)
    {
        var revised = manifest with { LocalTime = args[2] };
        _ = Rules.Expand(revised);
        db.Change("UPDATE input SET manifest=@p0, revision=revision+1 WHERE approved IS NULL", Format.Write(revised));
        Console.WriteLine(Format.Write(new { revision = state.Revision + 1, localTime = args[2] }));
        return 0;
    }
    // A fresh process starts its clock before the earliest approved target, including subsequent
    // invocations after a partial run. Already recorded occurrences are skipped by reconciliation.
    var startAt = (frozen?.Rows.Where(r => r.Decision != "protect" && r.Target != null)
        .Select(r => Format.Time(r.Target!)).DefaultIfEmpty().Min()
        ?? Format.Time(Rules.Expand(manifest)[0].Original)).AddSeconds(-1);
    var result = await Runner.WithScheduler<object>(db, manifest, verb, startAt, async runner =>
    {
        switch (verb, args.Length)
        {
            case ("preview", 2 or 3):
                var preview = frozen ?? await runner.Preview(state.Revision, args.ElementAtOrDefault(2) ?? "intent");
                return new { hash = preview.Fingerprint(), approved = frozen != null, plan = preview };
            case ("approve", 3 or 4):
                var requestedPolicy = args.ElementAtOrDefault(3) ?? "intent";
                if (frozen != null && frozen.Policy != requestedPolicy) throw new InvalidOperationException("A different policy is sealed.");
                var plan = frozen ?? await runner.Preview(state.Revision, requestedPolicy);
                if (plan.Fingerprint() != args[2]) throw new InvalidOperationException("Stale review hash. Preview the current revision.");
                if (plan.Rows.Any(x => x.Decision == "blocked")) throw new InvalidOperationException("A pending target is missing or falls inside protected history. Edit the intent and review again.");
                if (frozen == null) db.Change("UPDATE input SET approved=@p0 WHERE revision=@p1 AND approved IS NULL", Format.Write(plan), state.Revision);
                return new { hash = plan.Fingerprint(), approved = true };
            case ("interrupt", 2):
            case ("resume", 2):
                return await runner.Apply(frozen ?? throw new InvalidOperationException("Approval required."), verb == "interrupt");
            case ("inspect", 2):
                return await runner.Inspect();
            default:
                throw new ArgumentException("Unknown command or argument count.");
        }
    });
    Console.WriteLine(Format.Write(result));
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine(error.GetBaseException().Message);
    return 1;
}
