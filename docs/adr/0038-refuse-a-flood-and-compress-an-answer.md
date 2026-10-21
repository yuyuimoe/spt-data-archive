# ADR-0038: Refuse a flood at the Api, and compress what it answers

**Status:** Proposed
**Date:** 2026-10-21
**Deciders:** Yui

## Context

The security audit of 2026-10-21 found one Medium and three Low problems at the Api's own edge
(`docs/security/audit-2026-10-21.md`, findings F-01, F-02, F-03 and F-06).

**No limit on how often a client asks.** The Api has no rate limiter, and the edge proxy that ADR-0001
draws is still only planned (TD-05). A legal query returns 1.15 MB in 87 ms: `quests(sptVersion: "4.1.6",
first: 200) { nodes { id data } }`. The cost limit counts fields and objects, not bytes, so one client can
hold the connection pool, the network link and the Redis memory with ordinary requests.

**No compression.** An answer is sent as it left the query (`Transfer-Encoding: chunked`, no
`Content-Encoding`). Measured on 2026-10-21: a page of 200 items with every field is 513,918 bytes,
which is 109,320 bytes with gzip and 58,950 bytes with brotli. The Api sends every one of those bytes
as it left the query.

**No security headers.** An answer carries `Content-Type`, `Date`, `Server` and `X-Cache`, and nothing
else. A browser that sniffs a JSON body as HTML is the case that `X-Content-Type-Options: nosniff`
closes.

**The number of connections is unbounded.** The Api allows 100 pooled connections (the Npgsql default)
while `compose.yaml` starts Postgres with `max_connections=50` (TD-53). A burst on the deployment stack
would ask for more connections than the server has, and the extra requests would fail with
`53300 too many clients` instead of waiting.

## Decision

**A token bucket per client address.** The Api keeps one token bucket per client address. The defaults
are a burst of 100 requests and 100 tokens per 10 seconds, which is 10 requests per second per address.
A request beyond that answers `429` with `Retry-After` and the code `RATE_LIMITED`, in the same error
shape as every other Api error. The bucket never queues: a waiting request would hold a pooled database
connection, which is the resource the limit protects. `/healthz` is exempt, because a health check must
answer while the Api is busy. The settings live in the `RateLimit` section.

**A bounded map of buckets.** The partner of a rate limiter is a map that an attacker grows: one bucket
per address, and an address is attacker-chosen. The Api keeps at most `MaxClients` (50,000) buckets and
forgets one that has been idle for 10 minutes. When a flood fills the map, the oldest idle buckets are
dropped instead, so the map has a ceiling and the limiter cannot become the denial of service it
prevents. A bucket refills without a timer, so a forgotten bucket leaves nothing running.

**Compression outside the cache.** The Api compresses with brotli and gzip, and the middleware sits
*outside* the cache. The cache therefore stores one uncompressed answer, and each client receives it
compressed to what it asked for. The other order is wrong: the compression middleware would sit inside
the buffer, and the entry stored for everyone would be one client's compressed bytes. HTTPS stays
uncompressed by default (`EnableForHttps` is false), because TLS does not exist in front of the Api yet
and the default keeps a BREACH-style attack out of reach. The switch flips with TLS.

**A ceiling on what may be stored.** The cache stores an answer of at most `MaxResponseBytes` (2 MB) and
answers a larger one uncached. Every cacheable request holds its whole answer in memory until the query
finishes, so the ceiling bounds that too. The legal maximum today is 1.15 MB.

**Three headers, and no more.** Every answer carries `X-Content-Type-Options: nosniff`,
`Referrer-Policy: no-referrer` and `X-Frame-Options: DENY`. `HSTS` belongs where TLS ends, and the Api
does not terminate TLS.

**A pool that fits the server.** The Api asks Npgsql for at most `Database:MaxPoolSize` (16) connections,
well under the 50 that `compose.yaml` allows. A burst now waits for a connection instead of receiving
`53300`. The Archiver keeps its own pool: it runs alone and migrates the schema. The size and the four
timeouts come from the amendment of 2026-10-21 below.

The CORS policy stays open to every origin, and an idempotency key still belongs to nobody (F-04, F-05).
Both are correct while the Api is read-only and anonymous, and both must change with the change that adds
authentication.

## Options Considered

| Dimension | Token bucket per address (chosen) | Fixed window per address | One budget for the whole Api | Only the edge proxy |
|---|---|---|---|---|
| A burst after a silence | Allowed up to the bucket | Allowed up to the window | Allowed | Allowed |
| A client that shares a router address | Slowed with its neighbours | Slowed with its neighbours | Slowed | Slowed |
| Protects the pool from one client | Yes | Yes | No | Yes |
| Works while no proxy exists | Yes | Yes | Yes | No |
| Memory that an attacker chooses | Bounded by `MaxClients` | Bounded by `MaxClients` | Constant | Constant |

The edge proxy stays the right place for a limit that a whole deployment shares (TD-05). This ADR puts a
limit in the Api as well, because the proxy does not exist and a deployment ships the container alone.

For compression the alternative was to compress inside the cache, which is the ordering that stores
compressed bytes for every client. For the pool the alternative is to raise `max_connections`, which
spends server memory on a burst instead of making the burst wait.

## Trade-off Analysis

A limit per address treats a school, an office and a container network as one client. The default of 10
requests per second is generous for a data browser, which reads a page of 200 rows in one request, and a
client that needs more can ask for a larger page. The setting is in the environment, so a pilot can raise
it without a new release.

A bucket is per Api instance, so two instances allow twice the rate. The proxy is the fix, and it is
already planned.

The limiter reads the client address from the connection, so behind a proxy every request would arrive
from the proxy's address. A deployment behind a proxy must set the forwarded-headers middleware first,
and that configuration belongs to the deployment, not to this ADR.

Compression spends CPU to save bandwidth. Probed on the local Api, the same 514 KB page leaves as
109 KB with gzip (4.7 times smaller) and 59 KB with brotli (8.7 times smaller). The exchange is good
for every client on a slow link, and the answer came from the cache in both cases, so the query was
not run again.

Capping the reader pool makes a large burst slower: the requests wait in the pool instead of failing. The
wait is bounded by the query time and by the 10-second request timeout, so the worst case is an error
that names the timeout instead of the connection.

## Consequences

- **Easier:** One client can no longer take the pool, the link or the cache memory with legal requests.
- **Easier:** An answer costs about a tenth of the bandwidth on the wire.
- **Easier:** A burst on the deployment stack waits instead of failing with `53300`.
- **Harder:** Four more settings to tune (`RateLimit`, `Cache:MaxResponseBytes`, `Database:MaxPoolSize`).
- **Harder:** A load test must now raise the limit, or it measures the limiter instead of the Api.
- **Revisit when:** The edge proxy exists (move the shared limit there), TLS is terminated at the Api
  (`EnableForHttps`, `HSTS`), or authentication arrives (CORS and the idempotency key).

## Verification

```gherkin
@unverified
Feature: The Api refuses a flood and compresses what it answers

  Scenario: A client that asks too often is refused
    Given the Api allows a burst of two requests
    When one client sends three requests in a row
    Then the third answer is a "429"
    And the error code is "RATE_LIMITED"
    And the answer states a "Retry-After"

  Scenario: The health check is never refused
    Given the Api allows a burst of two requests
    When one client sends ten requests to "/healthz"
    Then every answer is a "200"

  Scenario: An answer is compressed when the client asks for it
    Given a client accepts "gzip"
    When the client sends a query
    Then the answer has "Content-Encoding: gzip"
    And it decodes to the same JSON as the uncompressed answer

  Scenario: The cache stores one answer for every client
    Given a client asked for a query with "Accept-Encoding: gzip"
    When another client asks for the same query without accepting compression
    Then the answer is readable without decoding
    And the answer came from the cache

  Scenario: Every answer carries the security headers
    When a client sends a query
    Then the answer has "X-Content-Type-Options: nosniff"
    And it has "Referrer-Policy: no-referrer"
    And it has "X-Frame-Options: DENY"
```

## Action Items

1. [ ] Put the new settings in `.env.example` and in the compose files.
2. [x] Read the client address from the header of a trusted proxy (ADR-0039).
3. [x] Set `HSTS` where TLS ends (ADR-0039); `EnableForHttps` stays false, because the Api never answers
   TLS itself.

## Amendment of 2026-10-21: the size of the pool, from a measurement

The driver asked whether 30 connections is the right number, and pointed at the checklist of
`connection-pool-tuner`. The checklist says three things that this decision had not written down:

1. A connection only works when PostgreSQL has a core to run its query, so a pool much larger than the
   core count does not add parallelism: it adds a queue inside the database.
2. The budget is `max_connections` minus what the Archiver, migrations and an administrator need, with a
   fifth kept free, and it must survive a second Api instance and a deploy that overlaps two.
3. Four timeouts keep a pool healthy: how long a request waits for a connection, how long an idle
   connection lives, how long any connection lives, and the smallest number kept warm.

The Api now sets all five as `Database:*`, and the defaults are 16 connections, an acquire timeout of 5 s
(a request has 10 s in total), an idle life of 60 s and a life of 30 minutes.

**The measurement.** `docs/load/capacity-2026-10-21.html` runs 4 000 clients at a page of 53 KB with the
pool at 8, 16 and 30. All three answered 3 915 to 3 920 answers per second with p95 between 5.9 ms and
6.6 ms, and the difference on p99 went in both directions when a run was repeated. PostgreSQL held 1.2
connections busy on average and 4 at the most, against 4 cores. The pool was never the constraint.

So the choice is not about latency, which the data does not separate. It is about the budget: 16 leaves 34
of the 50 connections of `compose.yaml` free, and lets two Api instances fit under the same ceiling. With
30, a second instance would take 60 and the Archiver would be the one refused.

Measured with `tests/load/scripts/search.sh` and `pg-<n>.txt`; reproducible with `tests/load/README.md`.

## Links

- ADR-0001: the deployment and its edge proxy
- ADR-0039: the proxy that terminates TLS and names the client
- ADR-0008: the response cache the compression sits outside of
- ADR-0037: the single flight and the idempotency key
- Finding F-01 to F-03 and F-06 in `docs/security/audit-2026-10-21.md`
- TD-05, TD-53, TD-54 in `docs/tech-debt.md`
- Code: `src/SptArchive.Api/Edge/`, `src/SptArchive.Api/Program.cs`
- Spec: `docs/specs/query-api.feature`
