"""Apply only the reviewed test-data retargeting; never claim a new scan."""
from __future__ import annotations

from copy import deepcopy


def expectation_for_raw_record(record: dict, updates: dict) -> dict:
    for change in updates["mappings"]:
        old = change["historical"]
        if (all(record[field] == old[field] for field in ("package", "path", "start_lines", "end_lines"))
                and record["raw_expression"] == old["license"]):
            return dict(record, **change["target"])
    return dict(record)


def expectation_path_for_raw_record(record: dict, updates: dict) -> str:
    return expectation_for_raw_record(record, updates)["path"]


def derive_native_findings(historical: dict, updates: dict) -> dict:
    if (updates["evidence_kind"] != "test-only-derived-expectations-not-a-scan"
            or len(updates["mappings"]) != 13 or len(updates["retired"]) != 1):
        raise ValueError("Expected thirteen reviewed relocations and one removed source.")
    result = deepcopy(historical)
    for change in updates["mappings"]:
        target, original = change["target"], change["historical"]
        if set(target) not in ({"package", "path"}, {"package", "path", "start_lines", "end_lines"}):
            raise ValueError("A mapping may change only source coordinates.")
        if ("start_lines" in target
                and target["end_lines"] - target["start_lines"] != original["end_lines"] - original["start_lines"]):
            raise ValueError("A relocation must preserve the reviewed text length.")
        matches = [row for row in result["findings"] if row == change["historical"]]
        if len(matches) != 1:
            raise ValueError("A mapping requires one exact historical observation.")
        matches[0].update(change["target"])
    for change in updates["retired"]:
        matches = [row for row in result["findings"] if row == change["historical"]]
        if len(matches) != 1:
            raise ValueError("A removed source requires one exact historical observation.")
        result["findings"].remove(matches[0])
    for change in updates["protected_prefixes"]:
        package, before, after = change["package"], change["historical"], change["target"]
        if result["prefixes"][package] != before:
            raise ValueError("The historical protected prefix changed.")
        result["prefixes"][package] = after
        for row in result["protected"]:
            if row["package"] == package:
                if not row["path"].startswith(before):
                    raise ValueError("A protected path escaped its historical prefix.")
                row["path"] = after + row["path"][len(before):]
    result["evidence_kind"] = updates["evidence_kind"]
    return result
