"""Asserts one leg: the bug leg must show every scenario stuck, the other legs must show every scenario clean."""
import json
import os
import sys

mode, log_path, json_path = sys.argv[1:4]
expect_bug = mode == "bug"
marker = f"C11-REPRO-DONE mode={mode} scenarios=4"

rows = []
failures = []
try:
    with open(log_path, encoding="utf-8") as log:
        marker_found = marker in log.read()
except OSError:
    marker_found = False
    failures.append(f"log file missing: {log_path}")

if not marker_found:
    failures.append(f"marker line missing: {marker}")

maui_version = "unknown"
try:
    with open(json_path, encoding="utf-8") as results_file:
        results = json.load(results_file)
    scenarios = results["scenarios"]
    maui_version = results.get("mauiVersion", "unknown")
except (OSError, ValueError, KeyError):
    scenarios = []
    failures.append(f"results file missing or unreadable: {json_path}")

if marker_found and len(scenarios) != 4:
    failures.append(f"expected 4 scenarios, found {len(scenarios)}")

for index, scenario in enumerate(scenarios):
    observed, expected = scenario["Observed"], scenario["Expected"]
    thrown, escaped = scenario["ThrownCount"], scenario["EscapedToCaller"]
    queued, applying = scenario["QueuedWritesNeverApplied"], scenario["ApplyingStuck"]
    problems = []
    if mode == "bug":
        if observed != scenario["StuckValue"]:
            problems.append(f"observed {observed!r}, wanted stuck value {scenario['StuckValue']!r}")
        if thrown != 1:
            problems.append(f"thrown {thrown}, wanted 1")
        if not escaped:
            problems.append("exception did not escape to caller")
        if index < 3 and queued < 1:
            problems.append(f"queued writes {queued}, wanted >= 1")
        if index == 3 and not applying:
            problems.append("_applying not stuck")
    else:
        if observed != expected:
            problems.append(f"observed {observed!r}, wanted {expected!r}")
        if thrown != (0 if mode == "control" else 1):
            problems.append(f"thrown {thrown}, wanted {0 if mode == 'control' else 1}")
        if escaped:
            problems.append("exception escaped to caller")
        if queued != 0:
            problems.append(f"queued writes {queued}, wanted 0")
        if applying:
            problems.append("_applying stuck")
    ok = not problems
    wanted = "stuck (bug shown)" if expect_bug else "updated (clean)"
    rows.append(f"| {scenario['Name']} | {wanted} | observed `{observed}`, expected `{expected}`, thrown {thrown}, escaped {escaped}, queued {queued}, _applying stuck {applying} | {'PASS' if ok else 'FAIL'} |")
    if not ok:
        failures.append(f"{scenario['Name']}: " + "; ".join(problems))

summary = [f"### Leg `{mode}`", "", "| Scenario | Expected | Observed | Result |", "|---|---|---|---|", *rows, ""]
if mode == "bug":
    passed = f"PASS: bug reproduced on Microsoft.Maui.Controls.Core {maui_version}. 4/4 scenarios stuck, the exception escaped once, and the next write is queued (1-3) or _applying stays true (4)."
    failed_prefix = "FAIL: bug not reproduced - "
elif mode == "control":
    passed = "PASS: clean. 4/4 scenarios updated, nothing thrown."
    failed_prefix = "FAIL: leg not clean - "
else:
    passed = "PASS: clean. 4/4 updated; the exception was thrown and caught inside the callback."
    failed_prefix = "FAIL: leg not clean - "
summary.append(failed_prefix + "; ".join(failures) if failures else passed)
summary.append("")
summary.append("The workflow is green only when the bug leg reproduces the bug and the control and workaround legs are clean.")
text = "\n".join(summary)
print(text)
summary_path = os.environ.get("GITHUB_STEP_SUMMARY")
if summary_path:
    with open(summary_path, "a", encoding="utf-8") as out:
        out.write(text + "\n")

sys.exit(1 if failures else 0)
