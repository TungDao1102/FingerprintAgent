/**
 * Mock backend for FingerprintAgent E2E tests.
 *
 * Two roles:
 *   1. Static host for fixtures/saas-page.html (so the page loads from a real
 *      HTTP origin — `file://` cannot fetch() to http://localhost).
 *   2. POST endpoint /receive that records the captured-data summary forwarded
 *      by the SaaS page. Tests inspect the recorded `received` array via
 *      GET /received (JSON debug surface — never used in production code).
 *
 * Endpoints:
 *   GET  /health           -> {status:"ok"}
 *   GET  /saas-page.html   -> serves fixtures/saas-page.html
 *   POST /receive          -> parses JSON body, pushes to `received`, returns 200
 *   GET  /received         -> JSON array of all received entries (debug)
 *
 * CLI usage: `npx ts-node fixtures/mock-backend.ts`
 * Library usage: `import { startMockBackend } from './mock-backend'`
 */
import { createServer, IncomingMessage, ServerResponse, Server } from 'http';
import { readFileSync } from 'fs';
import { join } from 'path';

export interface ReceivedEntry {
    success: boolean;
    bytesLen: number;
    sha256: string | null;
    receivedAt: string;
}

export interface MockBackendHandle {
    server: Server;
    received: ReceivedEntry[];
    port: number;
}

const FIXTURES_DIR = __dirname;
const SAAS_PAGE_PATH = join(FIXTURES_DIR, 'saas-page.html');

function logRequest(req: IncomingMessage): void {
    // Use stderr so it doesn't get mixed with Playwright's own output if invoked via webServer.
    process.stderr.write(`[mock-backend] ${req.method} ${req.url}\n`);
}

function sendJson(res: ServerResponse, status: number, body: unknown): void {
    const json = JSON.stringify(body);
    res.writeHead(status, {
        'Content-Type': 'application/json',
        'Content-Length': Buffer.byteLength(json),
    });
    res.end(json);
}

function sendText(res: ServerResponse, status: number, body: string, contentType: string): void {
    res.writeHead(status, {
        'Content-Type': contentType,
        'Content-Length': Buffer.byteLength(body),
    });
    res.end(body);
}

function readBody(req: IncomingMessage): Promise<string> {
    return new Promise((resolve, reject) => {
        let body = '';
        req.on('data', (chunk: Buffer | string) => {
            body += chunk.toString('utf8');
        });
        req.on('end', () => resolve(body));
        req.on('error', reject);
    });
}

/**
 * Start the mock backend on the given port.
 * Returns a handle with the http.Server and the live `received` array (mutable
 * from the calling test — push to it on POST /receive, read it from /received).
 */
export function startMockBackend(port: number = 8080): MockBackendHandle {
    const received: ReceivedEntry[] = [];

    const server = createServer(async (req, res) => {
        logRequest(req);

        try {
            const url = req.url ?? '/';
            const method = req.method ?? 'GET';

            if (method === 'GET' && url === '/health') {
                sendJson(res, 200, { status: 'ok' });
                return;
            }

            if (method === 'GET' && (url === '/saas-page.html' || url === '/')) {
                // Root path is a convenience alias for the SaaS page so a developer
                // hitting http://127.0.0.1:8080/ in a browser sees the test page.
                try {
                    const html = readFileSync(SAAS_PAGE_PATH, 'utf8');
                    sendText(res, 200, html, 'text/html; charset=utf-8');
                } catch (err) {
                    sendText(res, 500, `Failed to read SaaS page fixture: ${(err as Error).message}`, 'text/plain');
                }
                return;
            }

            if (method === 'POST' && url === '/receive') {
                const body = await readBody(req);
                let parsed: { success?: boolean; bytesLen?: number; sha256?: string | null };
                try {
                    parsed = JSON.parse(body);
                } catch (err) {
                    sendText(res, 400, `Invalid JSON: ${(err as Error).message}`, 'text/plain');
                    return;
                }
                const entry: ReceivedEntry = {
                    success: parsed.success ?? false,
                    bytesLen: parsed.bytesLen ?? 0,
                    sha256: parsed.sha256 ?? null,
                    receivedAt: new Date().toISOString(),
                };
                received.push(entry);
                sendText(res, 200, 'OK', 'text/plain');
                return;
            }

            if (method === 'GET' && url === '/received') {
                sendJson(res, 200, received);
                return;
            }

            if (method === 'DELETE' && url === '/received') {
                // WARN-04: reset endpoint so spec beforeEach can clear entries from
                // prior tests. Without this, a previous test's entries would satisfy
                // the `received.length >= 1` assertion in a later test even when the
                // current test's capture chain failed silently.
                const dropped = received.length;
                received.length = 0;
                sendJson(res, 200, { dropped });
                return;
            }

            if (method === 'GET' && url === '/received/last') {
                if (received.length === 0) {
                    sendJson(res, 404, { error: 'no entries yet' });
                } else {
                    sendJson(res, 200, received[received.length - 1]);
                }
                return;
            }

            sendText(res, 404, 'Not Found', 'text/plain');
        } catch (err) {
            process.stderr.write(`[mock-backend] ERROR: ${(err as Error).message}\n`);
            try {
                sendText(res, 500, `Internal error: ${(err as Error).message}`, 'text/plain');
            } catch {
                // Response already sent; nothing to do.
            }
        }
    });

    server.listen(port, '127.0.0.1', () => {
        process.stderr.write(`Mock backend listening on http://127.0.0.1:${port}\n`);
    });

    return { server, received, port };
}

// CLI invocation: when run via `npx ts-node fixtures/mock-backend.ts` (Playwright
// webServer, local dev), keep the process alive until SIGTERM/SIGINT.
if (require.main === module) {
    const port = Number(process.env.PORT ?? 8080);
    const handle = startMockBackend(port);

    const shutdown = (signal: NodeJS.Signals) => {
        process.stderr.write(`[mock-backend] received ${signal}, closing...\n`);
        handle.server.close(() => {
            process.stderr.write('[mock-backend] closed\n');
            process.exit(0);
        });
        // Force-exit if the server takes too long to drain in-flight requests.
        setTimeout(() => process.exit(0), 5000).unref();
    };

    process.on('SIGTERM', shutdown);
    process.on('SIGINT', shutdown);
}
