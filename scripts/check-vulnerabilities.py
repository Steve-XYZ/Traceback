#!/usr/bin/env python3
"""Fail closed when dotnet package-audit JSON reports a vulnerability."""

from __future__ import annotations

import json
import sys
from collections.abc import Iterator
from typing import Any


def load_report(path: str | None) -> Any:
    try:
        if path is None:
            return json.load(sys.stdin)
        with open(path, encoding="utf-8") as report_file:
            return json.load(report_file)
    except (OSError, json.JSONDecodeError) as error:
        raise ValueError(f"invalid JSON report: {error}") from error


def vulnerability_nodes(node: Any, location: str = "$") -> Iterator[tuple[dict[str, Any], list[Any], str]]:
    if isinstance(node, dict):
        if "vulnerabilities" in node:
            vulnerabilities = node["vulnerabilities"]
            if not isinstance(vulnerabilities, list):
                raise ValueError(f"{location}.vulnerabilities must be an array")
            if vulnerabilities:
                yield node, vulnerabilities, location
        for key, value in node.items():
            if key != "vulnerabilities":
                yield from vulnerability_nodes(value, f"{location}.{key}")
    elif isinstance(node, list):
        for index, value in enumerate(node):
            yield from vulnerability_nodes(value, f"{location}[{index}]")


def validate_report_shape(report: Any) -> None:
    if not isinstance(report, dict):
        raise ValueError("report must be a JSON object")
    if report.get("version") != 1:
        raise ValueError("report version must be 1")
    if not isinstance(report.get("projects"), list):
        raise ValueError("report projects must be an array")


def describe(package: dict[str, Any], vulnerability: Any, location: str) -> str:
    package_id = package.get("id", "<unknown package>")
    version = package.get("resolvedVersion", package.get("requestedVersion", "<unknown version>"))
    if isinstance(vulnerability, dict):
        severity = vulnerability.get("severity", "unknown severity")
        advisory = vulnerability.get("advisoryurl", vulnerability.get("advisoryUrl", "no advisory URL"))
        return f"{package_id} {version}: {severity} ({advisory}) at {location}"
    return f"{package_id} {version}: {vulnerability!r} at {location}"


def main(arguments: list[str]) -> int:
    if len(arguments) > 1:
        print(f"usage: {sys.argv[0]} [report.json]", file=sys.stderr)
        return 2
    try:
        report = load_report(arguments[0] if arguments else None)
        validate_report_shape(report)
        findings = [
            describe(package, vulnerability, location)
            for package, vulnerabilities, location in vulnerability_nodes(report)
            for vulnerability in vulnerabilities
        ]
    except ValueError as error:
        print(f"vulnerability audit failed closed: {error}", file=sys.stderr)
        return 2

    if findings:
        print("Vulnerable packages found:", file=sys.stderr)
        for finding in findings:
            print(f"- {finding}", file=sys.stderr)
        return 1

    print("No vulnerable packages found.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
