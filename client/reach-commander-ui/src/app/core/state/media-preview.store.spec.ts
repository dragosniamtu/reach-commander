import { HttpErrorResponse } from '@angular/common/http';
import { DestroyRef } from '@angular/core';
import {
  CreateMediaPreviewRequestDto,
  FileEntryDto,
  MediaPreviewDto,
  SubtitleRepairStatusDto,
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

  it('keeps polling while a preview moves from queued to transcoding to ready', async () => {
    const opening = store.open(context(), null);
    api.resolveCreate(ready({ phase: 'queued', playbackMode: 'hls' }));
    await opening;

    expect(store.state().phase).toBe('queued');
    expect(scheduler.pending).toBe(1);

    const queuedPoll = scheduler.runNext();
    api.resolveStatus(ready({ phase: 'transcoding', playbackMode: 'hls' }));
    await queuedPoll;
    expect(store.state().phase).toBe('transcoding');
    expect(scheduler.pending).toBe(1);

    const transcodePoll = scheduler.runNext();
    api.resolveStatus(ready({ playbackMode: 'hls' }));
    await transcodePoll;
    expect(store.state().phase).toBe('ready');
    expect(scheduler.pending).toBe(0);
  });

  it('keeps the heartbeat active while a playable HLS transcode is still running', async () => {
    const opening = store.open(context(), null);
    api.resolveCreate(ready({
      phase: 'ready',
      playbackMode: 'hls',
      transcodeActive: true,
    }));
    await opening;

    expect(store.state().phase).toBe('ready');
    expect(store.mediaUrl()).toContain('/hls/index.m3u8');
    expect(scheduler.pending).toBe(1);

    const heartbeat = scheduler.runNext();
    api.resolveStatus(ready({ playbackMode: 'hls', transcodeActive: false }));
    await heartbeat;

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

  it('allows a zero-offset UTF-8 conversion after encoding review', async () => {
    const opening = store.open(context(), null);
    const subtitleEncoding = {
      sourceEncoding: 'windows1251', requiresReview: false, conversionNeeded: true,
      previewText: 'Привет',
    };
    api.resolveCreate({ ...ready(), subtitleEncoding } as MediaPreviewDto);
    await opening;

    expect(store.canPlanSave()).toBe(true);
  });

  it('blocks save until the user reviews the source text', async () => {
    const opening = store.open(context(), null);
    const subtitleEncoding = {
      sourceEncoding: 'windows1251', requiresReview: true, conversionNeeded: true,
      previewText: 'Привет',
    };
    api.resolveCreate({ ...ready(), subtitleEncoding } as MediaPreviewDto);
    await opening;
    store.setOffset(500);

    expect(store.canPlanSave()).toBe(false);
  });

  it('previews and saves the timing estimate with a fine-tuned offset', async () => {
    await openReady(store, api);
    await store.startRepair();
    await scheduler.runNext();
    expect(store.adjustedCues()[0]?.startMilliseconds).toBe(1250);
    store.setOffset(300);
    expect(store.adjustedCues()[0]?.startMilliseconds).toBe(1350);
    const planning = store.planSave();
    expect(api.savePlanRequests).toEqual([{
      sessionId: 'session-id', offsetMilliseconds: 300, timeScale: 1.05,
    }]);
    api.resolvePlan(savePlan());
    await planning;
  });

  it('rejects a completion racing with cancellation and subtitle reselection', async () => {
    await openReady(store, api);
    let resolvePoll!: (status: SubtitleRepairStatusDto) => void;
    api.repairPoll = new Promise((resolve) => { resolvePoll = resolve; });
    await store.startRepair();
    expect(store.canPlanSave()).toBe(false);
    store.setOffset(500);
    expect(store.state().offsetMilliseconds).toBe(0);
    const polling = scheduler.runNext();
    await store.cancelRepair();
    await store.selectSubtitle('/Movies/Alternate.srt');
    resolvePoll({ state: 'completed', stage: 'Ready', percent: 100, offsetMilliseconds: 600, timeScale: 1.02 });
    await polling;
    expect(api.cancelRequests).toEqual(['session-id']);
    expect(store.state().session?.subtitlePath).toBe('/Movies/Alternate.srt');
    expect(store.adjustedCues()[0]?.startMilliseconds).toBe(1000);
  });

  it('stops retrying failed repair status and lets the user cancel or edit manually', async () => {
    await openReady(store, api);
    api.repairPollError = new HttpErrorResponse({ status: 503 });
    await store.startRepair();
    for (let attempt = 0; attempt < 5; attempt++) await scheduler.runNext();

    expect(scheduler.pending).toBe(0);
    expect(store.repairActive()).toBe(false);
    store.setOffset(250);
    expect(store.adjustedCues()[0]?.startMilliseconds).toBe(1250);
    await store.cancelRepair();
    expect(api.cancelRequests).toEqual(['session-id']);
  });

  it('keeps manual timing usable when repair cancellation fails and ignores the outstanding poll', async () => {
    await openReady(store, api);
    const status = deferred<SubtitleRepairStatusDto>();
    api.repairPoll = status.promise;
    api.cancellationError = new HttpErrorResponse({ status: 503 });
    await store.startRepair();
    const polling = scheduler.runNext();
    await store.cancelRepair();

    expect(scheduler.pending).toBe(0);
    expect(store.repairActive()).toBe(false);
    store.setOffset(400);
    status.resolve({ state: 'completed', stage: 'Ready', percent: 100, offsetMilliseconds: 900, timeScale: 1.05 });
    await polling;
    expect(store.adjustedCues()[0]?.startMilliseconds).toBe(1400);
    api.cancellationError = null;
    await store.cancelRepair();
    expect(api.cancelRequests).toEqual(['session-id', 'session-id']);
  });

  it('hides previous cues while a subtitle selection is pending or failed', async () => {
    await openReady(store, api);
    store.setVideoTime(1250);
    expect(store.activeCue()?.text).toBe('Hello');

    const next = deferred<MediaPreviewDto>();
    api.selectedSubtitle = next.promise;
    const selecting = store.selectSubtitle('/Movies/Alternate.srt');
    expect(store.activeCue()).toBeNull();
    next.reject(new HttpErrorResponse({ status: 422, error: { code: 'subtitle_encoding_unsupported' } }));
    await selecting;
    expect(store.activeCue()).toBeNull();

    api.selectedSubtitle = Promise.resolve(ready({
      subtitlePath: '/Movies/Alternate.srt',
      cues: [{ index: 0, startMilliseconds: 1000, endMilliseconds: 2000, text: 'Привет' }],
    }));
    await store.selectSubtitle('/Movies/Alternate.srt', 'windows1251');
    expect(store.activeCue()?.text).toBe('Привет');
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
    await Promise.resolve();
    api.resolveStatus(ready());
    await executing;
    expect(store.state().phase).toBe('saved');
    expect(store.state().savePlan).toBeNull();
    expect(store.state().saveResult).toEqual(saveResult());
  });

  it('rebases timing to the saved cues before a second edit in the same session', async () => {
    await openReady(store, api);
    await store.startRepair();
    await scheduler.runNext();
    const firstPlan = store.planSave();
    api.resolvePlan(savePlan());
    await firstPlan;

    const executing = store.executeSave();
    api.resolveExecute(saveResult());
    await Promise.resolve();
    expect(api.statusRequests).toEqual(['session-id']);
    const firstBackup = store.state().saveResult?.backupPath;
    store.setOffset(400);
    expect(store.canPlanSave()).toBe(false);
    expect(store.state().offsetMilliseconds).toBe(200);
    api.resolveStatus(ready({
      subtitleEncoding: { sourceEncoding: 'utf8', requiresReview: false, conversionNeeded: false, previewText: 'Hello' },
      cues: [{ index: 0, startMilliseconds: 1250, endMilliseconds: 2300, text: 'Hello' }],
    }));
    await executing;

    expect(store.state().saveResult?.backupPath).toBe(firstBackup);
    expect(store.state().offsetMilliseconds).toBe(0);
    expect(store.state().timeScale).toBe(1);
    expect(store.adjustedCues()[0]?.startMilliseconds).toBe(1250);
    store.setOffset(100);
    expect(store.adjustedCues()[0]?.startMilliseconds).toBe(1350);
    const secondPlan = store.planSave();
    expect(api.savePlanRequests.at(-1)).toEqual({ sessionId: 'session-id', offsetMilliseconds: 100 });
    api.resolvePlan(savePlan());
    await secondPlan;
  });

  it('keeps a successful backup visible and blocks edits until a failed refresh is retried', async () => {
    await openReady(store, api);
    store.setOffset(500);
    const planning = store.planSave();
    api.resolvePlan(savePlan());
    await planning;
    const executing = store.executeSave();
    api.resolveExecute(saveResult());
    await Promise.resolve();
    api.rejectStatus(new HttpErrorResponse({ status: 503 }));
    await executing;

    expect(store.state().phase).toBe('savedRefreshFailed');
    expect(store.state().saveResult?.backupPath).toBe('/Movies/movie_original.srt');
    expect(store.adjustedCues()).toEqual([]);
    store.setOffset(100);
    expect(store.canPlanSave()).toBe(false);

    const refreshing = store.refreshSavedSubtitle();
    api.resolveStatus(ready({
      cues: [{ index: 0, startMilliseconds: 1500, endMilliseconds: 2500, text: 'Hello' }],
    }));
    await refreshing;
    expect(store.state().phase).toBe('saved');
    expect(store.adjustedCues()[0]?.startMilliseconds).toBe(1500);
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

  it('closes a session that finishes opening after the dialog was dismissed', async () => {
    const opening = store.open(context(), null);

    await store.close();
    api.resolveCreate(ready({ phase: 'queued', playbackMode: 'hls' }));
    await opening;
    await Promise.resolve();

    expect(store.state().phase).toBe('closed');
    expect(api.closeRequests).toEqual(['session-id']);
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
    failureCode: null, failureDetail: null, transcodeActive: false, ...overrides,
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
  savePlanRequests: Array<{ sessionId: string; offsetMilliseconds: number; timeScale?: number }> = [];
  cancelRequests: string[] = [];
  cancellationError: unknown = null;
  repairPollError: unknown = null;
  selectedSubtitle: Promise<MediaPreviewDto> | null = null;
  repairPoll = Promise.resolve<SubtitleRepairStatusDto>({
    state: 'completed', stage: 'Timing correction ready', percent: 100, offsetMilliseconds: 200, timeScale: 1.05,
  });
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
  override getSubtitleRepairCapabilities() {
    return Promise.resolve({ enabled: true, available: true });
  }
  override startSubtitleRepair(_sessionId: string): Promise<SubtitleRepairStatusDto> {
    return Promise.resolve({ state: 'running', stage: 'Analyzing audio', percent: 20 });
  }
  override getSubtitleRepair(_sessionId: string): Promise<SubtitleRepairStatusDto> {
    return this.repairPollError ? Promise.reject(this.repairPollError) : this.repairPoll;
  }
  override cancelSubtitleRepair(sessionId: string): Promise<SubtitleRepairStatusDto> {
    this.cancelRequests.push(sessionId);
    if (this.cancellationError) return Promise.reject(this.cancellationError);
    return Promise.resolve({ state: 'cancelled', stage: 'Cancelled', percent: 100 });
  }
  override selectMediaPreviewSubtitle(_sessionId: string, subtitlePath: string): Promise<MediaPreviewDto> {
    return this.selectedSubtitle ?? Promise.resolve(ready({ subtitlePath }));
  }
  override requestMediaPreviewFallback(_sessionId: string): Promise<MediaPreviewDto> {
    return this.fallbacks.nextPromise();
  }
  override planMediaPreviewSubtitleSave(
    sessionId: string,
    offsetMilliseconds: number,
    timeScale?: number,
  ): Promise<SubtitleSavePlanDto> {
    this.savePlanRequests.push({
      sessionId, offsetMilliseconds, ...(timeScale === undefined ? {} : { timeScale }),
    });
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
  rejectStatus(error: unknown): void { this.statuses.rejectNext(error); }
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
