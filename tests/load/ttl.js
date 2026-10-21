// When an answer expires.
//
//   k6 run tests/load/ttl.js
//
// One client asks the same question every five seconds for 95 seconds. The answer is stored with a
// lifetime of 60 seconds spread by 10 % (ADR-0037), so it must stop hitting between 54 and 66
// seconds after it was stored.
//
// What to read: the printed timeline, and the check "expired between 50 and 75 seconds".

import { check, sleep } from 'k6';
import { cold, send } from './lib.js';

export const options = {
  vus: 1,
  iterations: 1,
  maxDuration: '3m',
};

export default function () {
  const tag = `ttl-${Date.now()}`;
  const timeline = [];
  let expiredAfter = -1;

  for (let second = 0; second <= 90; second += 5) {
    const { response, cache } = send(tag);
    timeline.push(`${second}s:${cache}:${Math.round(response.timings.duration)}ms`);

    if (second > 0 && cache === 'MISS' && expiredAfter < 0) {
      expiredAfter = second;
    }

    if (second === 0) {
      cold.add(response.timings.duration);
    }

    sleep(5);
  }

  console.log(`ttl: the answer expired after about ${expiredAfter} seconds`);
  console.log(`ttl: ${timeline.join(' ')}`);

  check(expiredAfter, {
    'expired between 50 and 75 seconds': (value) => value >= 50 && value <= 75,
  });
}
