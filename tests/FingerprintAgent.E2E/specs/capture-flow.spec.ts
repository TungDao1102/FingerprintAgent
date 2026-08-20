import { test, expect, request as playwrightRequest } from '@playwright/test';

/**
 * Capture flow tests — verifies POST /api/capture returns a valid PNG payload.
 *
 * These tests use Playwright's request API (HTTP-only, no browser) for direct,
 * deterministic assertions on the wire format. The browser-driven round-trip is
 * covered separately in end-to-end.spec.ts.
 *
 * Agent assumption:
 *   The agent is running on http://127.0.0.1:5043 with `scanner.mockMode: true`
 *   (the default in shipped config.json). MockScannerAdapter produces a
 *   deterministic 320x240 PNG with a known SHA-256.
 */

const AGENT_ORIGIN = 'http://127.0.0.1:5043';
const VALID_REQUEST = {
    thamChieuId: 'capture-flow-test',
    maPhieu: 'CAP-001',
    loaiPhieu: 'signature',
    vaiKyId: null,
    nhanLucId: null,
    metadata: { source: 'capture-flow.spec.ts' },
};

test.describe('Capture flow (POST /api/capture)', () => {
    test('returns 200 with valid PNG base64 + SHA-256 in wildcard mode', async () => {
        const ctx = await playwrightRequest.newContext();
        try {
            const response = await ctx.fetch(`${AGENT_ORIGIN}/api/capture`, {
                method: 'POST',
                headers: {
                    'Content-Type': 'application/json',
                    Origin: 'http://127.0.0.1:8080',
                },
                data: VALID_REQUEST,
            });

            expect(response.status()).toBe(200);

            const body = await response.json();

            // Wire-format DTO: src/FingerprintAgent/Models/CaptureResponse.cs
            expect(body.isSuccess).toBe(true);
            expect(typeof body.imageBytes).toBe('string');
            expect(body.imageBytes.length).toBeGreaterThan(0);

            // Base64-decoded byte length should be > 0 (mock returns a real PNG).
            const decodedBytes = Buffer.from(body.imageBytes, 'base64');
            expect(decodedBytes.length).toBeGreaterThan(0);

            // PNG magic number: 89 50 4E 47 0D 0A 1A 0A
            expect(decodedBytes[0]).toBe(0x89);
            expect(decodedBytes[1]).toBe(0x50);
            expect(decodedBytes[2]).toBe(0x4e);
            expect(decodedBytes[3]).toBe(0x47);

            // Mime type is image/png from the mock adapter.
            expect(body.mimeType).toBe('image/png');

            // SHA-256 base64 is exactly 44 characters (256 bits / 6 bits per char * 4/3 padding).
            expect(typeof body.verificationData).toBe('string');
            expect(body.verificationData.length).toBe(44);

            // ISO-8601 timestamp the agent stamps on every successful response
            // (CaptureResponse.CapturedAt). The legacy Timestamp field is reserved
            // for error responses and is intentionally null here — see
            // ErrorHandlingTests.CaptureHandler_SuccessResponse_DoesNotIncludeVendorErrorCodeOrTimestamp.
            expect(typeof body.capturedAt).toBe('string');
            expect(new Date(body.capturedAt).toString()).not.toBe('Invalid Date');

            // No error fields on success.
            expect(body.errorMessage == null).toBe(true);
            expect(body.errorCode == null).toBe(true);
            expect(body.vendorErrorCode == null).toBe(true);
        } finally {
            await ctx.dispose();
        }
    });

    test('returns 400 when required field (maPhieu) is missing', async () => {
        const ctx = await playwrightRequest.newContext();
        try {
            // maPhieu intentionally omitted.
            const invalidRequest = { ...VALID_REQUEST };
            delete (invalidRequest as { maPhieu?: string }).maPhieu;

            const response = await ctx.fetch(`${AGENT_ORIGIN}/api/capture`, {
                method: 'POST',
                headers: {
                    'Content-Type': 'application/json',
                    Origin: 'http://127.0.0.1:8080',
                },
                data: invalidRequest,
            });

            expect(response.status()).toBe(400);

            const body = await response.json();
            expect(body.isSuccess).toBe(false);
            expect(body.errorCode).toBe('INVALID_REQUEST');
        } finally {
            await ctx.dispose();
        }
    });
});
