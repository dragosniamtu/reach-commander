import { HttpErrorResponse } from '@angular/common/http';
import { DestroyRef } from '@angular/core';
import {
  FileEntryDto,
  SourceDto,
  TextEncodingOperationDto,
  TextEncodingPreviewDto,
  TextEncodingPreviewRequestDto,
} from '../api/api.models';
import { CommanderApiTestBase } from '../../testing/commander-api-test-base';
import { PanelState } from './commander.models';
import {
  captureTextEncodingContext,
  TextEncodingContext,
} from './text-encoding.models';
import { TextEncodingScheduler, TextEncodingStore } from './text-encoding-store';

describe('text encoding context', () => {
  it('keeps mixed selected rows when at least one supported text file is selected', () => {
    const active = filesystemPanel('media', '/TV', {
      entries: [
        entry('episode.srt', '/TV/episode.srt'),
        entry('cover.xml', '/TV/cover.xml'),
        entry('Extras', '/TV/Extras', 'directory'),
      ],
      selectedItems: new Set(['/TV/episode.srt', '/TV/cover.xml', '/TV/Extras']),
    });

    const result = captureTextEncodingContext('left', active, sources());

    expect(result.error).toBeNull();
    expect(result.context).toEqual(expect.objectContaining({
      panelSide: 'left', sourceId: 'media', sourceName: 'Media', directoryPath: '/TV',
    }));
    expect(result.context?.entries.map((candidate) => candidate.relativePath)).toEqual([
      '/TV/Extras', '/TV/cover.xml', '/TV/episode.srt',
    ]);
  });

  it.each([
    {
      name: 'archive tab',
      panel: archivePanel(),
      sources: sources(),
      message: 'Text encoding is available only in filesystem folders.',
    },
    {
      name: 'unavailable source',
      panel: filesystemPanel('media', '/TV', { entries: [entry('episode.srt', '/TV/episode.srt')], cursorIndex: 0 }),
      sources: [source('media', 'Media', { isAvailable: false })],
      message: 'Media is unavailable.',
    },
    {
      name: 'read-only source',
      panel: filesystemPanel('media', '/TV', { entries: [entry('episode.srt', '/TV/episode.srt')], cursorIndex: 0 }),
      sources: [source('media', 'Media', { isReadOnly: true })],
      message: 'Media is read-only.',
    },
    {
      name: 'no recognized file',
      panel: filesystemPanel('media', '/TV', { entries: [entry('cover.xml', '/TV/cover.xml')], cursorIndex: 0 }),
      sources: sources(),
      message: 'Select at least one supported text file.',
    },
  ])('rejects $name before opening', ({ panel, sources: availableSources, message }) => {
    expect(captureTextEncodingContext('left', panel, availableSources)).toEqual({
      context: null,
      error: message,
    });
  });
});

describe('TextEncodingStore', () => {
  let api: FakeTextEncodingApi;
  let scheduler: FakeScheduler;
  let store: TextEncodingStore;
  let destroy: () => void;

  beforeEach(() => {
    api = new FakeTextEncodingApi();
    scheduler = new FakeScheduler();
    const callbacks: Array<() => void> = [];
    destroy = () => callbacks.splice(0).forEach((callback) => callback());
    store = new TextEncodingStore(
      api,
      scheduler,
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

  it('opens with Auto to UTF-8 and enters review after preview', async () => {
    const opening = store.open(context());
    expect(api.previewRequests[0]).toEqual(expect.objectContaining({
      sourceEncoding: 'auto', outputEncoding: 'utf8', filePaths: ['/TV/episode.srt'],
    }));
    api.resolvePreview(preview());

    await opening;

    expect(store.state().phase).toBe('review');
    expect(store.canExecute()).toBe(true);
  });

  it('debounces setting changes for 250 ms and ignores stale preview responses', async () => {
    const opening = store.open(context());
    store.setSourceEncoding('windows1250');
    store.setOutputEncoding('utf8Bom');

    expect(scheduler.delays).toEqual([250]);
    api.resolvePreview(preview({ planId: 'stale-plan' }));
    await opening;
    expect(store.state().preview).toBeNull();

    const refresh = scheduler.runNext();
    expect(api.previewRequests[1]).toEqual(expect.objectContaining({
      sourceEncoding: 'windows1250', outputEncoding: 'utf8Bom',
    }));
    api.resolvePreview(preview({ planId: 'current-plan' }));
    await refresh;
    expect(store.state().preview?.planId).toBe('current-plan');
  });

  it('executes, polls every 500 ms, and invokes completion once', async () => {
    await openReview(store, api);
    const completed = vi.fn();
    store.setCompletionHandler(completed);
    const execution = store.execute();
    api.resolveExecute(operation({ state: 'running' }));
    await execution;

    expect(store.state().phase).toBe('running');
    expect(scheduler.delays).toEqual([500]);
    const polling = scheduler.runNext();
    api.resolveStatus(operation({ state: 'completed', completedFiles: 1, percent: 100, canCancel: false }));
    await polling;

    expect(store.state().phase).toBe('completed');
    expect(completed).toHaveBeenCalledOnce();
  });

  it('cancels a running operation and ignores an overlapping stale poll', async () => {
    await openRunning(store, api);
    const polling = scheduler.runNext();
    const cancelling = store.cancel();
    expect(store.state().phase).toBe('cancelling');
    api.resolveCancel(operation({ state: 'cancelled', canCancel: false }));
    await cancelling;
    api.resolveStatus(operation({ state: 'running' }));
    await polling;

    expect(store.state().phase).toBe('cancelled');
    expect(scheduler.pending).toBe(0);
  });

  it('maps safe HTTP errors and blocks execution of an expired plan', async () => {
    const opening = store.open(context());
    api.rejectPreview(problem('text_encoding_invalid_request', 'Choose valid files.'));
    await opening;
    expect(store.state().error).toEqual({
      code: 'text_encoding_invalid_request', detail: 'Choose valid files.',
    });

    const retry = store.reviewAgain();
    api.resolvePreview(preview({ expiresAt: '2000-01-01T00:00:00Z' }));
    await retry;
    await store.execute();
    expect(api.executeRequests).toEqual([]);
    expect(store.state().error?.code).toBe('text_encoding_plan_expired');
  });

  it('cancels every scheduled callback on close and destruction', async () => {
    await openRunning(store, api);
    store.close();
    expect(scheduler.pending).toBe(0);

    const opening = store.open(context());
    store.setOutputEncoding('utf8Bom');
    api.resolvePreview(preview());
    await opening;
    expect(scheduler.pending).toBe(1);
    destroy();
    expect(scheduler.pending).toBe(0);
  });
});

async function openReview(store: TextEncodingStore, api: FakeTextEncodingApi): Promise<void> {
  const opening = store.open(context());
  api.resolvePreview(preview());
  await opening;
}

async function openRunning(store: TextEncodingStore, api: FakeTextEncodingApi): Promise<void> {
  await openReview(store, api);
  const execution = store.execute();
  api.resolveExecute(operation({ state: 'running' }));
  await execution;
}

function context(): TextEncodingContext {
  return {
    panelSide: 'left', sourceId: 'media', sourceName: 'Media', directoryPath: '/TV',
    entries: [entry('episode.srt', '/TV/episode.srt')],
  };
}

function preview(overrides: Partial<TextEncodingPreviewDto> = {}): TextEncodingPreviewDto {
  return {
    planId: 'plan-id', expiresAt: '2099-09-02T10:00:00Z', rows: [],
    readyCount: 1, warningCount: 0, invalidCount: 0, canExecute: true, ...overrides,
  };
}

function operation(overrides: Partial<TextEncodingOperationDto> = {}): TextEncodingOperationDto {
  return {
    operationId: 'operation-id', state: 'queued', completedFiles: 0, totalFiles: 1,
    percent: 0, currentFileName: null, canCancel: true,
    rows: [{ filePath: '/TV/episode.srt', backupPath: null, result: 'pending', code: null, detail: null }],
    errorCode: null, errorDetail: null, ...overrides,
  };
}

function problem(code: string, detail: string): HttpErrorResponse {
  return new HttpErrorResponse({ status: 422, error: { code, detail } });
}

function sources(): readonly SourceDto[] { return [source('media', 'Media')]; }

function source(id: string, name: string, overrides: Partial<SourceDto> = {}): SourceDto {
  return {
    id, name, isAvailable: true, isReadOnly: false, totalBytes: 100, usedBytes: 10,
    freeBytes: 90, defaultLeft: true, defaultRight: false, ...overrides,
  };
}

function filesystemPanel(sourceId: string, path: string, overrides: Partial<PanelState> = {}): PanelState {
  return panel({
    tabs: [{ id: 'active', label: path, location: { kind: 'filesystem', sourceId, path } }],
    activeTabId: 'active', ...overrides,
  });
}

function archivePanel(): PanelState {
  return panel({
    tabs: [{ id: 'active', label: 'archive', location: { kind: 'archive', sourceId: 'media', archivePath: '/one.zip', internalPath: '/' } }],
    activeTabId: 'active', entries: [entry('episode.srt', '/episode.srt')], cursorIndex: 0,
  });
}

function panel(overrides: Partial<PanelState> = {}): PanelState {
  return {
    tabs: [], activeTabId: '', cursorIndex: -1, selectedItems: new Set(), selectionAnchor: null,
    sortColumn: 'name', sortDirection: 'ascending', filter: '', entries: [], loading: false,
    errorCode: null, errorDetail: null, archiveMetadata: null, requestToken: 0, ...overrides,
  };
}

function entry(
  name: string,
  relativePath: string,
  type: FileEntryDto['type'] = 'file',
): FileEntryDto {
  return {
    name, relativePath, type, size: 1, modifiedAt: null, extension: null, isReadOnly: false,
    isSymbolicLink: false, attributes: '', archiveFormatHint: null, archiveRole: null,
  };
}

class FakeScheduler implements TextEncodingScheduler {
  private callbacks: Array<{ callback: () => Promise<void> | void; delay: number }> = [];
  get pending(): number { return this.callbacks.length; }
  get delays(): number[] { return this.callbacks.map((item) => item.delay); }
  schedule(callback: () => Promise<void> | void, delay: number): unknown {
    const item = { callback, delay }; this.callbacks.push(item); return item;
  }
  cancel(handle: unknown): void { this.callbacks = this.callbacks.filter((item) => item !== handle); }
  async runNext(): Promise<void> { await this.callbacks.shift()?.callback(); }
}

class FakeTextEncodingApi extends CommanderApiTestBase {
  previewRequests: TextEncodingPreviewRequestDto[] = [];
  executeRequests: string[] = [];
  statusRequests: string[] = [];
  cancelRequests: string[] = [];
  private previewDeferred = deferred<TextEncodingPreviewDto>();
  private executeDeferred = deferred<TextEncodingOperationDto>();
  private statusDeferred = deferred<TextEncodingOperationDto>();
  private cancelDeferred = deferred<TextEncodingOperationDto>();

  override previewTextEncoding(request: TextEncodingPreviewRequestDto): Promise<TextEncodingPreviewDto> {
    this.previewRequests.push(request); return this.previewDeferred.promise;
  }
  override executeTextEncoding(planId: string): Promise<TextEncodingOperationDto> {
    this.executeRequests.push(planId); return this.executeDeferred.promise;
  }
  override getTextEncodingOperation(operationId: string): Promise<TextEncodingOperationDto> {
    this.statusRequests.push(operationId); return this.statusDeferred.promise;
  }
  override cancelTextEncodingOperation(operationId: string): Promise<TextEncodingOperationDto> {
    this.cancelRequests.push(operationId); return this.cancelDeferred.promise;
  }
  resolvePreview(value: TextEncodingPreviewDto): void { this.previewDeferred.resolve(value); this.previewDeferred = deferred(); }
  rejectPreview(error: unknown): void { this.previewDeferred.reject(error); this.previewDeferred = deferred(); }
  resolveExecute(value: TextEncodingOperationDto): void { this.executeDeferred.resolve(value); this.executeDeferred = deferred(); }
  resolveStatus(value: TextEncodingOperationDto): void { this.statusDeferred.resolve(value); this.statusDeferred = deferred(); }
  resolveCancel(value: TextEncodingOperationDto): void { this.cancelDeferred.resolve(value); this.cancelDeferred = deferred(); }

  override getSystemMetrics(): any { throw new Error('unused'); }
  override getSources(): any { throw new Error('unused'); }
  override listFiles(): any { throw new Error('unused'); }
  override listArchive(): any { throw new Error('unused'); }
  override getInfo(): any { throw new Error('unused'); }
  override getUploadLimits(): any { throw new Error('unused'); }
  override uploadFiles(): any { throw new Error('unused'); }
  override previewBatchRename(): any { throw new Error('unused'); }
  override executeBatchRename(): any { throw new Error('unused'); }
  override undoBatchRename(): any { throw new Error('unused'); }
  override previewArchiveExtraction(): any { throw new Error('unused'); }
  override executeArchiveExtraction(): any { throw new Error('unused'); }
  override getArchiveExtraction(): any { throw new Error('unused'); }
  override cancelArchiveExtraction(): any { throw new Error('unused'); }
}

function deferred<T>() {
  let resolve!: (value: T) => void;
  let reject!: (error: unknown) => void;
  const promise = new Promise<T>((success, failure) => { resolve = success; reject = failure; });
  return { promise, resolve, reject };
}
