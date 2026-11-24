"""Exercise the public CLI in fresh processes; optionally publish its captured receipts."""
import argparse
import datetime as dt
import json
from pathlib import Path
import selectors
import sqlite3
import subprocess
import tempfile
import time

ROOT = Path(__file__).resolve().parents[1]
CLI = ["dotnet", str(ROOT / "src/bin/Release/net10.0/Rehearsal.dll")]
SCENARIOS = ROOT / "scenarios"


def call(db, verb, *args, error=None):
    command = [*CLI, verb, str(db), *map(str, args)]
    result = subprocess.run(command, capture_output=True, text=True, timeout=25)
    if error is not None:
        assert result.returncode != 0 and error in result.stderr, (verb, result)
        return result.stderr.strip()
    assert result.returncode == 0, (verb, result.stdout, result.stderr)
    return json.loads(result.stdout)


def raw_triggers(db):
    # Quartz 4 persists UTC .NET ticks. Query its own tables without using the app's inspection API.
    with sqlite3.connect(db) as connection:
        def utc(ticks):
            instant = dt.datetime(1, 1, 1) + dt.timedelta(microseconds=ticks // 10)
            return instant.isoformat() + "Z"
        return {name: utc(ticks) for name, ticks in connection.execute(
            "select trigger_name, next_fire_time from qrtz_triggers where next_fire_time is not null order by trigger_name")}


def kill_after_commit(db):
    with subprocess.Popen([*CLI, "interrupt", str(db)], stdout=subprocess.PIPE, stderr=subprocess.PIPE) as worker:
        try:
            # Wait for the final line of a flushed JSON checkpoint with an actual wall-time timeout.
            captured = b""
            with selectors.DefaultSelector() as selector:
                selector.register(worker.stdout, selectors.EVENT_READ)
                deadline = time.monotonic() + 15
                while not captured.rstrip().endswith(b"}"):
                    if not selector.select(max(0, deadline - time.monotonic())):
                        raise TimeoutError("Worker never reached durable checkpoint")
                    chunk = worker.stdout.read1()
                    if not chunk:
                        raise RuntimeError(worker.stderr.read().decode())
                    captured += chunk
            marker = json.loads(captured)
        finally:
            worker.kill()
            worker.wait(timeout=5)
        assert worker.returncode != 0
        return {**marker, "exitStatus": worker.returncode}


def logical_outcome(observed):
    return {row["id"]: row["scheduled"] for row in observed["effects"]}


def check_history_immutable(db):
    with sqlite3.connect(db) as connection:
        for stmt in ["update effect set fired='rewritten'", "delete from effect"]:
            try:
                connection.execute(stmt)
                raise AssertionError("History mutation unexpectedly succeeded")
            except sqlite3.IntegrityError as error:
                assert "immutable" in str(error)


def exercise(folder, manifest_path):
    manifest = json.loads(manifest_path.read_text())
    scenario_id = manifest["id"]
    records = {}
    for policy in ("intent", "keep", "wall"):
        db = folder / f"{scenario_id}-{policy}.db"
        seeded = call(db, "init", manifest_path)
        original_triggers = raw_triggers(db)
        review = call(db, "preview", policy)
        call(db, "resume", error="Approval required")
        approved = call(db, "approve", review["hash"], policy)
        assert approved == call(db, "approve", review["hash"], policy)
        check_history_immutable(db)
        if policy == "intent":
            killed = kill_after_commit(db)
            partial = raw_triggers(db)
            changed = {k for k in original_triggers if partial[k] != original_triggers[k]}
            assert changed == {killed["id"]} and partial[killed["id"]] == killed["after"]
            history_after_kill = call(db, "inspect")["effects"]
            assert history_after_kill == seeded["effects"]
        resumed = call(db, "resume")
        observed = resumed["observed"]
        again = call(db, "resume")
        assert again["observed"]["attempts"] == observed["attempts"]
        assert again["observed"]["effects"] == observed["effects"]
        assert len(observed["attempts"]) == len(observed["effects"]) == len(review["plan"]["rows"])
        assert [x for x in observed["effects"] if x["worker"] == seeded["worker"]] == seeded["effects"]
        assert len(set(x["fireInstance"] for x in observed["attempts"])) == len(observed["attempts"])
        assert all(x["scheduled"] == x["fired"] for x in observed["attempts"])
        assert raw_triggers(db) == {}
        assert logical_outcome(observed) == {r["occurrence"]["id"]: r["target"] for r in review["plan"]["rows"]}
        records[policy] = {"hash": review["hash"], "scheduled": logical_outcome(observed), "callbacks": len(observed["attempts"])}
        if policy == "intent":
            assert killed["id"] in resumed["alreadyApplied"]
            assert killed["worker"] != observed["worker"] != seeded["worker"]
            main = {"manifest": manifest, "review": review, "approval": approved,
                    "initialTriggers": original_triggers, "history": seeded["effects"],
                    "interrupt": killed, "interruptedTriggers": partial,
                    "resumed": resumed, "secondReplayCallbacks": len(again["observed"]["attempts"]) - len(observed["attempts"])}
    # Gold expectations authored from the IANA release and the two operators' contracts;
    # the second manifest uses the later instant when the old rules repeat 00:30.
    target = records["intent"]["scheduled"]
    expected_local = "13:00:00" if scenario_id == "morning" else "04:30:00"
    expected_fixed = "14:00:00" if scenario_id == "morning" else "05:30:00"
    for day in manifest["pendingDays"]:
        assert target[f"local/{day}"] == f"{day}T{expected_local}Z"
        assert target[f"fixed/{day}"] == f"{day}T{expected_fixed}Z"
    if scenario_id == "overnight":
        occurrence = next(r["occurrence"] for r in main["review"]["plan"]["rows"] if r["decision"] == "move")
        assert occurrence["oldMappings"] == ["2026-11-01T04:30:00Z", "2026-11-01T05:30:00Z"]
        assert occurrence["newMappings"] == ["2026-11-01T04:30:00Z"]
    for policy, record in records.items():
        record["missedIntent"] = [id_ for id_, when in record["scheduled"].items() if when != target[id_]]
        assert len(record["missedIntent"]) == (0 if policy == "intent" else len(manifest["pendingDays"]))
    return {"formatVersion": 1, "recordedUtc": dt.datetime.now(dt.timezone.utc).isoformat(timespec="seconds"),
            "scheduler": "Quartz.NET 4.8.1", "store": "SQLite ADO job store", **main, "policyRuns": records}


def edit_and_conflict(folder):
    db = folder / "edit.db"
    call(db, "init", SCENARIOS / "morning.json")
    before = call(db, "preview")
    changed = call(db, "edit-local", "09:15")
    old_error = call(db, "approve", before["hash"], error="Stale review hash")
    after = call(db, "preview")
    assert changed["revision"] == 2 and before["hash"] != after["hash"]
    assert after["plan"]["rows"][2]["target"] == "2026-11-02T13:15:00Z"
    call(db, "approve", after["hash"])
    call(db, "edit-local", "11:00", error="already sealed")
    # Emulate a writer outside the supported lock after approval, on the *second* local trigger.
    # Assert the first trigger has not been rescheduled when preflight discovers the edit.
    with sqlite3.connect(db) as connection:
        connection.execute("update qrtz_triggers set next_fire_time = next_fire_time + 600000000 where trigger_name='local/2026-11-03'")
    tampered = raw_triggers(db)
    conflict = call(db, "resume", error="Unreviewed trigger state")
    assert raw_triggers(db) == tampered
    return {"editedLocalTime": "09:15", "originalHash": before["hash"], "newHash": after["hash"],
            "newTarget": after["plan"]["rows"][2]["target"], "staleApproval": old_error, "postApprovalConflict": conflict}


def edge_cases(folder):
    db = folder / "winnipeg.db"
    call(db, "init", ROOT / "fixtures/winnipeg.json")
    checked = call(db, "preview")
    row = next(r for r in checked["plan"]["rows"] if r["decision"] == "move")
    assert row["stored"] == "2026-11-02T15:00:00Z" and row["target"] == "2026-11-02T14:00:00Z"
    call(db, "approve", checked["hash"])
    fired = call(db, "resume")["observed"]
    assert logical_outcome(fired)["fixed/2026-11-02"] == row["stored"]
    assert logical_outcome(fired)["local/2026-11-02"] == row["target"]
    assert len(fired["attempts"]) == 4

    config = json.loads((SCENARIOS / "morning.json").read_text())
    path = folder / "edge.json"
    config.update(zone="America/New_York", historyDay="2026-03-06", pendingDays=["2026-03-08"], localTime="02:30")
    path.write_text(json.dumps(config))
    call(folder / "gap.db", "init", path, error="No old local instant exists")
    config.update(historyDay="2026-10-30", pendingDays=["2026-11-02"], localTime="09:00")
    path.write_text(json.dumps(config))
    db = folder / "control.db"
    call(db, "init", path)
    control = call(db, "preview")
    assert [r["decision"] for r in control["plan"]["rows"]] == ["protect", "protect", "keep", "keep"]
    call(db, "approve", control["hash"])
    assert len(call(db, "resume")["observed"]["effects"]) == 4

    config.update(zone="Australia/Lord_Howe", historyDay="2026-04-03", pendingDays=["2026-04-05"], localTime="01:45")
    path.write_text(json.dumps(config))
    db = folder / "half-hour-fold.db"
    call(db, "init", path)
    half = call(db, "preview")["plan"]["rows"][2]
    assert half["occurrence"]["oldMappings"] == half["occurrence"]["newMappings"] == [
        "2026-04-04T14:45:00Z", "2026-04-04T15:15:00Z"]
    assert half["target"] == "2026-04-04T15:15:00Z"

    config.update(historyDay="2026-10-01", pendingDays=["2026-10-04"], localTime="02:15")
    path.write_text(json.dumps(config))
    half_gap = call(folder / "half-hour-gap.db", "init", path, error="No old local instant exists")
    config.update(zone="America/Havana", historyDay="2027-03-12", pendingDays=["2027-03-14"], localTime="00:30",
                  previousVersion="2026f", candidateVersion="2026e")
    path.write_text(json.dumps(config))
    db = folder / "new-gap.db"
    call(db, "init", path)
    gap = call(db, "preview")
    assert gap["plan"]["rows"][2]["decision"] == "blocked"
    gap_error = call(db, "approve", gap["hash"], error="pending target is missing")
    return {"winnipeg": {"releasePair": ["2026d","2026e"], "reviewed": row, "observed": fired},
            "lordHowe": {"fold": half, "gapRejected": half_gap}, "havanaRollback": {"releasePair": ["2026f","2026e"], "gap": gap["plan"]["rows"][2], "error": gap_error}}


def normalized(value):
    # The OS clock and per-process Quartz IDs change on a fresh run. Preserve the role
    # responsible for each callback and compare every other recorded value.
    if isinstance(value, list):
        return [normalized(item) for item in value]
    if not isinstance(value, dict):
        return value
    stable = {}
    for key, item in value.items():
        if key in ("recordedUtc", "fireInstance"):
            continue
        if key == "worker":
            stable[key] = item.split("-", 1)[0]
        else:
            stable[key] = normalized(item)
    return stable


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--write-receipts", action="store_true")
    options = parser.parse_args()
    with tempfile.TemporaryDirectory(prefix="schedule-rehearsal-") as tmp:
        folder = Path(tmp)
        receipts = {path.stem: exercise(folder, path) for path in sorted(SCENARIOS.glob("*.json"))}
        edit = edit_and_conflict(folder)
        edges = edge_cases(folder)
        for name, data in {**receipts, "edit": edit, "controls": edges}.items():
            path = ROOT / "web/receipts" / f"{name}.json"
            if options.write_receipts:
                path.write_text(json.dumps(data, indent=2) + "\n")
            else:
                assert normalized(json.loads(path.read_text())) == normalized(data), f"Published receipt drift: {name}"
        for name, data in receipts.items():
            print(f"{name}: {data['policyRuns']['intent']['callbacks']} callbacks, retained {len(data['history'])} history rows, "
                  f"{data['secondReplayCallbacks']} extra callbacks on replay; all three scheduling policies exercised.")
        print("Passed stale approval, edited trigger, immutable history, Winnipeg 2026d/e fires, half-hour fold and gap, candidate-only gap and unchanged-zone checks.")


if __name__ == "__main__":
    main()
