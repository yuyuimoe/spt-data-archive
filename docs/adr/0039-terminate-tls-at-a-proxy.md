# ADR-0039: Terminate TLS at a proxy, and name the client there

**Status:** Accepted
**Date:** 2026-10-21
**Ratified:** 2026-10-21 by Yui
**Deciders:** Yui

## Context

The audit of 2026-10-21 (finding F-03) left the Api without TLS and without `HSTS`. The Api answered plain
HTTP on a published port, so a client reached it with nothing in front, and the rate limit of ADR-0038
charged every request to the address it saw: the client itself.

A reverse proxy changes that fact. Behind a proxy the peer of every request is the proxy, so a limit that
reads the peer address alone gives the whole world one shared bucket. The client address has to come from
the header the proxy writes, and only from a proxy that the Api was told to trust.

The driver asked for Caddy as the proxy, and said that the deployment will also sit behind Pangolin in
production. The load test of the same day (docs/load/capacity-2026-10-21.html) put the proxy in the
measured path, so this decision is measured and not only designed.

## Decision

1. **Caddy is the one public door.** `docker/caddy/Caddyfile` holds it, and `compose.yaml` runs it as a
   fifth container. The Api publishes no port: only the proxy does, on `API_PORT` for a plain request
   (which answers a redirect) and on `HTTPS_PORT` for TLS.

2. **Caddy terminates TLS with its own certificate authority on a machine with no public name.** The
   setting is `tls internal`, and the authority lives in the volume `caddy_data` so that a restart does
   not change the certificate that clients trust. A public deployment puts the real name in
   `ARCHIVE_HOST` and lets Caddy or Pangolin obtain the certificate.

3. **`HSTS` is set at the proxy, and nowhere else.** One year and a half, with subdomains. The Api speaks
   plain HTTP behind the proxy and cannot know whether the client arrived over TLS, so it cannot own this
   header. `EnableForHttps` stays false in the Api for the same reason: the Api never answers TLS itself.

4. **The proxy repeats the three headers of the Api** (`nosniff`, `no-referrer`, `X-Frame-Options: DENY`),
   because a response that Caddy makes itself — a refusal, a redirect — carries no header from the Api.

5. **The proxy does not compress.** The Api already compresses one answer for every client and stores it
   uncompressed (ADR-0038). A second compression would spend a core to change nothing.

6. **The Api believes `X-Forwarded-For` from one address: the proxy.** `RateLimit:TrustForwardedFor`
   turns the reading on, and `RateLimit:TrustedProxies` / `RateLimit:TrustedNetworks` name who may claim
   it. Both are off and empty by default, so the Api trusts no header unless a deployment says so.
   `compose.yaml` gives Caddy the fixed address `172.28.0.250` and believes that address alone.

7. **The walk of the header starts at the right and stops at the first address that is not a proxy.** The
   nearest proxy writes the rightmost entry, so a client that prepends a forged address cannot move the
   bucket of another client. A chain of trusted proxies is walked to the first untrusted address.

## Trade-off Analysis

What becomes true:

- A client reaches the archive over TLS, and the browser refuses a plain request after the first one
  because of `HSTS`.
- The rate limit gives one bucket per client and not one for the world. Measured: two containers on the
  same network answer `200 200 200 429 429` with a bucket of three, at the same time, and a forged
  `X-Forwarded-For` changes nothing.
- The proxy costs CPU. On the measured stack it was the second largest consumer at 6 000 clients: 1.7 of
  its 4 cores, most of it TLS and the JSON access log, which writes one line per request (ADR-0038
  amendment has the numbers).

What becomes harder:

- **The address of the proxy is a fact of the deployment, not of the Api.** Change it and the limit
  silently collapses into one shared bucket. The name `CADDY_ADDRESS` exists in `.env.example` for this
  reason, and a test in `ClientAddressTests` covers the form of the setting that an environment variable
  takes.
- **A trusted network is a trap.** The live test of 2026-10-21 first trusted `10.77.0.0/24`, which held
  both the proxy and the clients of the rig. Every client was then a proxy, so the walk skipped each one
  and fell back to the proxy: one shared bucket again. Name the proxy, not the network it lives in.
- **Pangolin in front means two proxies.** Caddy must trust Pangolin's address for the client address to
  survive the second hop, or Caddy reads the header of an untrusted client and replaces it with
  Pangolin's own address. That is one line in the Caddyfile (`trusted_proxies`) in the change that adds
  Pangolin.
- The certificate of the local authority is short-lived and must be installed once by a browser or by a
  test client (`tests/load/README.md` has the command).

## Consequences

- The Api has no published port any more: a client that bypasses the proxy cannot reach it, so the limit
  and `HSTS` cannot be skipped.
- A deployment that adds Pangolin has one more hop to name in `trusted_proxies`, and the certificate of
  the local authority must be trusted by Pangolin while Caddy holds it.
- The measure of the load test changes with the proxy in the path: TLS and the access log cost CPU that
  the Api did not spend before (1.7 of 4 cores at 6 000 clients).

## Options Considered

The alternatives, and why each lost.

- **TLS inside the Api (Kestrel `UseHttps`).** Each instance would need the certificate and its renewal,
  and a second instance in front of the same name would need a shared store. A proxy does this once.
- **No proxy, and `HSTS` from the Api.** A header over plain HTTP is a claim a client may ignore, and it
  would be wrong the moment TLS ends somewhere else.
- **`ForwardedHeadersMiddleware` of ASP.NET.** It rewrites the scheme and the host as well as the address,
  which the Api does not need, and it consumes the header before the limiter reads it. One function that
  resolves the address is testable without a server, and it is what the limiter actually uses.
- **Trusting a whole network.** Rejected by measurement: see the trap above.

## Verification

The scenarios live in `docs/specs/query-api.feature`, under "The guard".

```gherkin
@unverified
Feature: A proxy terminates TLS and names the client

  Scenario: A client behind the proxy is charged to its own address
    Given the Api believes the address of one proxy
    And two clients behind the proxy each send four requests while the Api allows a burst of three
    Then each client receives three answers and then a refusal

  Scenario: A forged forwarding header moves no bucket
    Given the Api believes the address of one proxy
    When a client behind the proxy sends a header that names another address
    Then the request is charged to the address of the client
    And the refusal says "RATE_LIMITED"

  Scenario: The proxy terminates TLS and states HSTS
    When a client asks for the query endpoint over HTTPS
    Then the answer has "Strict-Transport-Security" for one year and a half
    And the answer names no server
    And a plain request is answered with a redirect to HTTPS
```

## Action Items

1. [x] Add the Caddyfile, the container and the fixed address to `compose.yaml`.
2. [x] Resolve the client address from `X-Forwarded-For` behind a trusted proxy, with tests.
3. [ ] Add `trusted_proxies` to the Caddyfile in the change that puts Pangolin in front.
4. [ ] Obtain a real certificate in the deployment that has a public name.
5. [ ] Sample the access log of the proxy, which cost 1.7 of 4 cores at 6 000 clients.

## Links

- ADR-0001: the deployment and its edge proxy
- ADR-0038: the rate limit that needs the real client address, and the compression that stays in the Api
- ADR-0008: the response cache, which stores one uncompressed answer for every client
- Finding F-03 in `docs/security/audit-2026-10-21.md`
- `docs/load/capacity-2026-10-21.html`: what the proxy costs at load
- Code: `docker/caddy/Caddyfile`, `src/SptArchive.Api/Edge/ClientAddress.cs`,
  `src/SptArchive.Api/Edge/TrustedProxy.cs`, `compose.yaml`
- TD-56, TD-57 in `docs/tech-debt.md`
