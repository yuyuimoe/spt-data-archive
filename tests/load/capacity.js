// The capacity test: at what number of clients does an answer become slow?
//
//   USERS=500 k6 run tests/load/capacity.js
//
// "Clients" is an arrival rate, one request per second per client, so USERS is also the request rate.
// The client is a data browser: it pages through items with every field, sorts, and searches by name.
//
//   80% hot  — the ten pages a browser opens first. Their answers are in Redis, so the Api serves them
//              from memory, compresses them, and touches no database.
//   20% cold — a page that nobody asked for yet, so it runs against PostgreSQL and is stored for the
//              next client. Every cold request carries a unique tag, so it always misses.
//
// The verdict uses the three numbers the driver asks for: p95 <= 500 ms, p99 <= 1000 ms, and fewer than
// 1% failed requests. k6's own thresholds say the same, so a plain "k6 run" prints PASS or FAIL.
//
// Knobs: USERS, DURATION, PROFILE (heavy|typical), HOT_SHARE, ENCODING ('' = k6's default gzip,
//        'identity', 'br'), RUN_TAG, OUT.

import http from 'k6/http';
import { check } from 'k6';
import { Counter, Trend } from 'k6/metrics';

const users = Number(__ENV.USERS || 200);
const duration = __ENV.DURATION || '40s';
const hotShare = __ENV.HOT_SHARE === undefined ? 0.8 : Number(__ENV.HOT_SHARE);
const encoding = __ENV.ENCODING || '';
const release = __ENV.SPT_VERSION || '4.1.6';
const runTag = __ENV.RUN_TAG || 'cap';
const out = __ENV.OUT || 'summary.json';
const url = __ENV.BASE_URL || 'https://caddy/graphql';

// Two shapes of one page of items:
//   heavy   — 50 items with the raw _props of each: 116 KB, the widest page the cost limit allows.
//   typical — 50 items without _props: the list a data browser shows before a client opens one item.
// The heavy page is the worst case and it saturates the load generator before the Api; the typical page
// is what a browser really asks for and it is where the limit of the Api can be measured.
const profile = __ENV.PROFILE || 'heavy';
const ITEM_FIELDS = profile === 'typical'
  ? 'id name parentId type prototype weight width height stackMaxSize'
  : 'id name parentId type prototype weight width height stackMaxSize props';

// The ten pages a client opens first. Each one keeps its own tag, so its answer is one cache entry.
const HOT_PAGES = [
  `items(sptVersion: "${release}", first: 50, order: { name: ASC }) { nodes { ${ITEM_FIELDS} } }`,
  `items(sptVersion: "${release}", first: 50, order: { weight: DESC }) { nodes { ${ITEM_FIELDS} } }`,
  `items(sptVersion: "${release}", first: 50, order: { stackMaxSize: DESC }) { nodes { ${ITEM_FIELDS} } }`,
  `items(sptVersion: "${release}", first: 50, where: { type: { eq: "Item" } }, order: { name: ASC }) { nodes { ${ITEM_FIELDS} } }`,
  `customizations(sptVersion: "${release}", first: 50, order: { name: ASC }) { nodes { id name parentId type bodyPart availableAsDefault props } }`,
  `quests(sptVersion: "${release}", first: 50, order: { questName: ASC }) { nodes { id questName traderId location type side data } }`,
  `traders(sptVersion: "${release}", first: 20) { nodes { id name nickname currency location base services } }`,
  `translations(sptVersion: "${release}", first: 200) { nodes { scope languageCode key value } }`,
  `modItems(first: 50, order: { name: ASC }) { nodes { id name props } }`,
  `modCustomizations(first: 50, order: { name: ASC }) { nodes { id name type props } }`,
];

// The sorts of a cold page: each one makes PostgreSQL work on the whole table.
const SORTS = ['name', 'weight', 'width', 'height', 'stackMaxSize', 'parentId', 'prototype', 'type'];

// The searches of a cold page: a browser's search box, on a letter that a name may hold.
const SEARCHES = ['AK', 'M4', '9x19', 'armor', 'helmet', 'rig', 'scope', 'magazine', 'medkit', 'key'];

const hits = new Counter('cache_hit');
const misses = new Counter('cache_miss');
const shared = new Counter('cache_shared');
const ok = new Counter('status_ok');
const refused = new Counter('status_429');
const broken = new Counter('status_5xx');
const hitMs = new Trend('hit_ms', true);
const missMs = new Trend('miss_ms', true);
const answerBytes = new Trend('answer_bytes');

function hotBody(index) {
  return JSON.stringify({ query: `{ ${HOT_PAGES[index % HOT_PAGES.length]} } # hot-${index % HOT_PAGES.length}` });
}

function coldBody(tag) {
  // Half of the cold pages search by name, half sort the whole table. Both are what a browser does.
  const page = Math.random() < 0.5
    ? `items(sptVersion: "${release}", first: 50, where: { name: { contains: "${SEARCHES[Math.floor(Math.random() * SEARCHES.length)]}" } }, order: { name: ASC }) { nodes { ${ITEM_FIELDS} } }`
    : `items(sptVersion: "${release}", first: 50, order: { ${SORTS[Math.floor(Math.random() * SORTS.length)]}: ASC }) { nodes { ${ITEM_FIELDS} } }`;

  return JSON.stringify({ query: `{ ${page} } # ${tag}` });
}

function headers() {
  const head = { 'Content-Type': 'application/json' };
  if (encoding) {
    head['Accept-Encoding'] = encoding;
  }

  return head;
}

export const options = {
  scenarios: {
    capacity: {
      executor: 'constant-arrival-rate',
      rate: users,
      timeUnit: '1s',
      duration,
      // A virtual user holds one request in flight. At 20 ms an answer that is 2% of the offered rate;
      // the ceiling leaves room for an answer that gets ten times slower before k6 runs out of users.
      preAllocatedVUs: Math.max(20, Math.min(600, Math.ceil(users * 0.1))),
      // A virtual user holds one request in flight, so a run needs about rate x latency of them. The
      // ceiling is the memory of the generator, which is 2 GB like every other container here (ADR-0039).
      maxVUs: Math.max(200, Math.min(5000, Math.ceil(users * 1.5))),
      gracefulStop: '15s',
    },
  },
  thresholds: {
    'http_req_duration': ['p(95)<500', 'p(99)<1000'],
    'http_req_failed': ['rate<0.01'],
    'checks': ['rate>0.99'],
  },
  // The verdict needs these numbers, and k6 only reports the percentiles that are asked for here.
  summaryTrendStats: ['avg', 'min', 'med', 'p(50)', 'p(90)', 'p(95)', 'p(99)', 'max'],
};

// The hot pages are warmed first, so a run measures the load and not the first client's misses.
export function setup() {
  const warm = [];
  for (let index = 0; index < HOT_PAGES.length; index++) {
    warm.push(['POST', url, hotBody(index), { headers: headers(), tags: { phase: 'warmup' } }]);
  }

  const answers = http.batch(warm);
  const stored = answers.filter((answer) => answer.status === 200).length;
  if (stored !== HOT_PAGES.length) {
    throw new Error(`warmup stored ${stored} of ${HOT_PAGES.length} hot answers`);
  }

  return { warmed: stored };
}

export default function () {
  const hot = Math.random() < hotShare;
  const index = Math.floor(Math.random() * HOT_PAGES.length);
  const body = hot ? hotBody(index) : coldBody(`cold-${runTag}-${__VU}-${__ITER}`);

  const response = http.post(url, body, { headers: headers() });
  const cache = response.headers['X-Cache'] || 'none';
  const ms = response.timings.duration;

  if (cache === 'HIT') {
    hits.add(1);
    hitMs.add(ms);
  } else if (cache === 'MISS') {
    misses.add(1);
    missMs.add(ms);
  } else if (cache === 'SHARED') {
    shared.add(1);
  }

  if (response.status === 200) {
    ok.add(1);
    answerBytes.add(response.body ? response.body.length : 0);
  } else if (response.status === 429) {
    refused.add(1);
  } else if (response.status >= 500) {
    broken.add(1);
  }

  const answered = encoding === 'br'
    ? (r) => r.status === 200
    : (r) => r.status === 200 && r.body && r.body.indexOf('"data"') >= 0;

  check(response, {
    'answered with data': answered,
    'answered from the cache of the archive': () => cache !== 'none',
  });
}

function p(metric, key) {
  const values = metric && metric.values ? metric.values : {};
  return values[key] === undefined ? null : values[key];
}

export function handleSummary(data) {
  const m = (name) => data.metrics[name];
  const summary = {
    users,
    duration,
    hotShare,
    profile,
    encoding: encoding || 'gzip (k6 default)',
    requests: m('http_reqs') ? m('http_reqs').values.count : 0,
    rate: m('http_reqs') ? m('http_reqs').values.rate : 0,
    failed: p(m('http_req_failed'), 'rate'),
    checks: p(m('checks'), 'rate'),
    p50: p(m('http_req_duration'), 'p(50)'),
    p90: p(m('http_req_duration'), 'p(90)'),
    p95: p(m('http_req_duration'), 'p(95)'),
    p99: p(m('http_req_duration'), 'p(99)'),
    max: p(m('http_req_duration'), 'max'),
    hit: m('cache_hit') ? m('cache_hit').values.count : 0,
    miss: m('cache_miss') ? m('cache_miss').values.count : 0,
    shared: m('cache_shared') ? m('cache_shared').values.count : 0,
    hitP50: p(m('hit_ms'), 'p(50)'),
    hitP95: p(m('hit_ms'), 'p(95)'),
    missP50: p(m('miss_ms'), 'p(50)'),
    missP95: p(m('miss_ms'), 'p(95)'),
    answerBytes: p(m('answer_bytes'), 'avg'),
    statusOk: m('status_ok') ? m('status_ok').values.count : 0,
    status429: m('status_429') ? m('status_429').values.count : 0,
    status5xx: m('status_5xx') ? m('status_5xx').values.count : 0,
    dropped: p(m('dropped_iterations'), 'count') || 0,
    vusMax: m('vus_max') ? m('vus_max').values.value : null,
  };

  return { [out]: JSON.stringify(summary, null, 2) };
}
