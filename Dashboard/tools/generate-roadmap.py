#!/usr/bin/env python3
"""Writes the learning path of labs.json into the training roadmap (specs/learning-path-stages).

labs.json is the single source of truth of the path: its stages, and the order of its labs. The roadmap
(Roadmap-Formation-Session2.html, at the root of the repository) reads them from a JSON block that this script
rewrites, between the markers <!-- path-data:start --> and <!-- path-data:end -->. Run it after any change of
labs.json; `dotnet test` fails (LearningPathTests) while the roadmap is out of date.

    python3 Dashboard/tools/generate-roadmap.py            # rewrite the block
    python3 Dashboard/tools/generate-roadmap.py --check    # exit 1 if the block is out of date
"""
import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
CATALOG = ROOT / "Dashboard" / "LabDashboard" / "labs.json"
ROADMAP = ROOT / "Roadmap-Formation-Session2.html"
START, END = "<!-- path-data:start -->", "<!-- path-data:end -->"
# A lab split in two exercises (Lab06 A2A, Lab16 MCP) is one card of the roadmap: its id without the suffix.
PART = re.compile(r"-(server|client)$")


def texts(item: dict, field: str) -> dict:
    """The English text of a field and its translations, by language."""
    values = {"en": item[field]}
    for language, translation in sorted(item.get("translations", {}).items()):
        if translation.get(field):
            values[language] = translation[field]
    return values


def path_data(catalog: dict) -> dict:
    number = 0
    stages = []
    for stage in catalog["stages"]:
        optional = stage.get("optional", False)
        if not optional:
            number += 1
        stages.append({"id": stage["id"], "number": None if optional else number, "optional": optional, "title": texts(stage, "title")})
    labs = []
    for lab in catalog["labs"]:
        labs.append({
            "id": lab["id"],
            "card": PART.sub("", lab["id"]),
            "number": lab["number"],
            "stage": lab["stage"],
            "track": texts(lab, "track"),
            "level": texts(lab, "level"),
            "title": texts(lab, "title"),
            "summary": texts(lab, "summary"),
            "path": lab["path"],
            "bonusScenarios": lab.get("bonusScenarios", []),
        })
    return {"source": "Dashboard/LabDashboard/labs.json", "stages": stages, "labs": labs}


def block(catalog: dict) -> str:
    data = json.dumps(path_data(catalog), ensure_ascii=False, indent=1).replace("</", "<\\/")
    return f'{START}\n<script type="application/json" id="path-data">\n{data}\n</script>\n{END}'


def main() -> int:
    check = "--check" in sys.argv[1:]
    catalog = json.loads(CATALOG.read_text(encoding="utf-8"))
    html = ROADMAP.read_text(encoding="utf-8")
    pattern = re.compile(re.escape(START) + r".*?" + re.escape(END), re.S)
    if not pattern.search(html):
        print(f"{ROADMAP.name}: markers {START} ... {END} not found", file=sys.stderr)
        return 2
    updated = pattern.sub(lambda _: block(catalog), html, count=1)
    if check:
        if updated != html:
            print(f"{ROADMAP.name} is out of date: run python3 Dashboard/tools/generate-roadmap.py", file=sys.stderr)
            return 1
        print(f"{ROADMAP.name} is up to date.")
        return 0
    if updated != html:
        ROADMAP.write_text(updated, encoding="utf-8")
        print(f"{ROADMAP.name} updated from labs.json.")
    else:
        print(f"{ROADMAP.name} already up to date.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
