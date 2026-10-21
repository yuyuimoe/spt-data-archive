// A cache stampede: many clients ask for one answer that nobody has yet.
//
//   VUS=200 k6 run tests/load/stampede.js
//
// Every virtual user sends the same new query within the same instant. Without single-flight, every
// one of them runs its own query against PostgreSQL (ADR-0037). With it, one query runs and the
// requests that arrive while it runs answer with X-Cache: SHARED.
//
// What to read: cache_miss must be 1 for a burst, cache_shared must carry the rest of the burst, and
// cold_latency is what a client waits for the shared answer.

import { cold, misses, send, shared } from './lib.js';

const vus = Number(__ENV.VUS || 200);

export const options = {
  scenarios: {
    stampede: {
      executor: 'shared-iterations',
      vus: vus,
      iterations: vus,
      maxDuration: '2m',
    },
  },
  thresholds: {
    checks: ['rate>0.99'],
    'http_req_failed': ['rate<0.01'],
  },
};

export default function () {
  // One tag for the whole run, so one cold answer, and every virtual user wants it.
  const tag = `stampede-${__ENV.RUN_TAG || 'a'}`;
  const { response } = send(tag, 20);
  cold.add(response.timings.duration);
}

export function handleSummary(data) {
  const miss = data.metrics.cache_miss ? data.metrics.cache_miss.values.count : 0;
  const sharedCount = data.metrics.cache_shared ? data.metrics.cache_shared.values.count : 0;
  const hit = data.metrics.cache_hit ? data.metrics.cache_hit.values.count : 0;

  console.log(`stampede: ${miss} miss, ${sharedCount} shared, ${hit} hit for ${vus} clients`);
  return {};
}
