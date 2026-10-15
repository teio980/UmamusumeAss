"""Compare every Career page, action binding and reachable task against main.

This is a structural audit, not proof of live behavior. Pixel/action replays must
also exercise monitor exits and the coordinator's subsequent observations.
"""
import argparse
import io
import json
import re
import subprocess
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
RETIRED_PAGES = {"race_attributes": "race_runner"}
REPLACED_TASKS = {
    "race_attributes_race_start_playback": "race_runner_entry_view_results",
    "race_playback_race_play": "race_runner_playback_start",
    "race_playback_settings_race_playback_settings_ok": "race_runner_playback_ok",
    "race_live_race_live_next": "race_runner_playback_result_monitor",
    "race_result_result_next": "race_runner_result_next",
    "race_runner_view_results_tap": "race_runner_view_results_initial_clicks",
}
LINKS = ("next", "onErrorNext", "exceededNext", "sub", "monitorTasks", "successTasks",
         "alternativeTemplateTasks")


def git(*args):
    return subprocess.check_output(["git", *args], cwd=ROOT)


def collect(files):
    screens, tasks = {}, {}
    for path, document in files.items():
        if path.endswith("/profile.json"):
            for screen in document.get("screens", []):
                assert screen["screenId"] not in screens, screen["screenId"]
                screens[screen["screenId"]] = screen
        if path.endswith("/execution.json"):
            for name, task in document.get("tasks", {}).items():
                assert name not in tasks, name
                tasks[name] = task
    return screens, tasks


def references(task):
    result = [name for key in LINKS for name in task.get(key, [])]
    if task.get("successTask"):
        result.append(task["successTask"])
    return result


def reachable(tasks, entry):
    pending, seen = [entry], set()
    while pending:
        name = pending.pop()
        if name in seen:
            continue
        assert name in tasks, f"Undefined task: {name}"
        seen.add(name)
        pending.extend(references(tasks[name]))
    return sorted(seen)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--base", default="main")
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    commit = git("rev-parse", args.base).decode().strip()
    paths = [path for path in git("ls-tree", "-r", "--name-only", commit,
                                 "resource/hachimi/career").decode().splitlines()
             if path.endswith(("/profile.json", "/execution.json"))]
    with zipfile.ZipFile(io.BytesIO(git("archive", "--format=zip", commit, *paths))) as archive:
        baseline = {path: json.loads(archive.read(path)) for path in paths}
    current = {path.relative_to(ROOT).as_posix(): json.loads(path.read_text(encoding="utf-8-sig"))
               for path in sorted((ROOT / "resource/hachimi/career").glob("**/*.json"))
               if path.name in {"profile.json", "execution.json"} and "testdata" not in path.parts}
    old_screens, old_tasks = collect(baseline)
    screens, tasks = collect(current)
    assert old_screens.keys() - RETIRED_PAGES.keys() <= screens.keys(), "Removed Career page"
    for retired, replacement in RETIRED_PAGES.items():
        assert retired not in screens and replacement in screens, f"Unfinished page consolidation: {retired}"
    removed_tasks = old_tasks.keys() - tasks.keys()
    assert removed_tasks <= REPLACED_TASKS.keys(), f"Removed execution tasks without replacements: {removed_tasks - REPLACED_TASKS.keys()}"
    for retired in removed_tasks:
        assert REPLACED_TASKS[retired] in tasks, f"Missing replacement for {retired}"
    for name, task in tasks.items():
        assert all(target in tasks for target in references(task)), name
    loader = (ROOT / "src/UmaAssistantCore/TaskEngine/HachimiPipelineLoader.cpp").read_text()
    supported = set(re.findall(r'"([A-Za-z][A-Za-z0-9_]*)"', loader))
    used = {field for task in tasks.values() for field in task}
    assert used <= supported, f"Unparsed execution fields: {used - supported}"
    differences = {}
    for name, old in old_tasks.items():
        if name in removed_tasks:
            continue
        now = tasks[name]
        delta = {key: {"main": old.get(key), "current": now.get(key)}
                 for key in sorted(old.keys() | now.keys()) if old.get(key) != now.get(key)}
        if delta:
            differences[name] = delta
    recognition_changes = {sid: {"main": old["recognition"], "current": screens[sid]["recognition"]}
                           for sid, old in old_screens.items()
                           if sid not in RETIRED_PAGES and old.get("recognition") != screens[sid].get("recognition")}
    rows = []
    for sid, screen in sorted(old_screens.items()):
        if sid in RETIRED_PAGES:
            continue
        now = {action["semanticId"]: action["task"] for action in screens[sid].get("actions", [])}
        for action in screen.get("actions", []):
            semantic, old_entry = action["semanticId"], action["task"]
            entry = REPLACED_TASKS.get(old_entry, old_entry)
            assert now.get(semantic) == entry, f"Changed binding: {sid}.{semantic}"
            graph = reachable(tasks, entry)
            rows.append({"screen": sid, "action": semantic, "main_task": old_entry, "task": entry,
                         "reachable_tasks": graph,
                         "changed_tasks": [name for name in graph if name in differences],
                         "new_tasks": [name for name in graph if name not in old_tasks],
                         "outcomes": sorted({tasks[name]["outcome"] for name in graph
                                             if tasks[name].get("outcome")})})
    monitors = {name: {"main_success": ([old_tasks[name]["successTask"]]
                                        if old_tasks.get(name, {}).get("successTask") else [])
                                      + old_tasks.get(name, {}).get("successTasks", []),
                       "current_success": ([task["successTask"]] if task.get("successTask") else [])
                                          + task.get("successTasks", []),
                       "actions": task.get("monitorTasks", [])}
                for name, task in sorted(tasks.items()) if task.get("algorithm") == "ParallelMonitor"}
    report = {"baseline": commit,
              "counts": {"main_pages": len(old_screens), "current_pages": len(screens),
                         "audited_actions": len(rows), "main_tasks": len(old_tasks),
                         "current_tasks": len(tasks), "changed_tasks": len(differences)},
              "added_pages": sorted(screens.keys() - old_screens.keys()),
              "retired_pages": {sid: replacement for sid, replacement in RETIRED_PAGES.items() if sid in old_screens},
              "replaced_tasks": {name: REPLACED_TASKS[name] for name in sorted(removed_tasks)},
              "added_tasks": sorted(tasks.keys() - old_tasks.keys()),
              "changed_tasks": differences, "recognition_changes": recognition_changes,
              "monitors": monitors, "actions": rows,
              "limits": "Structural parity only; does not assert live playthrough or behavioral equivalence."}
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"baseline": commit, **report["counts"], "monitors": monitors}, indent=2))


if __name__ == "__main__":
    main()
