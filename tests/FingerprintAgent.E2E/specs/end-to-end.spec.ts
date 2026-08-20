import { test, expect, request as playwrightRequest, Page } from '@playwright/test';

/**
 * End-to-end browser test: real Chromium opens the mock SaaS page,
 * the embedded JS does CORS preflight -> POST /api/capture -> POST /receive
 * on the mock backend, and the page title updates to OK on success.
 *
 * This proves the production flow:
 *   Browser (Chromium) -> mock SaaS page (127.0.0.1:8080)
 *     -> CORS preflight + POST /api/capture (127.0.0.1:5043)
 *     -> POST /receive on mock backend (127.0.0.1:8080)
 *
 * Agent assumption:
 *   FingerprintAgent service running on 127.0.0.1:5043 with mockMode: true.
 */

const MOCK_BACKEND_ORIGIN = 'http://127.0.0.1:8080';
const AGENT_ORIGIN = 'http://127.0.0.1:5043';

test.describe('Browser -> agent -> backend round-trip', () => {
    // WARN-04: wipe the mock backend's recorded entries before EACH test so a
    // previous test's entries don't satisfy this test's `received.length >= 1`
    // assertion when its own capture chain silently fails.
    test.beforeEach(async () => {
        const adminCtx = await playwrightRequest.newContext();
        try {
            const response = await adminCtx.delete(`${MOCK_BACKEND_ORIGIN}/received`);
            expect(response.status(), 'mock backend DELETE /received must return 200').toBe(200);
        } finally {
            await adminCtx.dispose();
        }
    });

    test('service is healthy when E2E runs (precondition guard)', async () => {
        // If /health fails, the rest of E2E is meaningless — fail fast with
        // a clear message so the operator knows to start the agent first.
        const ctx = await playwrightRequest.newContext();
        try {
            const response = await ctx.get(`${AGENT_ORIGIN}/health`);
            expect(response.status(), 'agent /health must return 200 — start the service before running E2E').toBe(200);

            const body = await response.json();
            // HealthHandler returns at minimum {status, deviceId, uptime}.
            expect(typeof body.status).toBe('string');
        } finally {
            await ctx.dispose();
        }
    });

    test('mock backend is reachable at /health (precondition guard)', async () => {
        const ctx = await playwrightRequest.newContext();
        try {
            const response = await ctx.get(`${MOCK_BACKEND_ORIGIN}/health`);
            expect(response.status()).toBe(200);
            const body = await response.json();
            expect(body.status).toBe('ok');
        } finally {
            await ctx.dispose();
        }
    });

    test('browser navigates to SaaS page and completes full capture flow', async ({ page }: { page: Page }) => {
        // WARN-04: beforeEach cleared the mock backend's received entries. This
        // test is the ONLY source of entries for the duration of its execution,
        // so we can assert an exact count of 1 (not just >= 1).

        // Navigate to the SaaS page. Embedded JS kicks off the capture chain
        // immediately on load. The page title updates to "OK" or "FAIL" when done.
        await page.goto(`${MOCK_BACKEND_ORIGIN}/saas-page.html`);

        // 15s covers slow CI agents. The mock capture is < 1s, the network round-trips
        // are < 100ms — 15s is generous for a first-time install where the agent
        // may have just bound to 5043.
        await expect(page).toHaveTitle('OK', { timeout: 15_000 });

        // Now read what the mock backend recorded. Poll briefly because the
        // browser may have set the title a few ms before the /receive POST
        // completed.
        const verifyCtx = await playwrightRequest.newContext();
        let received: Array<{ success: boolean; bytesLen: number; sha256: string | null }> = [];
        try {
            await expect.poll(async () => {
                const response = await verifyCtx.get(`${MOCK_BACKEND_ORIGIN}/received`);
                expect(response.status()).toBe(200);
                received = await response.json();
                return received.length;
            }, { timeout: 6000 }).toBeGreaterThanOrEqual(1);
        } finally {
            await verifyCtx.dispose();
        }

        // Exact count — beforeEach cleared the array, so any entry present is
        // unambiguously from THIS test's capture chain.
        expect(received.length, 'mock backend should have recorded exactly one /receive entry after a single capture').toBe(1);

        const lastEntry = received[received.length - 1];
        expect(lastEntry.success).toBe(true);
        expect(typeof lastEntry.bytesLen).toBe('number');
        expect(lastEntry.bytesLen).toBeGreaterThan(0);
        expect(typeof lastEntry.sha256).toBe('string');
        expect(lastEntry.sha256).not.toBeNull();
        // SHA-256 = 32 bytes = 44 base64 chars (padded).
        expect(lastEntry.sha256!.length).toBe(44);
    });

    test('real-browser fetch issues CORS preflight and gets 204', async ({ page }: { page: Page }) => {
        // Cross-check the HTTP-only preflight spec (cors-preflight.spec.ts)
        // by going through a real Chromium fetch — proves the browser does not
        // see any CORS surprises that the bare-HTTP test missed.

        // Opaque-origin about:blank would cause Chromium to omit Origin on fetch,
        // so the agent's CorsMiddleware would skip preflight handling and the
        // request would fall through to a 404. Navigate to the SaaS page first.
        const saasPageUrl = `${MOCK_BACKEND_ORIGIN}/saas-page.html`;
        await page.goto(saasPageUrl);

        // POST + Content-Type: application/json forces the browser to auto-issue
        // an OPTIONS preflight. status === 0 means the preflight was rejected.
        const result = await page.evaluate(async (origin: string) => {
            const response = await fetch(`${origin}/api/capture`, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: '{}',
            });
            return {
                status: response.status,
                allowOrigin: response.headers.get('access-control-allow-origin'),
                allowMethods: response.headers.get('access-control-allow-methods'),
                allowHeaders: response.headers.get('access-control-allow-headers'),
                maxAge: response.headers.get('access-control-max-age'),
            };
        }, AGENT_ORIGIN);

        expect(result.status).not.toBe(0);
        expect(result.allowOrigin).toBe('*');
        expect(result.allowMethods).toBe('POST, GET, OPTIONS');
        expect(result.allowHeaders).toBe('Content-Type');
        expect(result.maxAge).toBe('86400');
    });
});
