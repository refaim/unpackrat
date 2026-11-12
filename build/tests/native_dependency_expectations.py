"""Apply only the reviewed test-data retargeting; never claim a new scan."""
from __future__ import annotations

from copy import deepcopy


def expectation_path_for_raw_record(record: dict, updates: dict) -> str:
    for change in updates["mappings"]:
        old = change["historical"]
        if (all(record[field] == old[field] for field in ("package", "path", "start_lines", "end_lines"))
                and record["raw_expression"] == old["license"]):
            return change["target"]["path"]
    return record["path"]


def derive_native_findings(historical: dict, updates: dict) -> dict:
    if updates["evidence_kind"] != "test-only-derived-expectations-not-a-scan" or len(updates["mappings"]) != 3:
        raise ValueError("Expected exactly three reviewed derived expectations.")
    result = deepcopy(historical)
    for change in updates["mappings"]:
        if set(change["target"]) != {"package", "path"}:
            raise ValueError("A mapping may change only package and path.")
        matches = [row for row in result["findings"] if row == change["historical"]]
        if len(matches) != 1:
            raise ValueError("A mapping requires one exact historical observation.")
        matches[0].update(change["target"])
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
