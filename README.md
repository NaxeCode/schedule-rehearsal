# Schedule Rehearsal

On November 2, 2026, a 9 a.m. dispatch briefing in Havana was due at **13:00 UTC**. Until Cuba canceled its fallback, the scheduler expected **14:00 UTC**. A settlement export promised for 14:00 UTC still belonged there. This C# lab lets a platform operator inspect those promises, approve a migration and interrupt the worker halfway through. [Walk through the recorded run](https://naxe-portfolio-review.vercel.app/labs/schedule-rehearsal/) · [Open its receipt](web/receipts/morning.json)

**What the worker did.** Quartz.NET had already executed two October jobs and persisted four November triggers in SQLite. The migration replaced the first local trigger, then the harness killed the process. A second process found that change, replaced the remaining local trigger and let Quartz execute all four pending jobs. A third process replayed the same approval. It added zero callbacks.

| Measured in separate databases | Intent-aware migration | Keep every UTC instant | Recompute every wall time |
|---|---:|---:|---:|
| Morning dispatch, pending jobs that missed their promise | 0 / 4 | 2 / 4 | 2 / 4 |
| Midnight handover, pending jobs that missed their promise | 0 / 2 | 1 / 2 | 1 / 2 |

The midnight manifest uses the **later** 00:30 when the old rules repeat that time. The previous UTC options were 04:30 and 05:30. The new rules leave only 04:30. Each authored manifest supplies one local schedule and one fixed UTC schedule. Each policy ran through the same scheduler against a fresh file. [Midnight receipt](web/receipts/overnight.json) · [Scenario manifests](scenarios)

## Run the experiment

Use the .NET 10 SDK and Python 3.9+ on macOS or Linux. The first run restores locked NuGet dependencies. The calendar data is included.

```sh
sh scripts/verify.sh
```

The script builds the CLI, creates temporary databases, terminates actual child processes, checks effects and callbacks separately, reads stored trigger times directly from Quartz’s SQLite tables and compares the results to the published JSON. Clock-of-record timestamps and unique worker IDs vary between runs. To deliberately refresh the published receipts, run `sh scripts/verify.sh --write-receipts`.

To step through the morning manifest yourself, start in the repository root after verifying. This example uses `jq` only to copy the full review hash.

```sh
lab() { dotnet src/bin/Release/net10.0/Rehearsal.dll "$@"; }
db="$(mktemp -d)/morning.db"
lab init "$db" scenarios/morning.json
lab preview "$db"
hash="$(lab preview "$db" | jq -r .hash)"
lab approve "$db" "$hash"
lab interrupt "$db"
# After "trigger-committed" appears, press Ctrl-C.
lab inspect "$db"
lab resume "$db"
lab resume "$db"
```

Run `lab edit-local "$db" 09:15` between preview and approval in a fresh rehearsal to reproduce the stale-review refusal. The operator must preview the new revision and approve its hash. A sealed approval owns the remaining migration in this database. The alternate policies select `keep` or `wall` as the last argument to both `preview` and `approve`. Each policy requires its own file. [Edited-state receipt](web/receipts/edit.json)

Serve the web replay locally with `python3 -m http.server 8000 --directory web`, then open `http://localhost:8000`. It reads the checked-in measurement. All controls are available without an account. The optional browser check uses Playwright 1.65.2 and Chromium. After installing them, run `python3 scripts/browser_smoke.py`. CI runs both the integration and visitor checks.

## How the clock and the commit work

1. **Materialize a bounded schedule.** Noda Time loads the two pinned rule files and maps each selected local date. A fold uses the manifest’s explicit earlier/later choice. A nonexistent old time prevents initialization. When a pending local job has no valid instant under the new rules, the intent-preserving policy blocks approval. For a fixed job, the manifest’s UTC time directly defines its instant. Quartz stores one dated, one-shot trigger per occurrence. A stable job/date key survives a reschedule. [Calendar code](src/Rules.cs) · [Noda Time TZDB guide](https://nodatime.org/3.3.x/userguide/tzdb)
2. **Review the persisted state.** The command reads the scheduler’s triggers and the callback ledger. The plan binds the manifest revision, rule-file hashes, previous trigger times and proposed targets. Approval stores the reviewed plan in SQLite. An edit before approval invalidates the hash. An unexpected change to a reviewed trigger’s next-fire time is rejected before another row moves. Replay permits a trigger already at its approved target. [Review and apply](src/Runner.cs)
3. **Interrupt at a real boundary.** In standby, the migration calls Quartz’s `RescheduleJob`, waits for its durable return and prints a checkpoint. The harness sends SIGKILL while the process is waiting there. After restart the worker reconciles each observed trigger against the reviewed old and new values. A vanished unfired trigger is a conflict. A trigger already moved to its approved instant requires no second replacement. [Process harness](scripts/replay.py) · [Quartz job stores](https://www.quartz-scheduler.net/documentation/quartz-4.x/tutorial/job-stores.html)
4. **Observe the job callback.** Quartz’s `FakeTimeProvider` starts before the first due instant. The harness advances it to each scheduled UTC time, then waits in real time for the Quartz worker. The job stores Quartz’s scheduled time, actual scheduler-clock firing time, fire instance and worker ID. Each invocation and its local effect share one SQLite transaction. The effect has one key per occurrence and refuses updates and deletes. The attempt ledger makes duplicates visible. [Callback ledger](src/Ledger.cs) · [Quartz clock support](https://www.quartz-scheduler.net/documentation/quartz-4.x/tutorial/time-and-timeprovider.html) · [SQLite transaction semantics](https://sqlite.org/transactional.html)

## Controls and limits

A separate **Winnipeg** fixture uses releases **2026d → 2026e**. Its 09:00 local job fired at 14:00 UTC after the change. Its fixed job fired at 15:00 UTC. This fixture tests a second jurisdiction and release pair with real Quartz fires. The two Havana manifests above also exercise the kill-and-replay boundary and all three policies. [Winnipeg fixture](fixtures/winnipeg.json) · [Control receipt](web/receipts/controls.json) · [IANA release notes](https://data.iana.org/time-zones/tzdb/NEWS)

Additional assertions cover New York’s unchanged autumn schedule, Lord Howe’s 30-minute repeated and missing times, and a Havana rollback that would move a pending job into a spring gap. The Lord Howe fold has two options, 14:45 and 15:15 UTC on the preceding date. These are fixed calendar expectations. The new-gap test refuses approval before the pending job can run.

The simulated clock makes a historical day pass in milliseconds. The measured results concern trigger selection, durable state and callback counts. This experiment has one writer and one worker at a time, a finite manifest horizon, a local database side effect and no external delivery. It exercises a kill **after a committed trigger replacement, before execution**. A crash inside a running job, clustered execution, arbitrary recurrence import, physical power loss and end-to-end delivery guarantees remain outside the evidence. Approval and Quartz updates are separate transactions. Reconciliation resolves the tested gap between them.

## Attribution

Quartz supplies scheduling and persistence, Noda Time supplies time-zone conversion, and IANA and Unicode supply rule and mapping data. The original contribution is the intent model, review and restart protocol, process-level experiment and recorded visitor replay. The input hashes and upstream notices accompany the source. [Credits](NOTICE) · [Upstream licenses](licenses)
