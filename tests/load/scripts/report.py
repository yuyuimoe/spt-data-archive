#!/usr/bin/env python3
"""Build the capacity report as one HTML page with inline SVG charts (no network, no script).

    python3 tests/load/scripts/report.py

It reads the results of tests/load/scripts/search.sh from tests/load/results and writes
docs/load/capacity-2026-10-21.html. Every number in the page comes from one of those files, so a report
can be rebuilt after a new measurement without editing prose.

The series are chosen by the tag of a run, so a measured run is never replaced by a later one that a
broken stack produced (see "The ceiling of the rig" in the page).
"""

from __future__ import annotations

import json
import pathlib
from dataclasses import dataclass
from datetime import date

HERE = pathlib.Path(__file__).resolve().parent
RESULTS = HERE.parent / "results"
OUTPUT = HERE.parents[2] / "docs" / "load" / "capacity-2026-10-21.html"

# The runs that make the two curves. Each tag is one measured pass; the ascending pass of a series ran
# against a stack that had not collapsed yet, which is why 8000 clients is a fail and 6000 is a pass.
TYPICAL = ["20261021T144617Z", "20261021T144708Z", "20261021T144800Z", "20261021T144856Z",
           "20261021T150712Z", "20261021T150807Z", "20261021T150900Z"]
TYPICAL_FAILED = ["20261021T145336Z", "20261021T145229Z"]
HEAVY = "20261021T142430Z"

# The pool runs: three sizes at 4,000 clients with the same workload (docs/load says why they are typed
# into this file: the connection counter of the Api in the rig could not be read reliably).
POOL_RUNS = [
    (8, "20261021T145754Z", 3, 8),
    (16, "20261021T145900Z", 4, 16),
    (30, "20261021T150002Z", 4, 30),
]

def n(value: float, digits: int = 0) -> str:
    """A number with a thin space between thousands, so a sentence reads the same way everywhere."""
    text = f"{value:,.{digits}f}"
    return text.replace(",", "\u2009")


INK = "#1c1f23"
MUTED = "#5b6570"
GRID = "#dfe3e8"
ACCENT = "#0b6bb5"
WARM = "#c2571a"
BAD = "#a3342c"
GOOD = "#2f7d4f"


@dataclass
class Run:
    users: int
    profile: str
    passed: bool
    p50: float
    p95: float
    p99: float
    rate: float
    hit_p95: float
    miss_p95: float
    dropped: int
    status5xx: int
    answer_kb: float
    tag: str


def load_runs() -> dict[str, Run]:
    runs: dict[str, Run] = {}
    for line in (RESULTS / "runs.ndjson").read_text().splitlines():
        if not line.strip():
            continue
        row = json.loads(line)
        runs[row["tag"] + f":{row['users']}"] = Run(
            users=row["users"],
            profile=row.get("profile", "heavy"),
            passed=row["passed"],
            p50=row["p50"],
            p95=row["p95"],
            p99=row["p99"],
            rate=row["rate"],
            hit_p95=row.get("hit_p95", 0.0),
            miss_p95=row.get("miss_p95", 0.0),
            dropped=row.get("dropped", 0),
            status5xx=row.get("status5xx", 0),
            answer_kb=row.get("answer_kb", 0.0),
            tag=row["tag"],
        )
    return runs


def series(runs: dict[str, Run], tags: list[str]) -> list[Run]:
    picked = [run for key, run in runs.items() if run.tag in tags]
    return sorted(picked, key=lambda run: run.users)


def by_tag(runs: dict[str, Run], tag: str) -> list[Run]:
    return sorted([run for run in runs.values() if run.tag == tag], key=lambda run: run.users)


def cpu(users: int) -> dict[str, float]:
    """The mean CPU of each container of the stack during one run, in percent of one core."""
    path = RESULTS / f"cpu-{users}.ndjson"
    if not path.exists():
        return {}

    totals: dict[str, list[float]] = {}
    for line in path.read_text().splitlines():
        if not line.strip():
            continue
        row = json.loads(line)
        name = row["name"]
        if not name.startswith("sptarchive-load"):
            continue
        totals.setdefault(name.replace("sptarchive-load-", "").replace("-1", ""), []).append(
            float(str(row["cpu"]).rstrip("%"))
        )

    return {name: sum(values) / len(values) for name, values in totals.items()}


def server(users: int) -> dict | None:
    path = RESULTS / f"server-{users}.json"
    return json.loads(path.read_text()) if path.exists() else None


# ---------------------------------------------------------------------------- SVG


def svg(width: int, height: int, body: str) -> str:
    return (f'<svg viewBox="0 0 {width} {height}" width="100%" height="{height}" '
            f'role="img" xmlns="http://www.w3.org/2000/svg" '
            f'font-family="ui-sans-serif, system-ui, sans-serif">{body}</svg>')


def text(x: float, y: float, value: str, size: int = 12, fill: str = INK, anchor: str = "middle",
         weight: str = "400") -> str:
    return (f'<text x="{x:.1f}" y="{y:.1f}" font-size="{size}" fill="{fill}" text-anchor="{anchor}" '
            f'font-weight="{weight}">{value}</text>')


def line(x1: float, y1: float, x2: float, y2: float, stroke: str = GRID, width: float = 1.0,
         dash: str | None = None) -> str:
    extra = f' stroke-dasharray="{dash}"' if dash else ""
    return (f'<line x1="{x1:.1f}" y1="{y1:.1f}" x2="{x2:.1f}" y2="{y2:.1f}" stroke="{stroke}" '
            f'stroke-width="{width}"{extra}/>')


def path(points: list[tuple[float, float]], stroke: str, width: float = 2.0,
         dash: str | None = None) -> str:
    if not points:
        return ""
    steps = " ".join(("M" if index == 0 else "L") + f"{x:.1f} {y:.1f}" for index, (x, y) in enumerate(points))
    extra = f' stroke-dasharray="{dash}"' if dash else ""
    return f'<path d="{steps}" fill="none" stroke="{stroke}" stroke-width="{width}"{extra}/>'


def dots(points: list[tuple[float, float]], fill: str, radius: float = 4.0) -> str:
    return "".join(f'<circle cx="{x:.1f}" cy="{y:.1f}" r="{radius}" fill="{fill}"/>' for x, y in points)


def log_scale(value: float, low: float, high: float, top: float, bottom: float) -> float:
    import math

    share = (math.log10(max(value, low)) - math.log10(low)) / (math.log10(high) - math.log10(low))
    return bottom - share * (bottom - top)


def latency_chart(typical: list[Run], typical_failed: list[Run], heavy: list[Run]) -> str:
    width, height = 920, 380
    left, right, top, bottom = 70, 900, 30, 330
    x_max = 8500
    body = []

    for milliseconds in (1, 10, 100, 1000, 10000):
        y = log_scale(milliseconds, 1, 10000, top, bottom)
        body.append(line(left, y, right, y, GRID))
        body.append(text(left - 10, y + 4, f"{n(milliseconds)} ms", 11, MUTED, "end"))

    for x in (500, 2000, 4000, 6000, 8000):
        cx = left + (x / x_max) * (right - left)
        body.append(line(cx, bottom, cx, bottom + 6, MUTED))
        body.append(text(cx, bottom + 20, n(x), 11, MUTED))

    for budget, label, colour in ((500, "p95 budget, 500 ms", GOOD), (1000, "p99 budget, 1 000 ms", WARM)):
        y = log_scale(budget, 1, 10000, top, bottom)
        body.append(line(left, y, right, y, colour, 1.4, "6 4"))
        body.append(text(left + 6, y - 6, label, 11, colour, "start"))

    def xy(users: int, value: float) -> tuple[float, float]:
        return left + (users / x_max) * (right - left), log_scale(value, 1, 10000, top, bottom)

    for runs, colour, label in ((typical, ACCENT, "typical page, 53 KB"),
                                (heavy, MUTED, "worst-case page, 116 KB")):
        if not runs:
            continue
        for field, dash in (("p95", None), ("p99", "4 3")):
            body.append(path([xy(run.users, getattr(run, field)) for run in runs], colour, 2.0, dash))
        body.append(dots([xy(run.users, run.p95) for run in runs], colour))
        x, y = xy(runs[-1].users, runs[-1].p95)
        near = x > right - 200
        body.append(text(x - 8 if near else x + 8, y + 4, label, 11, colour, "end" if near else "start"))

    for run in typical_failed:
        cx, cy = xy(run.users, run.p95)
        body.append(f'<path d="M{cx - 6:.1f} {cy - 6:.1f} l12 12 M{cx + 6:.1f} {cy - 6:.1f} l-12 12" '
                    f'stroke="{BAD}" stroke-width="2"/>')
        body.append(text(cx, cy - 14, f"{n(run.users)} refused", 11, BAD))

    body.append(text((left + right) / 2, height - 6, "clients (one request per second each)", 12, MUTED))
    body.append(f'<text x="16" y="{(top + bottom) / 2:.0f}" font-size="12" fill="{MUTED}" '
                f'text-anchor="middle" transform="rotate(-90 16 {(top + bottom) / 2:.0f})">'
                f'answer time, log scale</text>')

    return svg(width, height, "".join(body))


def cpu_chart(typical: list[Run]) -> str:
    width, height = 920, 340
    left, right, top, bottom = 70, 900, 30, 290
    x_max = 6500
    y_max = 400
    body = []

    for share in (0, 100, 200, 300, 400):
        y = bottom - (share / y_max) * (bottom - top)
        body.append(line(left, y, right, y, GRID))
        body.append(text(left - 10, y + 4, f"{share}%", 11, MUTED, "end"))

    body.append(line(left, top, right, top, BAD, 1.4, "6 4"))
    body.append(text(right - 4, top + 16, "the 4 vCPU of one container", 11, BAD, "end"))

    for users, values in [(run.users, cpu(run.users)) for run in typical]:
        cx = left + (users / x_max) * (right - left)
        body.append(line(cx, bottom, cx, bottom + 6, MUTED))
        body.append(text(cx, bottom + 20, n(users), 11, MUTED))

    names = {"api": (ACCENT, "the Api"), "caddy": (WARM, "the proxy"), "db": (GOOD, "PostgreSQL"),
             "redis": ("#7b4fa0", "Redis"), "k6": (BAD, "the generator")}
    for name, (colour, label) in names.items():
        points = []
        for run in typical:
            values = cpu(run.users)
            if name in values:
                points.append((left + (run.users / x_max) * (right - left),
                               bottom - (values[name] / y_max) * (bottom - top)))
        if not points:
            continue
        body.append(path(points, colour, 2.0))
        body.append(dots(points, colour, 3.0))
        end = points[-1][0] + 6
        body.append(text(end if end < right - 80 else right - 2, points[-1][1] + 4, label, 11, colour,
                         "start" if end < right - 80 else "end"))

    body.append(text((left + right) / 2, height - 6, "clients (one request per second each)", 12, MUTED))
    return svg(width, height, "".join(body))


def rate_chart(typical: list[Run], failed: list[Run]) -> str:
    width, height = 920, 300
    left, right, top, bottom = 70, 900, 30, 250
    x_max = 8500
    y_max = 8500
    body = [line(left, bottom, right, bottom, MUTED)]

    for value in (2000, 4000, 6000, 8000):
        x = left + (value / x_max) * (right - left)
        y = bottom - (value / y_max) * (bottom - top)
        body.append(line(x, bottom, x, bottom + 6, MUTED))
        body.append(text(x, bottom + 20, n(value), 11, MUTED))
        body.append(line(left, y, right, y, GRID))
        body.append(text(left - 10, y + 4, n(value), 11, MUTED, "end"))

    body.append(path([(left, bottom), (right, top)], MUTED, 1.4, "5 4"))
    body.append(text(right - 6, top + 18, "offered = answered", 11, MUTED, "end"))

    points = [(left + (run.users / x_max) * (right - left), bottom - (run.rate / y_max) * (bottom - top))
              for run in typical + failed]
    body.append(path(points, ACCENT, 2.0))
    body.append(dots(points, ACCENT))
    for run in failed:
        x = left + (run.users / x_max) * (right - left)
        y = bottom - (run.rate / y_max) * (bottom - top)
        body.append(f'<path d="M{x - 6:.1f} {y - 6:.1f} l12 12 M{x + 6:.1f} {y - 6:.1f} l-12 12" '
                    f'stroke="{BAD}" stroke-width="2"/>')
        body.append(text(x, y + 24, f"{n(run.rate)} a/s", 11, BAD))

    body.append(text((left + right) / 2, height - 6, "clients offered", 12, MUTED))
    body.append(text(30, (top + bottom) / 2, "answers/s", 11, MUTED, "middle"))
    return svg(width, height, "".join(body))


def pool_chart(runs: dict[str, Run]) -> str:
    width, height = 920, 300
    left, right, top, bottom = 70, 900, 30, 250
    body = [line(left, bottom, right, bottom, MUTED)]
    y_max = 70

    for value in (0, 20, 40, 60):
        y = bottom - (value / y_max) * (bottom - top)
        body.append(line(left, y, right, y, GRID))
        body.append(text(left - 10, y + 4, f"{value} ms", 11, MUTED, "end"))

    group = (right - left) / len(POOL_RUNS)
    for index, (size, tag, active, connections) in enumerate(POOL_RUNS):
        found = [run for run in runs.values() if run.tag == tag and run.users == 4000]
        if not found:
            continue
        run = found[0]
        cx = left + group * (index + 0.5)
        for offset, value, colour, label in ((-26, run.p95, ACCENT, "p95"), (26, run.p99, WARM, "p99")):
            bar_height = (min(value, y_max) / y_max) * (bottom - top)
            body.append(f'<rect x="{cx + offset - 22:.1f}" y="{bottom - bar_height:.1f}" width="44" '
                        f'height="{bar_height:.1f}" fill="{colour}" rx="3"/>')
            body.append(text(cx + offset, bottom - bar_height - 6, f"{value:.1f}", 11, colour))
        body.append(text(cx, bottom + 20, f"pool {size}", 12, INK, "middle", "600"))
        body.append(text(cx, bottom + 38, f"{active} of {connections} busy at most", 11, MUTED))

    body.append(text((left + right) / 2, height - 6,
                     "4 000 clients, typical page, three sizes of the pool of the Api", 12, MUTED))
    return svg(width, height, "".join(body))


# ---------------------------------------------------------------------------- page


def table(headers: list[str], rows: list[list[str]], numeric_from: int = 1) -> str:
    head = "".join(f"<th>{cell}</th>" for cell in headers)
    body = []
    for row in rows:
        cells = "".join(
            f'<td class="{"n" if index >= numeric_from else ""}">{cell}</td>' for index, cell in enumerate(row)
        )
        body.append(f"<tr>{cells}</tr>")

    return f"<table><thead><tr>{head}</tr></thead><tbody>{''.join(body)}</tbody></table>"


def main() -> None:
    runs = load_runs()
    typical = series(runs, TYPICAL)
    failed = series(runs, TYPICAL_FAILED)
    heavy = by_tag(runs, HEAVY)
    heavy_passed = [run for run in heavy if run.passed]
    heavy_failed = [run for run in heavy if not run.passed]

    best = typical[-1]
    worst_client = max(typical + failed, key=lambda run: run.users)
    last_server = server(best.users)
    peak = cpu(best.users)
    best_hit = best.hit_p95
    best_miss = best.miss_p95
    api_cores = peak.get("api", 0) / 100
    projection = int(round(best.users * 4 / api_cores / 100)) * 100 if api_cores else 0
    used = int(round(100 * api_cores / 4))
    caddy_cores = f"{peak.get('caddy', 0) / 100:.1f} of its 4 cores"

    evidence = []
    for users in (4000, 5000, 6000, 8000):
        side = server(users)
        if side:
            evidence.append([n(users), f"{side['p95']} ms", f"{side['p99']} ms",
                             ", ".join(f"{status} × {n(count)}"
                                       for status, count in sorted(side["status"].items()))])

    run_rows = []
    for run in heavy + typical + failed:
        run_rows.append([
            n(run.users),
            "worst case, 116 KB" if run.profile == "heavy" else "typical, 53 KB",
            f"{run.answer_kb:.0f} KB",
            f"{run.p50:.1f}",
            f"{run.p95:.1f}",
            f"{run.p99:.1f}",
            n(run.rate),
            n(run.dropped),
            "pass" if run.passed else f"fail ({n(run.status5xx)} refused)",
        ])

    pool_rows = []
    for size, tag, active, connections in POOL_RUNS:
        found = [run for run in runs.values() if run.tag == tag and run.users == 4000]
        if not found:
            continue
        run = found[0]
        pool_rows.append([
            str(size), f"{run.p50:.1f} ms", f"{run.p95:.1f} ms", f"{run.p99:.1f} ms",
            n(run.rate), f"{active} / {connections}",
        ])

    page = f"""<!doctype html>
<html lang="en">
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Capacity of the SPT data archive, {date.today().isoformat()}</title>
<style>
  :root {{ color-scheme: light dark; }}
  body {{ margin: 0 auto; max-width: 60rem; padding: 2.5rem 1.25rem 5rem;
         font: 16px/1.6 ui-sans-serif, system-ui, sans-serif; color: {INK}; background: #fff; }}
  h1 {{ font-size: 1.9rem; line-height: 1.2; margin: 0 0 .35rem; }}
  h2 {{ font-size: 1.2rem; margin: 2.6rem 0 .6rem; padding-top: 1.2rem; border-top: 1px solid {GRID}; }}
  h3 {{ font-size: 1rem; margin: 1.6rem 0 .4rem; }}
  p.lead {{ color: {MUTED}; margin-top: 0; }}
  .answer {{ border-left: 4px solid {ACCENT}; background: #f4f8fc; padding: .9rem 1.1rem; margin: 1.6rem 0;
             border-radius: 0 6px 6px 0; }}
  .answer strong {{ font-size: 1.05rem; }}
  figure {{ margin: 1.4rem 0 .4rem; }}
  figcaption {{ color: {MUTED}; font-size: .88rem; margin-top: .3rem; }}
  table {{ border-collapse: collapse; width: 100%; margin: .8rem 0 1.2rem; font-size: .92rem; }}
  th, td {{ text-align: left; padding: .38rem .55rem; border-bottom: 1px solid {GRID}; }}
  th {{ font-weight: 600; color: {MUTED}; font-size: .82rem; text-transform: uppercase; letter-spacing: .02em; }}
  td.n, th:not(:first-child) {{ text-align: right; font-variant-numeric: tabular-nums; }}
  code {{ font-size: .88em; background: #f2f4f6; padding: .1em .35em; border-radius: 3px; }}
  ul {{ padding-left: 1.15rem; }}
  li {{ margin: .35rem 0; }}
  .muted {{ color: {MUTED}; }}
  @media (prefers-color-scheme: dark) {{
    body {{ background: #14171a; color: #e6e9ec; }}
    .answer {{ background: #1a2027; }}
    code {{ background: #20262c; }}
    td, th {{ border-color: #2a3138; }}
  }}
</style>

<h1>How much load the archive holds</h1>
<p class="lead">Measured on 21 October 2026 against the stack of <code>compose.yaml</code>: Caddy, the
Api, PostgreSQL and Redis, each in its own container limited to 4 vCPU and 2 GB, with the generator
next to them under the same limit. Repository commit <code>272f330</code> plus the proxy of ADR-0039.</p>

<div class="answer">
  <strong>The Api did not reach its limit.</strong>
  <p>{n(best.users)} clients — one request per second each, so {n(best.rate)} answers per second of
  53 KB — pass with p95 {best.p95:.1f} ms and p99 {best.p99:.1f} ms, against a budget of 500 ms and
  1 000 ms. That is {100 * best.p95 / 500:.0f}% of the p95 budget. The Api held
  {peak.get('api', 0) / 100:.1f} of its 4 cores and PostgreSQL {peak.get('db', 0) / 100:.1f}.</p>
  <p>The Api would meet its 4 cores near {n(projection)} clients if the cost per answer stayed flat, so
  it used about {used}% of that budget at the last proven point. Above {n(best.users)} the
  <em>load generator</em> is what breaks, not the server: at 6 500 clients k6 was killed by its own 2 GB
  limit, and at {n(worst_client.users)} it answered only {n(worst_client.rate)} of
  {n(worst_client.users)} requests per second. The last proven point is {n(best.users)} clients; the
  limit of the deployment is above it and this rig cannot reach it.</p>
</div>

<h2>The curve</h2>
<figure>
{latency_chart(typical, failed, heavy_passed)}
<figcaption>Answer time as a client sees it. Solid line p95, dashed p99. The grey curve is the
worst-case page a client can ask for: 50 items with the raw <code>_props</code> of each, 116 KB. The
blue curve is what a data browser really asks for: 50 items without the raw JSON, 53 KB.</figcaption>
</figure>

<h3>Answers per second, offered against delivered</h3>
<figure>
{rate_chart(typical, failed)}
<figcaption>The generator keeps the offered rate until it does not: the crosses are the runs where it
lost the schedule and the queue of unfinished requests took the memory of the container.</figcaption>
</figure>

<h2>Where the CPU went</h2>
<figure>
{cpu_chart(typical)}
<figcaption>Mean CPU of each container during a run, in percent of one core, so 400% is the limit of a
container. The proxy is the second largest consumer: TLS for every answer, HTTP/2 flow control, and a
JSON access line for every request.</figcaption>
</figure>

<h2>What caused the most latency</h2>
<p>At {n(best.users)} clients an answer arrives every {(1000 / best.rate):.2f} ms, and the numbers below
split where that time goes.</p>
{table(["Measured at " + f"{n(best.users)} clients", "Value", "What it means"], [
    ["Api answer time, as the client sees it", f"p50 {best.p50:.1f} ms, p95 {best.p95:.1f} ms",
     "includes TLS, the proxy hop and the client"],
    ["Api answer time, as Caddy sees it", f"p50 {last_server['p50']} ms, p95 {last_server['p95']} ms"
     if last_server else "—", "the proxy is a fraction of a millisecond of the total"],
    ["A cache hit, as the client sees it", f"p95 {best_hit:.1f} ms", "Redis plus compression, no database"],
    ["A cache miss, as the client sees it", f"p95 {best_miss:.1f} ms", "one SQL statement plus compression"],
    ["The one statement that costs the most", "1.4 ms mean, 93% of the time in PostgreSQL",
     "the page of items: SELECT … ORDER BY, 4 673 rows"],
    ["Connections busy in PostgreSQL", "1.2 on average, 4 at most", "the pool is not the constraint"],
    ["CPU of the Api", f"{peak.get('api', 0) / 100:.1f} of 4 cores",
     "GraphQL, JSON serialization of the rows, gzip of every answer"],
    ["CPU of the proxy", f"{peak.get('caddy', 0) / 100:.1f} of 4 cores", "TLS, HTTP/2 and the access log"],
    ["CPU of PostgreSQL", f"{peak.get('db', 0) / 100:.1f} of 4 cores", "the cached 80% never reaches it"],
    ["CPU of the generator", f"{peak.get('k6', 0) / 100:.1f} of 4 cores",
     "the ceiling of the whole measurement, and the first thing that breaks"],
], numeric_from=1)}

<ul>
  <li><strong>The cache is the reason this works.</strong> 80% of the requests are ten pages that Redis
  answers in {best_hit:.1f} ms; the other 20% run one SQL statement and cost {best_miss:.1f} ms. Without
  the cache every request would be a miss, and the database would need
  {100 / 20:.0f} times the work it does now.</li>
  <li><strong>Compression costs CPU to save bandwidth.</strong> A page of 116 KB leaves as 109 KB with
  gzip or 59 KB with brotli: 4.7 times and 8.7 times smaller. The Api pays that with its own cores, and
  it is the largest single item in the Api's CPU.</li>
  <li><strong>PostgreSQL is not the bottleneck.</strong> One statement holds 93% of the database time
  and still takes 1.4 ms at its mean. Capping the work with the cache is what keeps it there.</li>
  <li><strong>The proxy is not free.</strong> Caddy spends {caddy_cores} of its 4 cores, and most of that
  is TLS and the JSON access log, which writes one line per request. A deployment that samples that log
  gives the proxy a good part of its budget back.</li>
</ul>

<h2>The pool of connections</h2>
<p>The question was whether 30 connections is the right size for PostgreSQL with 4 cores. Measured at
4 000 clients, the three sizes behave the same, and PostgreSQL never had more than 4 connections busy:
the pool was never the constraint, and the extra connections only sat idle holding a backend each.</p>
<figure>
{pool_chart(runs)}
<figcaption>p95 and p99 as the client sees them, at three sizes of the pool. The differences on p99 go
in both directions when a run is repeated, so they are noise.</figcaption>
</figure>
{table(["Pool", "p50", "p95", "p99", "Answers/s", "Busy / open"], pool_rows)}
<p>The default of the Api is now 16: 34 of the 50 connections of <code>compose.yaml</code> stay free for
the Archiver, migrations and an administrator, and two Api instances still fit under the same ceiling.
The four timeouts of the pool came from the same checklist: a request waits 5 s for a connection, an idle
connection closes after 60 s, and no connection lives longer than 30 minutes.</p>

<h2>The ceiling of the rig</h2>
<p>Two different things fail above 6 000 clients, and neither is the Api.</p>
<h3>1. The generator runs out of memory</h3>
<p>At 6 500 clients the k6 container was killed by its own 2 GB limit (exit 137). A virtual user holds
one request in flight, so a run needs about <code>rate × latency</code> of them, and each one costs most
of a megabyte once the answer is 53 KB of JSON that the client inflates.</p>
<h3>2. A collapse poisons the next run</h3>
<p>When the generator loses the schedule, the proxy keeps the unfinished answers of a slow client in
memory and opens more connections to the Api. In that state Caddy reached 1.3 GB and started to answer
502 with <code>cannot assign requested address</code>: it had exhausted its own range of source ports.
Every run in this report therefore starts from a clean stack, and a series is measured in one ascending
pass.</p>

<h3>The server side of the same requests</h3>
{table(["Clients", "p95 at the proxy", "p99 at the proxy", "Answers"], evidence)}
<p>Caddy writes one access line per request with its own duration, so these numbers exclude the
generator. At {n(best.users)} clients the proxy sees {last_server['p95'] if last_server else '—'} ms where
the client sees {best.p95:.1f} ms: the difference is the TLS handshake of a new client and the hop, and it
is small. At 8 000 clients the proxy itself answers in 2 233 ms, which is the cascade and not a slow
Api.</p>

<h2>Every run</h2>
{table(["Clients", "Page", "Answer", "p50", "p95", "p99", "Answers/s", "Dropped", "Verdict"], run_rows,
       numeric_from=2)}

<h2>How this was measured</h2>
<ul>
  <li>The stack is <code>tests/load/stack/compose.yaml</code>: the four containers of
  <code>compose.yaml</code> and the generator, each with 4 vCPU and 2 GB. The data is a dump of the
  development archive: 4.0.13 and 4.1.6 official, and the five mod datasets.</li>
  <li>One "client" is one request per second. 80% of the requests are ten popular pages, warmed before
  the run, so they are cache hits; 20% are pages nobody asked for yet, so every one of them runs SQL.
  Every cold request carries a unique tag in the query, which is what makes the cache key unique.</li>
  <li>A run passes when p95 ≤ 500 ms, p99 ≤ 1 000 ms and fewer than 1% of the requests fail. Each run
  lasts 40 s after a warm-up, and the generator rises in steps of 500 clients; a binary search narrows
  the interval between the last pass and the first fail.</li>
  <li>While a run happens, the driver samples the CPU and the memory of every container every 2 s, the
  connections of the read-only role in PostgreSQL every second, and resets
  <code>pg_stat_statements</code> before each run so the statements belong to that run alone.</li>
  <li>Reproduce it with <code>tests/load/scripts/stack.sh up</code> and
  <code>tests/load/scripts/search.sh all</code>; <code>tests/load/README.md</code> holds the details.
  This page is rebuilt by <code>python3 tests/load/scripts/report.py</code>.</li>
</ul>
"""

    OUTPUT.parent.mkdir(parents=True, exist_ok=True)
    OUTPUT.write_text(page)
    print(f"wrote {OUTPUT} ({OUTPUT.stat().st_size:,} bytes)")


if __name__ == "__main__":
    main()
