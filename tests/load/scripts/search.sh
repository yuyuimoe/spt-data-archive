#!/usr/bin/env bash
# The capacity search: raise the number of clients until an answer becomes slow, then bisect
# (docs/load/README.md).
#
#   tests/load/scripts/search.sh ramp     # 500, 1000, 1500, ... until one run fails
#   tests/load/scripts/search.sh bisect   # narrow the interval between the last pass and the first fail
#   tests/load/scripts/search.sh all      # ramp, then bisect
#   tests/load/scripts/search.sh report   # print the table of every run so far
#
# A run passes when p95 <= 500 ms, p99 <= 1000 ms and fewer than 1% of the requests fail. Each run is a
# k6 container limited to 4 vCPU and 2 GB, in the network of tests/load/stack/compose.yaml.
#
# The container runs as root, which rootless podman maps onto the calling user, so the summary file
# lands in the results directory instead of failing on a permission. Inside the container nothing
# needs a privilege: k6 reads a mounted script and writes one JSON file.
#
# k6 skips certificate validation (--insecure-skip-tls-verify). The image is Alpine without the tool that
# installs a certificate authority, and the chain of the local CA was proven with curl on the host
# (docs/load/README.md). The TLS handshake itself still happens, once per virtual user.
#
# Environment: OUT (results directory), DURATION (40s), STEP (500), MAX (8000), TOL (50), SETTLE (12),
#              POOL (30, the database pool of the Api), ENCODING ('' = gzip).

set -euo pipefail

here=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
root=$(cd "$here/../../.." && pwd)
project=sptarchive-load

from=${FROM:-500}
step=${STEP:-500}
max=${MAX:-8000}
tol=${TOL:-50}
duration=${DURATION:-40s}
settle=${SETTLE:-12}
pool=${POOL:-30}
encoding=${ENCODING:-}
profile=${PROFILE:-heavy}
target=${TARGET:-https://caddy/graphql}
hot_share=${HOT_SHARE:-0.8}
out=${OUT:-$root/tests/load/results}
tag=$(date -u +%Y%m%dT%H%M%SZ)

mkdir -p "$out"

say() { printf '%s\n' "$*" >&2; }

container() {
	podman ps -q \
		--filter "label=com.docker.compose.project=$project" \
		--filter "label=com.docker.compose.service=$1" | head -1
}

network() {
	podman network ls --filter "label=com.docker.compose.project=$project" --format '{{.Name}}' | head -1
}

# The pool of the Api is an environment variable of its container, so a run states which one it measured.
pool_of_service() {
	podman inspect "$(container api)" --format '{{range .Config.Env}}{{println .}}{{end}}' |
		sed -n 's/^Database__MaxPoolSize=//p'
}

record() {
	# record <file> <json>: one line per run, appended, so a later run never rewrites an earlier result.
	printf '%s\n' "$2" >>"$out/runs.ndjson"
}

# sample_cpu <users> <seconds>: the CPU and memory of every container while a run happens, the load
# generator included, because a slow generator is also an answer and has to be visible as one.
sample_cpu() {
	local users=$1 seconds=$2
	while sleep 2 && [ "$seconds" -gt 0 ]; do
		podman stats --no-stream --format json 2>/dev/null |
			python3 -c 'import json, sys, time
for row in json.load(sys.stdin):
    print(json.dumps({"t": int(time.time()), "name": row["name"],
                      "cpu": row["cpu_percent"], "mem": row["mem_usage"], "mem_perc": row["mem_percent"]}))' \
				>>"$out/cpu-$users.ndjson"
		seconds=$((seconds - 2))
	done
}

server_timing() {
	# server_timing <users> <since>: what the proxy waited for, independent of the generator. The window
	# is the run, so the number is that run and no other.
	podman logs --since "$2" "$(container caddy)" 2>&1 >"$out/caddy-$1.log"
	python3 - "$out/caddy-$1.log" >"$out/server-$1.json" <<'ANALYSIS'
import json
import sys

durations, statuses = [], {}
with open(sys.argv[1]) as stream:
    for line in stream:
        try:
            row = json.loads(line)
        except ValueError:
            continue

        status = row.get("status")
        if status is None:
            continue

        statuses[str(status)] = statuses.get(str(status), 0) + 1
        if status == 200 and row.get("duration") is not None:
            durations.append(row["duration"] * 1000)

durations.sort()


def share(part):
    if not durations:
        return None

    return round(durations[min(len(durations) - 1, int(len(durations) * part))], 1)


print(json.dumps({
    "requests": sum(statuses.values()),
    "status": statuses,
    "p50": share(0.50),
    "p95": share(0.95),
    "p99": share(0.99),
    "max": round(durations[-1], 1) if durations else None,
}))
ANALYSIS
}

sample_db() {
	# sample_db <users>: how many connections the reader really holds, and how many are busy. This is
	# the number that says whether the pool of ADR-0038 is the right size.
	local users=$1 db
	db=$(container db)
	while sleep 1 && [ -f "$out/running-$users" ]; do
		podman exec "$db" psql -qtA -U archive_owner -d archive -c \
			"select now(), count(*) filter (where state = 'active'), count(*) from pg_stat_activity where usename = 'archive_reader'" \
			>>"$out/pg-$users.txt" 2>/dev/null || true
	done
}

top_sql() {
	# The statements that took the time of this run, by total time and by calls.
	local db
	db=$(container db)
	podman exec -i "$db" psql -qtA -U archive_owner -d archive <<'SQL' >"$out/sql-$1.json"
select json_agg(row_to_json(q)) from (
	select calls, round(total_exec_time::numeric, 1) as total_ms,
	       round(mean_exec_time::numeric, 2) as mean_ms,
	       round((100 * total_exec_time / nullif(sum(total_exec_time) over (), 0))::numeric, 1) as percent,
	       left(regexp_replace(query, '\s+', ' ', 'g'), 160) as query
	from pg_stat_statements
	where query not like '%pg_stat_statements%'
	order by total_exec_time desc
	limit 12
) q;
SQL
}

run_one() {
	local users=$1 summary verdict
	summary=$out/users-$users.json

	if [ -f "$summary" ] && [ "${FORCE:-0}" != 1 ]; then
		say "run $users is already measured"
	else
		say "run: $users clients, $duration, $profile page, pool $(pool_of_service), hot share $hot_share"
		rm -f "$summary"
		podman exec -i "$(container db)" psql -qtA -U archive_owner -d archive -c 'select pg_stat_statements_reset()' >/dev/null
		rm -f "$out/cpu-$users.ndjson"

		touch "$out/running-$users"
		sample_cpu "$users" 400 >/dev/null 2>&1 &
		local sampler=$!
		sample_db "$users" >/dev/null 2>&1 &
		local dbsampler=$!
		local started
		started=$(date -u +%Y-%m-%dT%H:%M:%SZ)

		local rc=0
		podman run --rm \
			--name sptarchive-load-k6 \
			--cpus=4 --memory=2g \
			--user 0:0 \
			--network "$(network)" \
			-v "$here/../:/scripts:ro" \
			-v "$out:/out:rw" \
			-e BASE_URL="${TARGET:-https://caddy/graphql}" \
			-e USERS="$users" \
			-e DURATION="$duration" \
			-e HOT_SHARE="$hot_share" \
			-e ENCODING="$encoding" \
			-e PROFILE="$profile" \
			-e RUN_TAG="$tag" \
			-e OUT="/out/users-$users.json" \
			docker.io/grafana/k6:1.8.2 run \
			--insecure-skip-tls-verify --quiet --no-color /scripts/capacity.js \
			>"$out/k6-$users.log" 2>&1 || rc=$?

		rm -f "$out/running-$users"
		kill "$sampler" "$dbsampler" 2>/dev/null || true
		wait "$sampler" 2>/dev/null || true
		wait "$dbsampler" 2>/dev/null || true

		if [ ! -f "$summary" ]; then
			say "k6 wrote no summary for $users clients (exit $rc, see $out/k6-$users.log)"
			exit 1
		fi

		top_sql "$users"
		server_timing "$users" "$started"
		sleep "$settle"
	fi

	verdict=$(python3 - "$summary" "$users" "$tag" "$pool" "$profile" <<'PY'
import json
import sys

path, users, tag, pool, profile = sys.argv[1], int(sys.argv[2]), sys.argv[3], int(sys.argv[4]), sys.argv[5]
run = json.load(open(path))
p95 = run.get("p95") or 0.0
p99 = run.get("p99") or 0.0
failed = run.get("failed") or 0.0
passed = p95 <= 500 and p99 <= 1000 and failed < 0.01
line = {
    "users": users,
    "tag": tag,
    "profile": profile,
    "pool": pool,
    "passed": passed,
    "p50": round(run.get("p50") or 0, 1),
    "p95": round(p95, 1),
    "p99": round(p99, 1),
    "max": round(run.get("max") or 0, 1),
    "failed": round(failed, 4),
    "rate": round(run.get("rate") or 0, 1),
    "hit": run.get("hit", 0),
    "miss": run.get("miss", 0),
    "shared": run.get("shared", 0),
    "hit_p95": round(run.get("hitP95") or 0, 1),
    "miss_p95": round(run.get("missP95") or 0, 1),
    "dropped": run.get("dropped") or 0,
    "status429": run.get("status429", 0),
    "status5xx": run.get("status5xx", 0),
    "answer_kb": round((run.get("answerBytes") or 0) / 1024, 1),
}
print(json.dumps(line))
print("PASS" if passed else "FAIL", file=sys.stderr)
PY
)

	record "$users" "$verdict"
	printf '%s\n' "$verdict"
}

report() {
	python3 - "$out/runs.ndjson" <<'PY'
import json
import sys

rows = [json.loads(line) for line in open(sys.argv[1])]
print(f"{'clients':>8} {'verdict':>7} {'page':>8} {'p50':>7} {'p95':>8} {'p99':>9} {'max':>9} {'fail%':>6} {'hit/s':>8} {'miss/s':>7} {'drop':>6} {'KB':>7}")
for r in rows:
    print(f"{r['users']:>8} {('pass' if r['passed'] else 'FAIL'):>7} {r.get('profile', 'heavy'):>8} {r['p50']:>7} {r['p95']:>8} {r['p99']:>9} {r['max']:>9} "
          f"{100 * r['failed']:>6.2f} {r['hit'] / 40:>8.0f} {r['miss'] / 40:>7.0f} {r['dropped']:>6} {r['answer_kb']:>7}")
PY
}

passed_at() {
	python3 - "$out/runs.ndjson" "$1" "$profile" <<'PY'
import json
import sys

rows = [json.loads(line) for line in open(sys.argv[1])]
known = [r for r in rows if r["users"] == int(sys.argv[2]) and r.get("profile", "heavy") == sys.argv[3]]
print("yes" if known and known[-1]["passed"] else ("no" if known else "unknown"))
PY
}

last() {
	# last pass < | last fail >: the edges of the interval that the bisect narrows.
	python3 - "$out/runs.ndjson" "$1" "$profile" <<'PY'
import json
import sys

rows = [json.loads(line) for line in open(sys.argv[1])]
rows = [r for r in rows if r.get("profile", "heavy") == sys.argv[3]]
passes = [r["users"] for r in rows if r["passed"]]
fails = [r["users"] for r in rows if not r["passed"]]
print((max(passes) if passes else "") if sys.argv[2] == "pass" else (min(fails) if fails else ""))
PY
}

ramp() {
	local users=$from
	while [ "$users" -le "$max" ]; do
		run_one "$users" >/dev/null
		if [ "$(passed_at "$users")" = "no" ]; then
			say "$users clients failed: the interval is $(last pass) to $users"
			return
		fi

		users=$((users + step))
	done

	say "every run passed up to $max clients"
}

bisect() {
	local lo hi mid
	lo=$(last pass)
	hi=$(last fail)

	if [ -z "$lo" ] || [ -z "$hi" ]; then
		say "a bisect needs one pass and one fail: run the ramp first"
		exit 2
	fi

	say "bisecting $lo (pass) to $hi (fail), tolerance $tol"
	while [ $((hi - lo)) -gt "$tol" ]; do
		mid=$(((lo + hi) / 2))
		mid=$(((mid + 24) / 25 * 25))
		if [ "$(passed_at "$mid")" = "unknown" ]; then
			run_one "$mid" >/dev/null
		fi

		if [ "$(passed_at "$mid")" = "yes" ]; then
			lo=$mid
		else
			hi=$mid
		fi

		say "  $lo passes, $hi fails"
	done

	say "the limit is $lo clients: $hi clients break the thresholds"
}

case ${1:-all} in
ramp) ramp ;;
bisect) bisect ;;
all) ramp && bisect ;;
report) report ;;
one) run_one "${2:?clients}" ;;
*) say "usage: $(basename "$0") ramp|bisect|all|report|one <clients>"; exit 2 ;;
esac
