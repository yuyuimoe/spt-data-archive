# ADR-0008: Cache complete GraphQL responses in Redis with a dataset version in the key

**Status:** Accepted
**Date:** 2026-10-19
**Deciders:** Yui

## Context

The driver asked for a cache for the most requested queries. The archive runs for a long time and has low memory. The data changes only when the Archiver finishes a run.

A cached answer is wrong after a new run unless the cache learns about the new data.

## Decision

We will cache complete GraphQL responses. The key is the SHA-256 of the request body plus the current dataset version. The Archiver changes the dataset version at the end of every successful run. Old keys are never used again and expire by TTL.

Redis is the store. The Api and the Archiver fall back to an in-process memory cache when no Redis connection string exists.

## Options Considered

| Dimension               | Redis with version key (chosen) | In-process memory cache | HTTP or CDN cache (GET plus persisted queries) | No cache            |
|-------------------------|----------------------------------|-------------------------|-----------------------------------------------|---------------------|
| Shared by Api instances | Yes                              | No                      | Yes                                           | Not relevant        |
| Invalidation            | Automatic through the version    | Automatic on restart    | Needs purge rules                             | Not relevant        |
| Memory control          | `maxmemory` with LRU             | Competes with the Api   | Outside the app                               | None needed         |
| Complexity              | Low: one middleware              | Low                     | Medium                                        | None                |
| Fits POST requests      | Yes                              | Yes                     | No: needs GET                                 | Yes                 |

## Trade-off Analysis

A version key removes explicit invalidation, which is the hard part of caching. The cost is one Redis read per request for the version. A CDN cache is a good later addition, but it needs GET queries.

## Consequences

- **Easier:** Cached answers can never come from older data.
- **Harder:** The cache is only safe because the API is anonymous. Identical requests must give identical answers.
- **Harder:** Responses that contain `errors` are not cached. This is deliberate.
- **Revisit when:** The API adds authentication or per-user data. Remove the cache or add the user to the key.

## Verification

```gherkin
@unverified
Feature: The response cache never serves old data

  Scenario: A repeated query is answered from the cache
    Given the cache is empty
    When a client sends the same query twice
    Then the first response has the header "X-Cache: MISS"
    And the second response has the header "X-Cache: HIT"

  Scenario: A new archive run makes cached answers unreachable
    Given a cached answer exists
    When the Archiver finishes a successful run
    And the client sends the same query again
    Then the response has the header "X-Cache: MISS"

  Scenario: An error response is not cached
    Given a query that returns GraphQL errors
    When a client sends it twice
    Then both responses have the header "X-Cache: MISS"

  Scenario: The Api still works when Redis is down
    Given Redis is not reachable
    When a client sends a valid query
    Then the Api returns the data without the cache
```

## Action Items

1. [ ] Add an anonymous-only warning test, so authentication cannot be added without changing the cache.

## Links

- C4: `archive.api.queryCache`, `archive.redis`
- Code: `src/SptArchive.Api/Caching/QueryCacheMiddleware.cs`, `src/SptArchive.Core/CacheKeys.cs`
- Spec: `docs/specs/query-api.feature`
