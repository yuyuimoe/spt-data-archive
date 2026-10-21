# ADR-0037: Survive a burst on one answer, and let a client repeat a request

**Status:** Proposed
**Date:** 2026-10-21
**Deciders:** Yui

## Context

ADR-0008 caches a complete GraphQL response for one minute under the dataset version and a hash of the request body. Two problems follow.

**A stampede.** A cached answer expires while clients still ask for it. Every request that arrives before the first one finishes goes to PostgreSQL. Measured on the local Api, 240 requests for one cold answer at 50 clients ran 240 queries at 495 requests per second. The database does the same work many times for one answer.

**A lost answer.** A client may lose the network between its request and the answer. It then has no way to ask again without running the query again. The Api is read-only, so a repeat costs time and no data. A repeat is still worth avoiding. A client needs a way to say "this is the same request".

The driver asked for three changes: protect the cache from a stampede, let an answer expire in about one minute, and offer idempotency.

## Decision

**One query per answer, in flight.** `SingleFlight` keeps one execution per cache key. The first request runs the query. The requests that arrive while it runs wait for that same answer and answer with the header `X-Cache: SHARED`. The map of flights holds at most `MaxFlights` entries (1024). A request beyond the bound runs its own query, so a flood of distinct keys cannot grow memory without limit. A failure is never shared: the waiting request answers on its own, uncached.

**One minute, spread.** An answer lives `TtlSeconds` (60). The store spreads that life by `TtlJitterPercent` (10), so keys that were stored together do not expire in the same millisecond. Without the spread, a warm cache of a thousand keys becomes a burst of a thousand misses every minute.

**A client may name a request.** A client may send an `Idempotency-Key` header. The key must be 8 to 128 characters of `A-Z`, `a-z`, `0-9`, `.`, `:`, `-` or `_`. Any other value is refused with `400` and the code `IDEMPOTENCY_KEY_INVALID`. An arbitrary string from a client must not become a cache key of its own shape.

An answer stored under a key keeps the SHA-256 of the request body in front of it. A replay with the same body answers from the store and sets `Idempotency-Replayed: true`. A key used again for a different body is refused with `409` and the code `IDEMPOTENCY_KEY_REUSED`, so one client's key never returns another client's answer. Such an answer lives `IdempotencySeconds` (300), because a client repeats a request in seconds, not hours.

The dataset version stays in the key, as ADR-0008 says. A new Archiver run makes every old answer unreachable, idempotent answers included.

## Options Considered

| Dimension | One flight per key (chosen) | Redis lock across instances | Early refresh (XFetch) | Nothing |
|---|---|---|---|---|
| Queries for one cold answer | One per Api instance | One for the whole deployment | One, refreshed before expiry | One per request |
| Waits on Redis for a lock | No | Yes, one round trip for every miss | No | No |
| Works after a failed leader | Yes, the waiter answers on its own | Yes, when the lock expires | Yes | Yes |
| Complexity | One small class | A lock protocol and a lease | A probability formula | None |

For the idempotency key, the alternative was to store nothing and trust the client to repeat the query. That costs a query per retry, and a retry during an incident is exactly when the database is busiest.

## Trade-off Analysis

A single flight is per process. Two Api instances still run two queries for one cold answer. That is acceptable while the deployment runs one instance, and the table above states the fix: a Redis lock when a second instance arrives.

The jitter makes the lifetime of an answer imprecise: it lives 54 to 66 seconds. Nothing depends on the exact second, because the dataset version and not the clock decides which data is current.

An idempotency key gives a client a way to store a response in Redis that the client names. The bound on length, on the allowed characters and on the lifetime keeps that from becoming free storage. A client that wants to fill Redis with 300-second entries can already fill it with ordinary queries, so the key adds no new power.

## Consequences

- **Easier:** A hot answer that expires costs one query, not one per waiting request.
- **Easier:** A client can repeat a request safely and see that the answer is a replay.
- **Harder:** The cache now holds two kinds of entry with two lifetimes. The stored value of an idempotent answer also has a header in front of it.
- **Harder:** The `X-Cache` header has a third value, `SHARED`, which a load test must read correctly.
- **Revisit when:** The Api runs more than one instance, or the Api adds authentication. A key must then belong to a user, not to the client that typed it.

## Verification

```gherkin
@unverified
Feature: One answer is computed once

  Scenario: Requests that miss together run one query
    Given the cache is empty
    When twenty clients send the same query at the same time
    Then one query runs
    And one response has the header "X-Cache: MISS"
    And nineteen responses have the header "X-Cache: SHARED"

  Scenario: An answer expires in about one minute
    Given an answer is stored
    Then its lifetime is between 54 and 66 seconds

  Scenario: A key that names a request replays its answer
    Given a client sends a query with the idempotency key "order-42-abc"
    When the client sends the same query with the same key
    Then the second response is the same answer
    And it has the header "Idempotency-Replayed: true"
    And the query ran once

  Scenario: A key used for another request is refused
    Given a client sent a query with the idempotency key "order-42-abc"
    When the client sends another query with that key
    Then the response is a "409"
    And the error code is "IDEMPOTENCY_KEY_REUSED"

  Scenario: A malformed key is refused
    When a client sends a query with the idempotency key "short"
    Then the response is a "400"
    And the error code is "IDEMPOTENCY_KEY_INVALID"
```

## Action Items

1. [ ] Add a Redis lock, so one cold answer costs one query for the whole deployment, when a second Api instance arrives.
2. [ ] Put the new settings in `.env.example`, next to the other cache settings.
3. [ ] Agree the `Idempotency-Key` contract with the data browser that consumes the Api.

## Links

- ADR-0008: the response cache and its dataset version key
- C4: `archive.api.queryCache`, `archive.redis`
- Code: `src/SptArchive.Api/Caching/SingleFlight.cs`, `QueryCacheMiddleware.cs`, `QueryCacheOptions.cs`
- Spec: `docs/specs/query-api.feature`
