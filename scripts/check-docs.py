#!/usr/bin/env python3
"""Checks the documentation of the SPT Data Archive.

Errors (exit code 1):
  * A .feature file or a ```gherkin block in a Markdown file does not parse.
  * A relative Markdown link points to a file that does not exist.
  * An ADR is missing a required header field or section.
  * The ADR index does not list an ADR, or lists a different status than the ADR file.

Warnings (exit code 0):
  * A prose sentence has more than 25 words (Simplified Technical English).

Usage:   python3 scripts/check-docs.py
Needs:   pip install gherkin-official
"""
from __future__ import annotations

import re
import sys
from pathlib import Path

try:
    from gherkin.parser import Parser
except ImportError:
    sys.exit("Missing dependency: pip install gherkin-official")

ROOT = Path(__file__).resolve().parent.parent
MARKDOWN = sorted([*ROOT.glob("*.md"), *(ROOT / "docs").rglob("*.md")])
FEATURES = sorted((ROOT / "docs").rglob("*.feature"))
ADR_DIR = ROOT / "docs" / "adr"

ADR_FILE = re.compile(r"^(\d{4})-.+\.md$")
ADR_SECTIONS = ["Context", "Decision", "Options Considered", "Trade-off Analysis",
                "Consequences", "Verification", "Action Items", "Links"]
MAX_WORDS = 25

errors: list[str] = []
warnings: list[str] = []


def rel(path: Path) -> str:
    return str(path.relative_to(ROOT))


def parse_gherkin(text: str, where: str) -> None:
    try:
        Parser().parse(text)
    except Exception as exc:  # the parser raises several exception types
        errors.append(f"{where}: Gherkin does not parse: {str(exc).splitlines()[0] if str(exc) else exc}")


def check_gherkin() -> None:
    for feature in FEATURES:
        parse_gherkin(feature.read_text(encoding="utf-8"), rel(feature))
    for md in MARKDOWN:
        text = md.read_text(encoding="utf-8")
        for index, block in enumerate(re.findall(r"```gherkin\n(.*?)```", text, re.S), 1):
            parse_gherkin(block, f"{rel(md)} (gherkin block {index})")


def check_links() -> None:
    link = re.compile(r"\[[^\]]*\]\(([^)\s]+)\)")
    for md in MARKDOWN:
        in_code = False
        for number, line in enumerate(md.read_text(encoding="utf-8").splitlines(), 1):
            if line.startswith("```"):
                in_code = not in_code
            if in_code:
                continue
            for target in link.findall(line):
                if re.match(r"^(https?:|mailto:|#)", target):
                    continue
                path = (md.parent / target.split("#")[0]).resolve()
                if not path.exists():
                    errors.append(f"{rel(md)}:{number}: broken link to '{target}'")


def adr_status(text: str) -> str:
    match = re.search(r"^\*\*Status:\*\*\s*(.+)$", text, re.M)
    return match.group(1).strip() if match else ""


def check_adrs() -> None:
    index_text = (ADR_DIR / "README.md").read_text(encoding="utf-8")
    for adr in sorted(ADR_DIR.glob("*.md")):
        match = ADR_FILE.match(adr.name)
        if not match or match.group(1) == "0000":
            continue
        text = adr.read_text(encoding="utf-8")
        number = match.group(1)

        if not re.match(rf"^# ADR-{number}: ", text):
            errors.append(f"{rel(adr)}: the title must start with '# ADR-{number}: '")
        for field in ("Status", "Date", "Deciders"):
            if not re.search(rf"^\*\*{field}:\*\*", text, re.M):
                errors.append(f"{rel(adr)}: missing header field '{field}'")
        for section in ADR_SECTIONS:
            if not re.search(rf"^## {re.escape(section)}\s*$", text, re.M):
                errors.append(f"{rel(adr)}: missing section '## {section}'")
        if "```gherkin" not in text:
            errors.append(f"{rel(adr)}: the Verification section has no gherkin block")

        row = re.search(rf"^\| \[{number}\]\([^)]*\) \| .*? \| (.+?) \|$", index_text, re.M)
        if not row:
            errors.append(f"docs/adr/README.md: ADR-{number} is not in the index")
        elif row.group(1).strip() != adr_status(text):
            errors.append(
                f"docs/adr/README.md: status of ADR-{number} is '{row.group(1).strip()}' "
                f"but the ADR says '{adr_status(text)}'"
            )


def check_sentence_length() -> None:
    skip_files = {"diagrams.md", "0000-template.md"}
    for md in MARKDOWN:
        if md.name in skip_files:
            continue
        in_code = False
        for number, line in enumerate(md.read_text(encoding="utf-8").splitlines(), 1):
            if line.startswith("```"):
                in_code = not in_code
                continue
            stripped = line.strip()
            if in_code or not stripped or stripped.startswith(("|", "#", ">", "<!--", "---")):
                continue
            prose = re.sub(r"^([-*]|\d+\.)\s+(\[[ x]\]\s+)?", "", stripped)
            prose = re.sub(r"`[^`]*`", "code", prose)
            prose = re.sub(r"\[([^\]]*)\]\([^)]*\)", r"\1", prose)
            for sentence in re.split(r"(?<=[.!?:])\s+", prose):
                words = len(sentence.split())
                if words > MAX_WORDS:
                    warnings.append(f"{rel(md)}:{number}: {words} words: {sentence[:70]}...")


def main() -> int:
    check_gherkin()
    check_links()
    check_adrs()
    check_sentence_length()

    scenarios = 0
    for feature in FEATURES:
        scenarios += len(re.findall(r"^\s*Scenario( Outline)?:", feature.read_text(encoding="utf-8"), re.M))

    for warning in warnings:
        print(f"WARN  {warning}")
    for error in errors:
        print(f"ERROR {error}")
    print(
        f"\nChecked {len(FEATURES)} feature files ({scenarios} scenarios), "
        f"{len(MARKDOWN)} Markdown files: {len(errors)} errors, {len(warnings)} warnings."
    )
    return 1 if errors else 0


if __name__ == "__main__":
    sys.exit(main())
