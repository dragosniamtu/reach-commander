import { HttpErrorResponse } from '@angular/common/http';
import { DestroyRef } from '@angular/core';
import {
  CreateMediaPreviewRequestDto,
  FileEntryDto,
  MediaPreviewDto,
  SubtitleSavePlanDto,
  SubtitleSaveResultDto,
} from '../api/api.models';
import { ProtectedStateResetService } from '../auth/protected-state-reset.service';
import { CommanderApiTestBase } from '../../testing/commander-api-test-base';
import { MediaPreviewScheduler, MediaPreviewStore } from './media-preview.store';
import { MediaPreviewContext } from './media-preview.models';

describe('MediaPreviewStore', () => {
  let api: FakeMediaPreviewApi;
  let scheduler: FakeScheduler;
  let protectedState: ProtectedStateResetService;
  let store: MediaPreviewStore;
  let destroy: () => void;

  beforeEach(() => {
    api = new FakeMediaPreviewApi();
    scheduler = new FakeScheduler();
    protectedState = new ProtectedStateResetService();
    const callbacks: Array<() => void> = [];
    destroy = () => callbacks.splice(0).forEach((callback) => callback());
    store = new MediaPreviewStore(
      api,
      scheduler,
      protectedState,
      {
        destroyed: false,
        onDestroy: (callback: () => void) => {
          callbacks.push(callback);
          return () => undefined;
        },
      } as unknown as DestroyRef,
    );
  });

  afterEach(() => destroy());

  it('ignores a stale open response after another video is opened', async () => {
    const first = store.open(context('/Movies/first.mp4'), null);
    const second = store.open(context('/Movies/second.mp4'), null);
    api.resolveCreate(ready({ videoPath: '/Movies/first.mp4', videoName: 'first.mp4' }));
    await first;
    expect(store.state().context?.videoPath).toBe('/Movies/second.mp4');

    api.resolveCreate(ready({ videoPath: '/Movies/second.mp4', videoName: 'second.mp4' }));
    await second;
    expect(store.state().session?.videoName).toBe('second.mp4');
  });

  it('lists only same-directory SRT files as subtitle candidates', async () => {
    api.fileEntries = [
      fileEntry('Zulu.SRT'),
      fileEntry('notes.txt'),
      fileEntry('Alpha.srt'),
      fileEntry('Linked.srt', { isSymbolicLink: true }),
      fileEntry('Folder.srt', { type: 'directory' }),
    ];

    await openReady(store, api);

    expect(api.fileListRequests).toEqual([{ sourceId: 'media', path: '/Movies' }]);
    expect(store.state().subtitleCandidates).toEqual([
      { name: 'Alpha.srt', path: '/Movies/Alpha.srt' },
      { name: 'Zulu.SRT', path: '/Movies/Zulu.SRT' },
    ]);
  });

  it('keeps the preview usable when subtitle discovery fails', async () => {
    api.fileListError = new Error('listing unavailable');

    await openReady(store, api);

    expect(store.state().phase).toBe('ready');
    expect(store.state().subtitleCandidates).toEqual([]);
  });

  it('polls a transcoding preview one request at a time until it is ready', async () => {
    const opening = store.open(context(), null);
    api.resolveCreate(ready({ phase: 'transcoding', playbackMode: 'hls' }));
    await opening;
    expect(store.state().phase).toBe('transcoding');
    expect(scheduler.pending).toBe(1);

    const polling = scheduler.runNext();
    expect(api.statusRequests).toEqual(['session-id']);
    api.resolveStatus(ready({ playbackMode: 'hls' }));
    await polling;
    expect(store.state().phase).toBe('ready');
    expect(scheduler.pending).toBe(0);
  });

  it('selects cues on adjusted clipped boundaries', async () => {
    await openReady(store, api);
    store.setOffset(-1_500);
    store.setVideoTime(0);
    expect(store.activeCue()?.text).toBe('Hello');
    expect(store.adjustedCues()[0]).toEqual(expect.objectContaining({
      startMilliseconds: 0,
      endMilliseconds: 500,
    }));
    store.setVideoTime(500);
    expect(store.activeCue()).toBeNull();
  });

  it('disables save for zero offset and read-only sources', async () => {
    await openReady(store, api);
    expect(store.canPlanSave()).toBe(false);
    store.setOffset(500);
    expect(store.canPlanSave()).toBe(true);

    const reopening = store.open(context('/Movies/readonly.mp4', true), null);
    api.resolveCreate(ready({ sourceReadOnly: true }));
    await reopening;
    store.setOffset(500);
    expect(store.canPlanSave()).toBe(false);
  });

  it('sends only the opaque session and offset when planning and executing a save', async () => {
    await openReady(store, api);
    store.setOffset(1_400);
    const planning = store.planSave();
    expect(api.savePlanRequests).toEqual([
      { sessionId: 'session-id', offsetMilliseconds: 1_400 },
    ]);
    api.resolvePlan(savePlan());
    await planning;
    expect(store.state().phase).toBe('review');

    const executing = store.executeSave();
    expect(api.executeRequests).toEqual(['plan-id']);
    api.resolveExecute(saveResult());
    await executing;
    expect(store.state().phase).toBe('saved');
    expect(store.state().savePlan).toBeNull();
    expect(store.state().saveResult).toEqual(saveResult());
  });

  it('queues an explicit browser fallback and resumes polling', async () => {
    await openReady(store, api);
    const retry = store.retryWithFallback();
    api.resolveFallback(ready({ phase: 'transcoding', playbackMode: 'hls' }));
    await retry;
    expect(store.state().phase).toBe('transcoding');
    expect(scheduler.pending).toBe(1);
  });

  it('closes the server session, cancels polling, and restores opener focus', async () => {
    const opener = document.createElement('button');
    document.body.appendChild(opener);
    const opening = store.open(context(), opener);
    api.resolveCreate(ready({ phase: 'transcoding', playbackMode: 'hls' }));
    await opening;

    await store.close();
    await Promise.resolve();

    expect(api.closeRequests).toEqual(['session-id']);
    expect(scheduler.pending).toBe(0);
    expect(document.activeElement).toBe(opener);
    opener.remove();
  });

  it('clears protected state without applying late responses', async () => {
    const opening = store.open(context(), null);
    protectedState.reset();
    api.resolveCreate(ready());
    await opening;
    expect(store.state().phase).toBe('closed');
  });

  it('projects unknown problem details to a bounded safe client error', async () => {
    const opening = store.open(context(), null);
    api.rejectCreate(new HttpErrorResponse({
      status: 500,
      error: { code: 'unknown_server_error', detail: 'D:\\private\\movie.mp4' },
    }));
    await opening;

    expect(store.state().error).toEqual({
      code: 'media_preview_request_failed',
      detail: 'The media preview request could not be completed.',
    });
    expect(JSON.stringify(store.state().error)).not.toContain('private');
  });
});

function context(
  videoPath = '/Movies/movie.mp4',
  sourceReadOnly = false,
): MediaPreviewContext {
  return {
    sourceId: 'media',
    videoPath,
    videoName: videoPath.slice(videoPath.lastIndexOf('/') + 1),
    sourceReadOnly,
  };
}

function ready(overrides: Partial<MediaPreviewDto> = {}): MediaPreviewDto {
  return {
    sessionId: 'session-id', phase: 'ready', playbackMode: 'direct',
    videoName: 'movie.mp4', videoPath: '/Movies/movie.mp4', durationMilliseconds: 10_000,
    subtitlePath: '/Movies/movie.srt',
    cues: [{ index: 0, startMilliseconds: 1_000, endMilliseconds: 2_000, text: 'Hello' }],
    sourceReadOnly: false, expiresAt: '2026-09-01T10:20:00Z',
    failureCode: null, failureDetail: null, ...overrides,
  };
}

function savePlan(): SubtitleSavePlanDto {
  return {
    planId: 'plan-id', expiresAt: '2026-09-01T10:10:00Z',
    subtitlePath: '/Movies/movie.srt', backupPath: '/Movies/movie_original.srt',
    offsetMilliseconds: 1_400, canExecute: true,
  };
}

function saveResult(): SubtitleSaveResultDto {
  return {
    subtitlePath: '/Movies/movie.srt', backupPath: '/Movies/movie_original.srt',
    recoveryRequired: false,
  };
}

function fileEntry(
  name: string,
  overrides: Partial<FileEntryDto> = {},
): FileEntryDto {
  return {
    name,
    relativePath: `/Movies/${name}`,
    type: 'file',
    size: 100,
    modifiedAt: '2026-09-01T10:00:00Z',
    extension: name.slice(name.lastIndexOf('.')),
    isReadOnly: false,
    isSymbolicLink: false,
    attributes: '',
    archiveFormatHint: null,
    archiveRole: null,
    ...overrides,
  };
}

async function openReady(store: MediaPreviewStore, api: FakeMediaPreviewApi): Promise<void> {
  const opening = store.open(context(), null);
  api.resolveCreate(ready());
  await opening;
}

class FakeScheduler implements MediaPreviewScheduler {
  private callbacks: Array<() => Promise<void> | void> = [];
  get pending(): number { return this.callbacks.length; }
  schedule(callback: () => Promise<void> | void): unknown {
    this.callbacks.push(callback); return callback;
  }
  cancel(handle: unknown): void {
    this.callbacks = this.callbacks.filter((callback) => callback !== handle);
  }
  async runNext(): Promise<void> { await this.callbacks.shift()?.(); }
}

class FakeMediaPreviewApi extends CommanderApiTestBase {
  fileEntries: readonly FileEntryDto[] = [];
  fileListError: unknown | null = null;
  fileListRequests: Array<{ sourceId: string; path: string }> = [];
  statusRequests: string[] = [];
  savePlanRequests: Array<{ sessionId: string; offsetMilliseconds: number }> = [];
  executeRequests: string[] = [];
  closeRequests: string[] = [];
  private creates = deferredQueue<MediaPreviewDto>();
  private statuses = deferredQueue<MediaPreviewDto>();
  private fallbacks = deferredQueue<MediaPreviewDto>();
  private plans = deferredQueue<SubtitleSavePlanDto>();
  private executions = deferredQueue<SubtitleSaveResultDto>();

  override createMediaPreview(_request: CreateMediaPreviewRequestDto): Promise<MediaPreviewDto> {
    return this.creates.nextPromise();
  }
  override listFiles(sourceId: string, path: string): Promise<readonly FileEntryDto[]> {
    this.fileListRequests.push({ sourceId, path });
    return this.fileListError === null
      ? Promise.resolve(this.fileEntries)
      : Promise.reject(this.fileListError);
  }
  override getMediaPreview(sessionId: string): Promise<MediaPreviewDto> {
    this.statusRequests.push(sessionId); return this.statuses.nextPromise();
  }
  override requestMediaPreviewFallback(_sessionId: string): Promise<MediaPreviewDto> {
    return this.fallbacks.nextPromise();
  }
  override planMediaPreviewSubtitleSave(
    sessionId: string,
    offsetMilliseconds: number,
  ): Promise<SubtitleSavePlanDto> {
    this.savePlanRequests.push({ sessionId, offsetMilliseconds });
    return this.plans.nextPromise();
  }
  override executeMediaPreviewSubtitleSave(planId: string): Promise<SubtitleSaveResultDto> {
    this.executeRequests.push(planId); return this.executions.nextPromise();
  }
  override closeMediaPreview(sessionId: string): Promise<void> {
    this.closeRequests.push(sessionId); return Promise.resolve();
  }
  resolveCreate(value: MediaPreviewDto): void { this.creates.resolveNext(value); }
  rejectCreate(error: unknown): void { this.creates.rejectNext(error); }
  resolveStatus(value: MediaPreviewDto): void { this.statuses.resolveNext(value); }
  resolveFallback(value: MediaPreviewDto): void { this.fallbacks.resolveNext(value); }
  resolvePlan(value: SubtitleSavePlanDto): void { this.plans.resolveNext(value); }
  resolveExecute(value: SubtitleSaveResultDto): void { this.executions.resolveNext(value); }

  getSystemMetrics(): any { throw new Error('unused'); }
  getSources(): any { throw new Error('unused'); }
  listArchive(): any { throw new Error('unused'); }
  getInfo(): any { throw new Error('unused'); }
  getUploadLimits(): any { throw new Error('unused'); }
  uploadFiles(): any { throw new Error('unused'); }
  previewBatchRename(): any { throw new Error('unused'); }
  executeBatchRename(): any { throw new Error('unused'); }
  undoBatchRename(): any { throw new Error('unused'); }
  previewArchiveExtraction(): any { throw new Error('unused'); }
  executeArchiveExtraction(): any { throw new Error('unused'); }
  getArchiveExtraction(): any { throw new Error('unused'); }
  cancelArchiveExtraction(): any { throw new Error('unused'); }
}

function deferredQueue<T>() {
  const pending: Array<ReturnType<typeof deferred<T>>> = [];
  return {
    nextPromise(): Promise<T> { const item = deferred<T>(); pending.push(item); return item.promise; },
    resolveNext(value: T): void { pending.shift()?.resolve(value); },
    rejectNext(reason: unknown): void { pending.shift()?.reject(reason); },
  };
}

function deferred<T>() {
  let resolve!: (value: T) => void;
  let reject!: (reason: unknown) => void;
  const promise = new Promise<T>((accept, decline) => { resolve = accept; reject = decline; });
  return { promise, resolve, reject };
}
