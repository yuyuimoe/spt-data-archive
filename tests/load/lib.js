// Shared parts of the load tests of the public GraphQL Api (ADR-0008, ADR-0037).
//
// The cache key is the SHA-256 of the whole request body, so every test adds its own tag to the
// query. A tag that is new makes the answer cold; the same tag makes the answer hit.

import http from 'k6/http';
import { check } from 'k6';
import { Counter, Trend } from 'k6/metrics';

export const baseUrl = __ENV.BASE_URL || 'http://127.0.0.1:5099/graphql';
export const release = __ENV.SPT_VERSION || '4.1.6';

export const hits = new Counter('cache_hit');
export const misses = new Counter('cache_miss');
export const shared = new Counter('cache_shared');
export const cold = new Trend('cold_latency', true);

// Every field of an item: the widest page the cost limit allows, so the answer is about 500 KB.
const fields = 'id name parentId type prototype weight width height stackMaxSize sptVersion props';

export function body(tag, first = 200) {
  return JSON.stringify({
    query: `{ items(sptVersion: "${release}", first: ${first}) { nodes { ${fields} } } } # ${tag}`,
  });
}

export function send(tag, first = 200) {
  const response = http.post(baseUrl, body(tag, first), {
    headers: { 'Content-Type': 'application/json' },
  });

  const cache = response.headers['X-Cache'] || 'none';
  if (cache === 'HIT') hits.add(1);
  else if (cache === 'MISS') misses.add(1);
  else if (cache === 'SHARED') shared.add(1);

  check(response, {
    'answered with data': (r) => r.status === 200 && r.body.includes('"data"'),
    'answered from a known cache state': () => cache !== 'none',
  });

  return { response, cache };
}
