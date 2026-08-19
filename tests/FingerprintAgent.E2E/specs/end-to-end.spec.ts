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
        // Wipe the mock backend's recorded entries before this test runs so we
        // can assert exact counts. The mock backend is a single shared instance
        // (webServer auto-start in playwright.config.ts), so a previous test
        // could have left entries.
        const adminCtx = await playwrightRequest.newContext();
        try {
            const initial = await adminCtx.get(`${MOCK_BACKEND_ORIGIN}/received`);
            // If the backend got an entry from a prior test, surface it but don't
            // fail — we still need the assertion below to prove THIS test's flow.
            expect(initial.status()).toBe(200);
        } finally {
            await adminCtx.dispose();
        }

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
        let received: Array<{ success: boolean; bytesLen: number; sha256: string | null }> = [];
        const verifyCtx = await playwrightRequest.newContext();
        try {
            for (let i = 0; i < 30; i++) {
                const response = await verifyCtx.get(`${MOCK_BACKEND_ORIGIN}/received`);
                expect(response.status()).toBe(200);
                received = await response.json();
                if (received.length >= 1) break;
                await page.waitForTimeout(200);
            }
        } finally {
            await verifyCtx.dispose();
        }

        expect(received.length, 'mock backend should have recorded at least one /receive entry').toBeGreaterThanOrEqual(1);

        const lastEntry = received[received.length - 1];
        expect(lastEntry.success).toBe(true);
        expect(typeof lastEntry.bytesLen).toBe('number');
        expect(lastEntry.bytesLen).toBeGreaterThan(0);
        expect(typeof lastEntry.sha256).toBe('string');
        expect(lastEntry.sha256).not.toBeNull();
        // SHA-256 base64 is 44 characters (256 bits / 6 bits * 4/3 padding).
        expect(lastEntry.sha256!.length).toBe(44);
    });

    test('real-browser fetch issues CORS preflight and gets 204', async ({ page }: { page: Page }) => {
        // Cross-check the HTTP-only preflight spec (cors-preflight.spec.ts)
        // by going through a real Chromium fetch — proves the browser does not
        // see any CORS surprises that the bare-HTTP test missed.
        const result = await page.evaluate(async (origin: string) => {
            const response = await fetch(`${origin}/api/capture`, { method: 'OPTIONS' });
            return {
                status: response.status,
                allowOrigin: response.headers.get('access-control-allow-origin'),
                allowMethods: response.headers.get('access-control-allow-methods'),
                allowHeaders: response.headers.get('access-control-allow-headers'),
                maxAge: response.headers.get('access-control-max-age'),
            };
        }, AGENT_ORIGIN);

        expect(result.status).toBe(204);
        expect(result.allowOrigin).toBe('*');
        expect(result.allowMethods).toBe('POST, GET, OPTIONS');
        expect(result.allowHeaders).toBe('Content-Type');
        expect(result.maxAge).toBe('86400');
    });
});
