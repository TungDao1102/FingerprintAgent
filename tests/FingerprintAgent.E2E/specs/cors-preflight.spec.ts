import { test, expect, request as playwrightRequest } from '@playwright/test';

/**
 * CORS preflight tests for the FingerprintAgent HTTP API.
 *
 * Why request (not browser):
 *   Preflight is an HTTP-level concern. Using Playwright's request API skips
 *   the browser overhead and gives us deterministic access to response headers.
 *   The browser-driven CORS path is exercised separately in end-to-end.spec.ts.
 *
 * Agent assumption:
 *   The FingerprintAgent service is running on http://127.0.0.1:5043 with the
 *   default Cors.Mode = "wildcard". CI installs the MSI before running tests.
 */

const AGENT_ORIGIN = 'http://127.0.0.1:5043';
const SAAS_ORIGIN = 'http://127.0.0.1:8080';

test.describe('CORS preflight (OPTIONS /api/capture)', () => {
    test('returns 204 with valid wildcard CORS headers', async () => {
        const ctx = await playwrightRequest.newContext();
        try {
            const response = await ctx.fetch(`${AGENT_ORIGIN}/api/capture`, {
                method: 'OPTIONS',
                headers: {
                    Origin: SAAS_ORIGIN,
                    'Access-Control-Request-Method': 'POST',
                    'Access-Control-Request-Headers': 'Content-Type',
                },
            });

            // Status: 204 No Content (CorsMiddleware default success path).
            expect(response.status()).toBe(204);

            const headers = response.headers();

            // Wildcard origin: any SaaS frontend may call the agent.
            // Matches src/FingerprintAgent/Api/CorsMiddleware.cs (ApplyCorsHeaders).
            expect(headers['access-control-allow-origin']).toBe('*');

            // Methods the agent accepts for cross-origin requests.
            expect(headers['access-control-allow-methods']).toBe('POST, GET, OPTIONS');

            // Headers the agent will echo back (Content-Type is the only one the
            // capture endpoint needs for JSON bodies).
            expect(headers['access-control-allow-headers']).toBe('Content-Type');

            // Cache the preflight for 24h so the browser doesn't repeat it for
            // every request from the same SaaS page session.
            expect(headers['access-control-max-age']).toBe('86400');
        } finally {
            await ctx.dispose();
        }
    });

    test('returns CORS headers on actual POST response (not just preflight)', async () => {
        // CorsMiddleware.ApplyCorsHeaders is called for every response, not just
        // preflight, so the browser can read the response body cross-origin.
        const ctx = await playwrightRequest.newContext();
        try {
            const response = await ctx.fetch(`${AGENT_ORIGIN}/api/capture`, {
                method: 'POST',
                headers: {
                    'Content-Type': 'application/json',
                    Origin: SAAS_ORIGIN,
                },
                data: {
                    requestId: 'cors-post-test',
                    purpose: 'signature',
                    metadata: { source: 'cors-preflight.spec.ts' },
                },
            });

            expect(response.status()).toBe(200);

            const headers = response.headers();
            expect(headers['access-control-allow-origin']).toBe('*');
            expect(headers['access-control-allow-methods']).toBe('POST, GET, OPTIONS');
            expect(headers['access-control-allow-headers']).toBe('Content-Type');
        } finally {
            await ctx.dispose();
        }
    });

    // Future work (deferred from v1):
    //   Test: 'returns 403 in allowlist mode for non-allowed origin'
    //   Reason: would require toggling the running agent's Cors.Mode to "allowlist"
    //   mid-test. The agent does not expose a reconfigure endpoint for this; the
    //   ConfigFileWatcher reloads scanner/cors but only on file change. To add
    //   this test, write a config.json with Mode="allowlist" + empty allowedOrigins
    //   to a temp dir, point ConfigLoader at it, spin up the agent, then assert
    //   403 for the foreign origin. Out of scope for the manual workflow_dispatch
    //   CI gate; tracked as Phase 5+ enhancement.
});
