import { type Page, type Route } from '@playwright/test';

export type MediaSaveFailure = 'stale' | 'rolledBack' | 'recoveryRequired';

export interface MediaPreviewFixtureOptions {
  readonly failDirectContentOnce?: boolean;
  readonly createFailureCode?: 'media_probe_failed' | 'media_session_stale';
  readonly saveFailure?: MediaSaveFailure;
}

export interface MediaPreviewRequestLog {
  readonly method: string;
  readonly path: string;
  readonly body: unknown;
}

export interface MediaPreviewFixture {
  readonly requests: readonly MediaPreviewRequestLog[];
  readonly subtitlePaths: readonly string[];
  readonly offsets: readonly number[];
  readonly executeCount: number;
  readonly fallbackRequests: number;
  readonly statusReads: number;
}

interface SessionState {
  readonly sessionId: string;
  readonly sourceId: string;
  readonly videoPath: string;
  readonly videoName: string;
  subtitlePath: string | null;
  phase: 'queued' | 'transcoding' | 'ready' | 'failed';
  playbackMode: 'direct' | 'hls';
  sourceReadOnly: boolean;
  failureCode: string | null;
  failureDetail: string | null;
  transcodeActive: boolean;
}

const sessionId = '55555555-5555-4555-8555-555555555555';
const planId = '66666666-6666-4666-8666-666666666666';
const expiresAt = '2026-09-01T12:20:00Z';
let cachedBrowserVideo: Uint8Array | null = null;

export async function routeMediaPreview(
  page: Page,
  options: MediaPreviewFixtureOptions = {},
): Promise<MediaPreviewFixture> {
  const mediaBytes = await oneSecondBrowserVideo(page);
  const requests: MediaPreviewRequestLog[] = [];
  const subtitlePaths: string[] = [];
  const offsets: number[] = [];
  let executeCount = 0;
  let fallbackRequests = 0;
  let statusReads = 0;
  let failDirectContent = options.failDirectContentOnce === true;
  let session: SessionState | null = null;

  const handler = async (route: Route) => {
    const request = route.request();
    const url = new URL(request.url());
    const path = url.pathname;
    const method = request.method();
    const body = request.postDataJSON() ?? null;
    requests.push({ method, path, body });

    if (path === '/api/media-previews' && method === 'POST') {
      if (options.createFailureCode) {
        await problem(route, 422, options.createFailureCode, 'The media preview could not be created.');
        return;
      }
      const create = body as { sourceId: string; videoPath: string };
      const name = create.videoPath.split('/').at(-1) ?? 'video.mp4';
      const fallback = name.endsWith('.mkv') || name.endsWith('.avi');
      session = {
        sessionId,
        sourceId: create.sourceId,
        videoPath: create.videoPath,
        videoName: name,
        subtitlePath: create.videoPath.replace(/\.[^.]+$/, '.srt'),
        phase: fallback ? 'queued' : 'ready',
        playbackMode: fallback ? 'hls' : 'direct',
        sourceReadOnly: create.sourceId === 'archive',
        failureCode: null,
        failureDetail: null,
        transcodeActive: fallback,
      };
      await json(route, fallback ? 202 : 200, response(session));
      return;
    }

    if (!session) {
      await problem(route, 404, 'media_session_not_found', 'The media preview session was not found.');
      return;
    }

    if (path === `/api/media-previews/${sessionId}` && method === 'GET') {
      statusReads += 1;
      if (session.phase === 'queued') {
        session.phase = 'transcoding';
      } else if (session.phase === 'transcoding') {
        session.phase = 'ready';
      } else if (session.phase === 'ready' && session.transcodeActive) {
        session.transcodeActive = false;
      }
      await json(route, 200, response(session));
      return;
    }

    if (path === `/api/media-previews/${sessionId}/content` && method === 'GET') {
      if (failDirectContent) {
        failDirectContent = false;
        await route.abort('failed');
        return;
      }
      await media(route, mediaBytes, 'video/webm');
      return;
    }

    if (path === `/api/media-previews/${sessionId}/fallback` && method === 'POST') {
      fallbackRequests += 1;
      session.phase = 'queued';
      session.playbackMode = 'hls';
      session.transcodeActive = true;
      await json(route, 202, response(session));
      return;
    }

    if (path === `/api/media-previews/${sessionId}/subtitle` && method === 'PUT') {
      const selection = body as { subtitlePath: string };
      subtitlePaths.push(selection.subtitlePath);
      session.subtitlePath = selection.subtitlePath;
      session.phase = 'ready';
      await json(route, 200, response(session));
      return;
    }

    if (path === `/api/media-previews/${sessionId}/subtitle-save-plans` && method === 'POST') {
      const save = body as { offsetMilliseconds: number };
      offsets.push(save.offsetMilliseconds);
      const subtitlePath = session.subtitlePath ?? '/Movies/Family Movie.srt';
      await json(route, 200, {
        planId,
        expiresAt,
        subtitlePath,
        backupPath: backupPath(subtitlePath),
        offsetMilliseconds: save.offsetMilliseconds,
        canExecute: !session.sourceReadOnly,
      });
      return;
    }

    if (path === `/api/media-previews/subtitle-save-plans/${planId}/execute` && method === 'POST') {
      executeCount += 1;
      if (options.saveFailure) {
        const failure = saveFailure(options.saveFailure);
        await problem(route, failure.status, failure.code, failure.detail);
        return;
      }
      const subtitlePath = session.subtitlePath ?? '/Movies/Family Movie.srt';
      await json(route, 200, {
        subtitlePath,
        backupPath: backupPath(subtitlePath),
        recoveryRequired: false,
      });
      return;
    }

    if (path === `/api/media-previews/${sessionId}` && method === 'DELETE') {
      await route.fulfill({ status: 204, body: '' });
      return;
    }

    if (path === `/api/media-previews/${sessionId}/hls/index.m3u8` && method === 'GET') {
      await route.fulfill({
        status: 200,
        contentType: 'application/vnd.apple.mpegurl',
        body: '#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:1\n#EXTINF:1.0,\nsegment00000.ts\n#EXT-X-ENDLIST\n',
      });
      return;
    }

    if (path === `/api/media-previews/${sessionId}/hls/segment00000.ts` && method === 'GET') {
      await media(route, mediaBytes, 'video/webm');
      return;
    }

    await problem(route, 404, 'hls_asset_invalid', 'The preview asset is unavailable.');
  };

  await page.route(/\/api\/media-previews(?:\/[^?]*)?(?:\?.*)?$/, handler);

  return {
    requests,
    subtitlePaths,
    offsets,
    get executeCount() { return executeCount; },
    get fallbackRequests() { return fallbackRequests; },
    get statusReads() { return statusReads; },
  };
}

function response(session: SessionState) {
  return {
    sessionId: session.sessionId,
    phase: session.phase,
    playbackMode: session.playbackMode,
    videoName: session.videoName,
    videoPath: session.videoPath,
    durationMilliseconds: 1_000,
    subtitlePath: session.subtitlePath,
    cues: session.subtitlePath
      ? [{ index: 0, startMilliseconds: 100, endMilliseconds: 900, text: 'Fixture cue' }]
      : [],
    sourceReadOnly: session.sourceReadOnly,
    expiresAt,
    failureCode: session.failureCode,
    failureDetail: session.failureDetail,
    transcodeActive: session.transcodeActive,
  };
}

function backupPath(subtitlePath: string): string {
  return subtitlePath.replace(/\.srt$/i, '_original.srt');
}

function saveFailure(kind: MediaSaveFailure) {
  switch (kind) {
    case 'stale':
      return { status: 409, code: 'subtitle_save_plan_stale', detail: 'The subtitle changed after review.' };
    case 'rolledBack':
      return { status: 500, code: 'subtitle_save_failed', detail: 'Saving failed and the original was restored.' };
    case 'recoveryRequired':
      return { status: 500, code: 'subtitle_recovery_required', detail: 'Manual subtitle recovery is required.' };
  }
}

async function json(route: Route, status: number, value: unknown): Promise<void> {
  await route.fulfill({ status, contentType: 'application/json', json: value });
}

async function problem(route: Route, status: number, code: string, detail: string): Promise<void> {
  await route.fulfill({
    status,
    contentType: 'application/problem+json',
    json: { type: 'about:blank', title: 'Media preview failed', status, code, detail },
  });
}

async function media(route: Route, bytes: Uint8Array, contentType: string): Promise<void> {
  const range = route.request().headers()['range'];
  const match = /^bytes=(\d+)-(\d*)$/.exec(range ?? '');
  if (!match) {
    await route.fulfill({
      status: 200,
      contentType,
      headers: { 'Accept-Ranges': 'bytes', 'Content-Length': String(bytes.length) },
      body: Buffer.from(bytes),
    });
    return;
  }
  const start = Number(match[1]);
  const requestedEnd = match[2] ? Number(match[2]) : bytes.length - 1;
  const end = Math.min(requestedEnd, bytes.length - 1);
  const body = bytes.slice(start, end + 1);
  await route.fulfill({
    status: 206,
    contentType,
    headers: {
      'Accept-Ranges': 'bytes',
      'Content-Range': `bytes ${start}-${end}/${bytes.length}`,
      'Content-Length': String(body.length),
    },
    body: Buffer.from(body),
  });
}

async function oneSecondBrowserVideo(page: Page): Promise<Uint8Array> {
  if (cachedBrowserVideo) {
    return cachedBrowserVideo;
  }
  const values = await page.evaluate(async () => {
    const canvas = document.createElement('canvas');
    canvas.width = 64;
    canvas.height = 36;
    const context = canvas.getContext('2d');
    if (!context || typeof canvas.captureStream !== 'function' || typeof MediaRecorder === 'undefined') {
      throw new Error('Chromium does not expose the deterministic media fixture APIs.');
    }
    const mimeType = MediaRecorder.isTypeSupported('video/webm;codecs=vp8')
      ? 'video/webm;codecs=vp8'
      : 'video/webm';
    const stream = canvas.captureStream(12);
    const chunks: Blob[] = [];
    const recorder = new MediaRecorder(stream, { mimeType, videoBitsPerSecond: 64_000 });
    recorder.ondataavailable = (event) => {
      if (event.data.size > 0) chunks.push(event.data);
    };
    const stopped = new Promise<void>((resolve) => { recorder.onstop = () => resolve(); });
    recorder.start(100);
    const started = performance.now();
    while (performance.now() - started < 1_000) {
      const elapsed = performance.now() - started;
      context.fillStyle = elapsed % 200 < 100 ? '#071018' : '#15e0c5';
      context.fillRect(0, 0, canvas.width, canvas.height);
      await new Promise((resolve) => setTimeout(resolve, 50));
    }
    recorder.stop();
    await stopped;
    stream.getTracks().forEach((track) => track.stop());
    const buffer = await new Blob(chunks, { type: mimeType }).arrayBuffer();
    return [...new Uint8Array(buffer)];
  });
  cachedBrowserVideo = Uint8Array.from(values);
  return cachedBrowserVideo;
}
