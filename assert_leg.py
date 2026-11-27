"""Asserts one leg: the bug leg must show every scenario stuck, the other legs must show every scenario clean."""
import json
import os
import sys

mode, log_path, json_path = sys.argv[1:4]
expect_bug = mode == "bug"
marker = f"C11-REPRO-DONE mode={mode} scenarios=4"

with open(log_path, encoding="utf-8") as log:
    marker_found = marker in log.read()

rows = []
failures = []
if not marker_found:
    failures.append(f"marker line missing: {marker}")

try:
    with open(json_path, encoding="utf-8") as results_file:
        scenarios = json.load(results_file)["scenarios"]
except OSError:
    scenarios = []
    failures.append(f"results file missing: {json_path}")

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
summary.append("Leg result: " + ("FAIL - " + "; ".join(failures) if failures else "PASS"))
text = "\n".join(summary)
print(text)
summary_path = os.environ.get("GITHUB_STEP_SUMMARY")
if summary_path:
    with open(summary_path, "a", encoding="utf-8") as out:
        out.write(text + "\n")

sys.exit(1 if failures else 0)
