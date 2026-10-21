// A hot answer: the same query, arriving at a steady rate, served from Redis.
//
//   k6 run tests/load/steady.js          # 100 requests per second for 20 seconds
//   RATE=500 k6 run tests/load/steady.js
//
// What it shows: the throughput and the latency that a cache hit gives, against the cold latency
// that the same test reports as cold_latency (the first request of the run).

import { cold, send } from './lib.js';

const rate = Number(__ENV.RATE || 100);

export const options = {
  scenarios: {
    steady: {
      executor: 'constant-arrival-rate',
      rate: rate,
      timeUnit: '1s',
      duration: __ENV.DURATION || '20s',
      preAllocatedVUs: 50,
      maxVUs: 500,
    },
  },
  thresholds: {
    checks: ['rate>0.99'],
    'http_req_duration': ['p(95)<50'],
    'http_req_failed': ['rate<0.01'],
  },
};

export default function () {
  const { response, cache } = send('steady');

  if (cache === 'MISS') {
    cold.add(response.timings.duration);
  }
}
