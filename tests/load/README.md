# Load tests

Four scripts measure the Api of the archive: a hot answer, a cache stampede, the expiry of an answer, and
the capacity of the whole stack. They run against a stack of containers, never against the machine of a
developer, so a number here is a number about the deployment.

The measured result is [docs/load/capacity-2026-10-21.html](../../docs/load/capacity-2026-10-21.html):
the Api holds 6 000 clients at p95 8.1 ms, and the load generator, not the server, is what breaks above
that.

## The stack that is measured

`tests/load/stack/compose.yaml` holds the same five containers as `compose.yaml` plus the generator, in
one network:

| Container | What it is | Limit |
|---|---|---|
| `caddy` | TLS, HSTS, the one public door (ADR-0039) | 4 vCPU, 2 GB |
| `api` | the read-only GraphQL Api, as a container | 4 vCPU, 2 GB |
| `db` | PostgreSQL 17, the same settings as `compose.yaml`, plus `pg_stat_statements` | 4 vCPU, 2 GB |
| `redis` | the response cache: 128 MB, least-recently-used | 4 vCPU, 2 GB |
| `k6` | the generator, started per run | 4 vCPU, 2 GB |

The database holds a dump of the development archive: 4.0.13 and 4.1.6 official, and the five mod
datasets. Every container is limited the same way, so one slow part cannot take the machine of another.

## Run it

```bash
tests/load/scripts/stack.sh up           # build the Api image, start five containers, restore the data
tests/load/scripts/stack.sh ca           # the certificate authority that Caddy signed, for a browser

# One hot answer at 100 requests per second for 20 seconds.
BASE_URL=https://caddy/graphql k6 run --insecure-skip-tls-verify tests/load/steady.js

# The capacity search: 500 clients, then 1 000, until one run fails, then a binary search.
tests/load/scripts/search.sh ramp
tests/load/scripts/search.sh bisect
tests/load/scripts/search.sh report      # the table of every run so far

tests/load/scripts/stack.sh down         # keep the data
tests/load/scripts/stack.sh destroy      # delete it
```

`tests/load/scripts/report.py` rebuilds the HTML page from `tests/load/results`. Every number in that
page comes from a file in that directory, so a new measurement needs no prose.

## What one client is

One client sends one request per second, so a run of 6 000 clients offers 6 000 answers per second. The
traffic is a data browser:

- **80% hot**: ten pages that a client opens first (a page of items by name, by weight, quests, traders,
  translations, mod items). They are warmed before the run, so Redis answers them and no database is
  touched.
- **20% cold**: a page that nobody asked for yet, either a search by name or a sort of the whole table.
  Every cold request carries a unique tag in the query text, and the cache key is the hash of the body, so
  it always misses and always runs SQL.

Two shapes of a page of items are available, and `PROFILE` chooses one:

| Profile | What a client asks for | Answer |
|---|---|---|
| `heavy` (default) | 50 items with the raw `_props` of each | 116 KB |
| `typical` | 50 items without `_props` | 53 KB |

The heavy page is the worst case that the cost limit of the Api allows, and it saturates the generator
before the Api. The typical page is what a browser really asks for, and it is where the limit of the Api
can be measured.

## When a run passes

A run passes when all three hold:

- p95 of the answer time is at most 500 ms,
- p99 is at most 1 000 ms,
- fewer than 1% of the requests fail.

Each run lasts 40 s after a warm-up, and the generator rises in steps of 500 clients. When one step fails,
a binary search narrows the interval between the last pass and the first fail to 50 clients. k6 prints
PASS or FAIL from the same thresholds, so a manual `k6 run` says the same thing.

## What the driver records, per run

- `users-<n>.json` — the summary of k6: p50, p90, p95, p99, the rate, the cache states, the failures.
- `cpu-<n>.ndjson` — CPU and memory of every container every 2 s, the generator included.
- `pg-<n>.txt` — the connections of the read-only role and how many are busy, every second.
- `sql-<n>.json` — the twelve statements that took the time in PostgreSQL, as a share of the run, from
  `pg_stat_statements`, which is reset before every run.
- `server-<n>.json` — the answer time as Caddy measured it, from its access log. This is the number that
  separates a slow server from a slow client.
- `runs.ndjson` — one line per run, with its verdict. Never rewritten.

## Two traps that cost a day

**A run that collapses poisons the one after it.** When the generator loses its schedule, Caddy keeps the
unfinished answers of a slow client in memory and opens more connections to the Api. In that state it
reached 1.3 GB and answered `502 cannot assign requested address`: it had used its own range of source
ports. Every series in the report therefore starts from a clean stack (`stack.sh destroy`, then `up`) and
is measured in one ascending pass. A run whose stack is not fresh is not a measurement.

**The generator is the first thing to break.** k6 holds one request in flight per virtual user, so a run
needs about `rate × latency` virtual users, and each costs most of a megabyte once the answer is 53 KB of
JSON that the client inflates. At 6 500 clients the container was killed by its own 2 GB limit (exit 137).
A capacity number above that is a number about k6.

## Choosing the pool of connections

`POOL=<n> tests/load/scripts/stack.sh up` sets `Database__MaxPoolSize` of the Api, and `pg-<n>.txt` says
how many connections were busy. At 4 000 clients the peak was 4 busy connections with a pool of 8, 16 or
30, and the latency was the same: the pool is not the constraint, the cores of PostgreSQL are. The default
of the Api is 16, which leaves 34 of the 50 connections of `compose.yaml` for the Archiver, migrations and
an administrator (ADR-0038, amended on 2026-10-21).

## The rate limit during a load test

`RATELIMIT_ENABLED=false` is the default of the stack: a capacity test measures the Api, not the guard.
To see the guard work behind the proxy, bring it up with a small bucket and two clients:

```bash
RATELIMIT_ENABLED=true TOKENS=3 REPLENISH=600 POOL=16 \
  podman compose -f tests/load/stack/compose.yaml up -d --force-recreate api caddy

# Two containers at the same time: each one gets its own bucket of three, so each answers 200 200 200
# and then 429. A client that forges X-Forwarded-For is charged to its own address: Caddy does not
# believe the header of a client it was not told to trust (ADR-0039).
podman run --rm --network sptarchive-load_default curlimages/curl -sk -X POST https://caddy/graphql \
  -H 'Content-Type: application/json' -d '{"query":"{ __typename }"}'
```
