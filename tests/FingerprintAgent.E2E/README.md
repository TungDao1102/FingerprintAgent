# FingerprintAgent E2E Tests

Browser-based end-to-end tests for the FingerprintAgent Windows Service.
Real Chromium issues CORS preflight + `POST /api/capture` against a
running agent on `http://127.0.0.1:5043`, then a mock SaaS page
forwards the captured PNG to a Node `http.createServer` mock backend.

## Prerequisites

- Node.js 22 LTS (or later 20.x).
- FingerprintAgent Windows Service **already running** on
  `127.0.0.1:5043` with `scanner.mockMode: true` (the default in shipped
  `config.json`).

## Install + run

```powershell
npm ci                       # install pinned @playwright/test 1.55.1
npm run install-browsers     # download Chromium ~120 MB
npm test                     # runs the Playwright suite
```

`npm test` also starts the mock backend on port 8080 via
`playwright.config.ts -> webServer` — no separate process needed.

The mock backend is a small `http.createServer` in
`fixtures/mock-backend.ts`. It serves:

| Endpoint | Role |
|---|---|
| `GET /health` | `{status:"ok"}` — used by `end-to-end.spec.ts` as a precondition check |
| `GET /saas-page.html` | Serves the embedded-JS page that does the real capture chain |
| `POST /receive` | Records the forwarded capture summary (pushes into `received[]`) |
| `GET /received` | JSON array — debug surface for spec assertions |
| `GET /received/last` | Last entry, or 404 if empty |

## Test layout

```
tests/FingerprintAgent.E2E/
├── package.json
├── tsconfig.json
├── playwright.config.ts
├── fixtures/
│   ├── saas-page.html           # embedded JS: fetch agent -> forward to /receive -> set title
│   └── mock-backend.ts          # Node http server (see endpoints table)
└── specs/
    ├── cors-preflight.spec.ts   # OPTIONS /api/capture via Playwright request API
    ├── capture-flow.spec.ts     # POST /api/capture via Playwright request API
    └── end-to-end.spec.ts       # full browser round-trip (page -> agent -> mock backend)
```

## What's covered

| Spec | Validates |
|---|---|
| `cors-preflight.spec.ts` | `OPTIONS /api/capture` returns 204 + correct wildcard CORS headers |
| `capture-flow.spec.ts` | `POST /api/capture` returns 200 + valid PNG (magic bytes verified) + 44-char SHA-256 base64; missing required field returns 400 |
| `end-to-end.spec.ts` | Real Chromium loads the SaaS page, runs the capture chain, mock backend receives the forwarded summary, title updates to `OK`; real-browser CORS preflight also returns 204 |

## Why Playwright 1.55.1 is pinned

Chromium 142+ (Playwright 1.56.x) blocks public-origin `fetch` to private
network (localhost) by default. Workarounds require
`--ip-address-space-overrides=127.0.0.1:0=public` plus
`use.permissions: ['local-network-access']`. 1.55.1 ships Chromium 141
and works without these flags. Revisit when 1.56+ stabilizes.

## Running locally without Playwright

If you only want to smoke-test the agent from a browser manually, the
SaaS page works as a standalone file too — but it MUST be served from a
real HTTP origin (not `file://`), so run:

```powershell
npx ts-node fixtures/mock-backend.ts
# Open http://127.0.0.1:8080/saas-page.html in your browser.
# Title flips to OK or FAIL when the chain completes.
```

## CI

The `.github/workflows/e2e.yml` workflow runs this suite on
`windows-latest` after building and installing the MSI. Trigger manually
via `workflow_dispatch` — never on every push (the suite is heavy).
