import { describe, expect, it } from 'vitest';
import { brotliDecompressSync, gunzipSync } from 'node:zlib';
import { compressBody, pickProxyEncoding } from './proxy-compression';

const req = (acceptEncoding: string | null) => new Headers(acceptEncoding === null ? {} : { 'accept-encoding': acceptEncoding });
const json = (extra: Record<string, string> = {}) => new Headers({ 'content-type': 'application/json; charset=utf-8', ...extra });

describe('pickProxyEncoding', () => {
  it('prefers brotli, then gzip, then nothing', () => {
    expect(pickProxyEncoding(req('gzip, deflate, br, zstd'), 200, json())).toBe('br');
    expect(pickProxyEncoding(req('gzip, deflate'), 200, json())).toBe('gzip');
    expect(pickProxyEncoding(req('identity'), 200, json())).toBeNull();
    expect(pickProxyEncoding(req(null), 200, json())).toBeNull();
  });

  it('honours q=0', () => {
    expect(pickProxyEncoding(req('br;q=0, gzip'), 200, json())).toBe('gzip');
    expect(pickProxyEncoding(req('br;q=0, gzip;q=0'), 200, json())).toBeNull();
  });

  it('leaves media, event streams and byte ranges alone', () => {
    expect(pickProxyEncoding(req('br'), 200, new Headers({ 'content-type': 'audio/flac' }))).toBeNull();
    expect(pickProxyEncoding(req('br'), 200, new Headers({ 'content-type': 'text/event-stream' }))).toBeNull();
    expect(pickProxyEncoding(req('br'), 206, json())).toBeNull();
    expect(pickProxyEncoding(req('br'), 304, json())).toBeNull();
  });

  it('skips small, already-encoded and no-transform bodies', () => {
    expect(pickProxyEncoding(req('br'), 200, json({ 'content-length': '212' }))).toBeNull();
    expect(pickProxyEncoding(req('br'), 200, json({ 'content-length': '4096' }))).toBe('br');
    expect(pickProxyEncoding(req('br'), 200, json({ 'content-encoding': 'gzip' }))).toBeNull();
    expect(pickProxyEncoding(req('br'), 200, json({ 'cache-control': 'no-store, no-transform' }))).toBeNull();
  });
});

describe('compressBody', () => {
  const payload = JSON.stringify({ songs: Array.from({ length: 500 }, (_, id) => ({ id, title: `Track ${id}` })) });

  it.each(['br', 'gzip'] as const)('round-trips as %s', async (encoding) => {
    const compressed = Buffer.from(await new Response(compressBody(new Response(payload).body!, encoding)).arrayBuffer());
    expect(compressed.byteLength).toBeLessThan(payload.length / 2);
    const decoded = encoding === 'br' ? brotliDecompressSync(compressed) : gunzipSync(compressed);
    expect(decoded.toString()).toBe(payload);
  });
});
