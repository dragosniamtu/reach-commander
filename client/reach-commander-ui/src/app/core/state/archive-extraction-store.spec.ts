import { DestroyRef } from '@angular/core';
import { HttpErrorResponse } from '@angular/common/http';
import {
  ArchiveExtractionOperationDto,
  ArchiveExtractionPreviewDto,
  FileEntryDto,
  SourceDto,
} from '../api/api.models';
import {
  ArchiveExtractionScheduler,
  ArchiveExtractionStore,
} from './archive-extraction-store';
import {
  captureArchiveExtractionContext,
  ArchiveExtractionContext,
} from './archive-extraction.models';
import { PanelState } from './commander.models';
import { CommanderApiTestBase } from '../../testing/commander-api-test-base';

describe('archive extraction context', () => {
  it('captures archive selection and the opposite destination once', () => {
    const left = panel({
      tabs: [{
        id: 'archive',
        label: 'photos.7z',
        location: {
          kind: 'archive',
          sourceId: 'downloads',
          archivePath: '/photos.7z',
          internalPath: '/Family',
        },
      }],
      activeTabId: 'archive',
      entries: [entry('2025', '/Family/2025', 'directory'), entry('notes.txt', '/Family/notes.txt')],
      selectedItems: new Set(['/Family/2025']),
      cursorIndex: 2,
      archiveMetadata: { format: 'sevenZip', volumeCount: 2 },
    });
    const right = filesystemPanel('media', '/Photos');

    const result = captureArchiveExtractionContext('left', left, right, sources());
    (right.tabs[0] as any).location = {
      kind: 'filesystem', sourceId: 'media', path: '/Changed',
    };

    expect(result.error).toBeNull();
    expect(result.context).toEqual(expect.objectContaining({
      sourcePanelSide: 'left',
      destinationPanelSide: 'right',
      sourceId: 'downloads',
      archivePath: '/photos.7z',
      internalDirectory: '/Family',
      entryPaths: ['/Family/2025'],
      extractAll: false,
      destinationSourceId: 'media',
      destinationPath: '/Photos',
    }));
  });

  it('uses the focused archive row when nothing is selected', () => {
    const left = panel({
      tabs: [{
        id: 'archive', label: 'photos.zip',
        location: { kind: 'archive', sourceId: 'downloads', archivePath: '/photos.zip', internalPath: '/' },
      }],
      activeTabId: 'archive',
      entries: [entry('one.txt', '/one.txt'), entry('two.txt', '/two.txt')],
      cursorIndex: 2,
      archiveMetadata: { format: 'zip', volumeCount: 1 },
    });

    expect(captureArchiveExtractionContext(
      'left', left, filesystemPanel('media', '/Extracted'), sources(),
    ).context?.entryPaths).toEqual(['/two.txt']);
  });

  it('uses whole-archive mode only for one primary or single filesystem archive', () => {
    const left = filesystemPanel('downloads', '/', {
      entries: [entry('photos.7z', '/photos.7z', 'file', { archiveFormatHint: 'sevenZip', archiveRole: 'primary' })],
      cursorIndex: 0,
    });

    const result = captureArchiveExtractionContext(
      'left', left, filesystemPanel('media', '/Extracted'), sources(),
    );

    expect(result.context).toEqual(expect.objectContaining({
      archivePath: '/photos.7z',
      internalDirectory: '/',
      entryPaths: [],
      extractAll: true,
      format: 'sevenZip',
    }));
  });

  it.each([
    {
      name: 'multiple archives',
      source: filesystemPanel('downloads', '/', {
        entries: [
          entry('one.zip', '/one.zip', 'file', { archiveFormatHint: 'zip', archiveRole: 'single' }),
          entry('two.zip', '/two.zip', 'file', { archiveFormatHint: 'zip', archiveRole: 'single' }),
        ],
        selectedItems: new Set(['/one.zip', '/two.zip']),
      }),
      destination: filesystemPanel('media', '/Extracted'),
      message: 'Select exactly one archive to extract.',
    },
    {
      name: 'secondary volume',
      source: filesystemPanel('downloads', '/', {
        entries: [entry('part02.rar', '/part02.rar', 'file', { archiveFormatHint: 'rar', archiveRole: 'secondary' })],
        cursorIndex: 0,
      }),
      destination: filesystemPanel('media', '/Extracted'),
      message: 'Open the primary archive volume before extracting.',
    },
    {
      name: 'ordinary file',
      source: filesystemPanel('downloads', '/', { entries: [entry('notes.txt', '/notes.txt')], cursorIndex: 0 }),
      destination: filesystemPanel('media', '/Extracted'),
      message: 'Select a supported archive to extract.',
    },
    {
      name: 'archive destination',
      source: filesystemPanel('downloads', '/', {
        entries: [entry('one.zip', '/one.zip', 'file', { archiveFormatHint: 'zip', archiveRole: 'single' })],
        cursorIndex: 0,
      }),
      destination: panel({
        tabs: [{ id: 'dest', label: 'inside', location: { kind: 'archive', sourceId: 'media', archivePath: '/other.zip', internalPath: '/' } }],
        activeTabId: 'dest',
      }),
      message: 'Choose a filesystem folder in the opposite panel.',
    },
  ])('rejects $name before preview', ({ source, destination, message }) => {
    const result = captureArchiveExtractionContext('left', source, destination, sources());
    expect(result.context).toBeNull();
    expect(result.error).toBe(message);
  });
});

describe('ArchiveExtractionStore', () => {
  let api: FakeArchiveExtractionApi;
  let scheduler: FakeScheduler;
  let store: ArchiveExtractionStore;
  let destroyed: () => void;

  beforeEach(() => {
    api = new FakeArchiveExtractionApi();
    scheduler = new FakeScheduler();
    const callbacks: Array<() => void> = [];
    destroyed = () => callbacks.splice(0).forEach((callback) => callback());
    store = new ArchiveExtractionStore(
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

  it('previews immutable context and enters review', async () => {
    const context = extractionContext();
    const opening = store.open(context);
    context.entryPaths.push('/later.txt');
    api.resolvePreview(preview());

    await opening;
    expect(store.state().phase).toBe('review');
    expect(store.state().context?.entryPaths).toEqual(['/Family/2025']);
    expect(api.previewRequests[0]).toEqual(expect.objectContaining({ destinationPath: '/Photos' }));
  });

  it('executes, polls one request at a time, and stops at completion', async () => {
    await openReview(store, api);
    const completed = vi.fn();
    store.setCompletionHandler(completed);
    const starting = store.execute();
    api.resolveExecute(operation({ state: 'extracting' }));
    await starting;

    expect(store.state().phase).toBe('running');
    expect(scheduler.pending).toBe(1);
    const poll = scheduler.runNext();
    expect(api.statusRequests).toEqual(['operation-id']);
    expect(scheduler.pending).toBe(0);
    api.resolveStatus(operation({ state: 'completed', completedFiles: 1, extractedBytes: 12, percent: 100, canCancel: false }));
    await poll;

    expect(store.state().phase).toBe('completed');
    expect(scheduler.pending).toBe(0);
    expect(completed).toHaveBeenCalledWith('left', 'right');
  });

  it('ignores an in-flight polling snapshot after cancellation completes', async () => {
    await openRunning(store, api);
    const poll = scheduler.runNext();
    const cancelling = store.cancel();

    api.resolveCancel(operation({ state: 'cancelled', canCancel: false }));
    await cancelling;
    api.resolveStatus(operation({ state: 'extracting' }));
    await poll;

    expect(store.state().phase).toBe('cancelled');
    expect(scheduler.pending).toBe(0);
  });

  it('refreshes completed panels once when cancel and poll responses overlap', async () => {
    await openRunning(store, api);
    const completed = vi.fn();
    store.setCompletionHandler(completed);
    const poll = scheduler.runNext();
    const cancelling = store.cancel();

    api.resolveStatus(operation({ state: 'completed', canCancel: false, percent: 100 }));
    await poll;
    api.resolveCancel(operation({ state: 'completed', canCancel: false, percent: 100 }));
    await cancelling;

    expect(store.state().phase).toBe('completed');
    expect(completed).toHaveBeenCalledOnce();
  });

  it('stops polling on close and destruction', async () => {
    await openRunning(store, api);
    store.close();
    expect(scheduler.pending).toBe(0);

    await openRunning(store, api);
    destroyed();
    expect(scheduler.pending).toBe(0);
  });

  it('keeps review context for capacity and stale execution errors', async () => {
    await openReview(store, api);
    const execution = store.execute();
    api.rejectExecute(problem('archive_capacity_reached', 'Busy.'));
    await execution;

    expect(store.state().phase).toBe('review');
    expect(store.state().context?.archivePath).toBe('/photos.7z');
    expect(store.state().error?.code).toBe('archive_capacity_reached');
    expect(store.canExecute()).toBe(true);

    const retry = store.execute();
    api.rejectExecute(problem('archive_plan_stale', 'The archive changed.'));
    await retry;
    expect(store.state().phase).toBe('review');
    expect(store.canExecute()).toBe(false);

    const review = store.reviewAgain();
    api.resolvePreview(preview());
    await review;
    expect(api.previewRequests).toHaveLength(2);
    expect(store.canExecute()).toBe(true);
  });

  it('stops polling when the operation resource is no longer available', async () => {
    await openRunning(store, api);
    const poll = scheduler.runNext();
    api.rejectStatus(problem('archive_plan_not_found', 'The operation is no longer available.'));
    await poll;

    expect(store.state().phase).toBe('failed');
    expect(scheduler.pending).toBe(0);
  });

  it('disables cancellation while finalizing and makes a cancellation request idempotent', async () => {
    await openRunning(store, api, operation({ state: 'finalizing', canCancel: false }));
    await store.cancel();
    expect(api.cancelRequests).toEqual([]);

    api.executeResult = operation({ state: 'extracting', canCancel: true });
    store.close();
    await openRunning(store, api);
    const first = store.cancel();
    const second = store.cancel();
    expect(api.cancelRequests).toEqual(['operation-id']);
    api.resolveCancel(operation({ state: 'cancelled', canCancel: false }));
    await Promise.all([first, second]);
    expect(store.state().phase).toBe('cancelled');
  });
});

async function openReview(store: ArchiveExtractionStore, api: FakeArchiveExtractionApi): Promise<void> {
  const opening = store.open(extractionContext());
  api.resolvePreview(preview());
  await opening;
}

async function openRunning(
  store: ArchiveExtractionStore,
  api: FakeArchiveExtractionApi,
  result = operation({ state: 'extracting' }),
): Promise<void> {
  await openReview(store, api);
  const executing = store.execute();
  api.resolveExecute(result);
  await executing;
}

function extractionContext(): ArchiveExtractionContext & { entryPaths: string[] } {
  return {
    sourcePanelSide: 'left', destinationPanelSide: 'right',
    sourceId: 'downloads', sourceName: 'Downloads', archivePath: '/photos.7z',
    internalDirectory: '/Family', entryPaths: ['/Family/2025'], extractAll: false,
    format: 'sevenZip', volumeCount: 1,
    destinationSourceId: 'media', destinationSourceName: 'Media', destinationPath: '/Photos',
  };
}

function preview(): ArchiveExtractionPreviewDto {
  return {
    planId: 'plan-id', expiresAt: '2026-08-20T08:10:00Z', format: 'sevenZip', volumeCount: 1,
    selectedRoots: ['2025'], fileCount: 1, directoryCount: 1, totalExtractedBytes: 12,
    destinationSourceId: 'media', destinationPath: '/Photos', conflicts: [], violations: [],
    canExecute: true,
  };
}

function operation(overrides: Partial<ArchiveExtractionOperationDto> = {}): ArchiveExtractionOperationDto {
  return {
    operationId: 'operation-id', state: 'extracting', completedFiles: 0, totalFiles: 1,
    extractedBytes: 0, totalBytes: 12, percent: 0, currentEntryName: 'photo.jpg', canCancel: true,
    compensationState: 'notRequired', recoveryNames: [], errorCode: null, errorDetail: null,
    ...overrides,
  };
}

function problem(code: string, detail: string): HttpErrorResponse {
  return new HttpErrorResponse({ status: 409, error: { code, detail } });
}

function sources(): readonly SourceDto[] {
  return [source('downloads', 'Downloads'), source('media', 'Media')];
}

function source(id: string, name: string, overrides: Partial<SourceDto> = {}): SourceDto {
  return {
    id, name, isAvailable: true, isReadOnly: false, totalBytes: 100, usedBytes: 10,
    freeBytes: 90, defaultLeft: id === 'downloads', defaultRight: id === 'media', ...overrides,
  };
}

function filesystemPanel(sourceId: string, path: string, overrides: Partial<PanelState> = {}): PanelState {
  return panel({
    tabs: [{ id: `${sourceId}-tab`, label: path, location: { kind: 'filesystem', sourceId, path } }],
    activeTabId: `${sourceId}-tab`, ...overrides,
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
  overrides: Partial<FileEntryDto> = {},
): FileEntryDto {
  return {
    name, relativePath, type, size: 1, modifiedAt: null, extension: null, isReadOnly: false,
    isSymbolicLink: false, attributes: '', archiveFormatHint: null, archiveRole: null, ...overrides,
  };
}

class FakeScheduler implements ArchiveExtractionScheduler {
  private callbacks: Array<() => Promise<void> | void> = [];
  get pending(): number { return this.callbacks.length; }
  schedule(callback: () => Promise<void> | void): unknown { this.callbacks.push(callback); return callback; }
  cancel(handle: unknown): void { this.callbacks = this.callbacks.filter((callback) => callback !== handle); }
  async runNext(): Promise<void> { await this.callbacks.shift()?.(); }
}

class FakeArchiveExtractionApi extends CommanderApiTestBase {
  previewRequests: any[] = [];
  statusRequests: string[] = [];
  cancelRequests: string[] = [];
  executeResult: ArchiveExtractionOperationDto | null = null;
  private previewDeferred = deferred<ArchiveExtractionPreviewDto>();
  private executeDeferred = deferred<ArchiveExtractionOperationDto>();
  private statusDeferred = deferred<ArchiveExtractionOperationDto>();
  private cancelDeferred = deferred<ArchiveExtractionOperationDto>();

  previewArchiveExtraction(request: any): Promise<ArchiveExtractionPreviewDto> {
    this.previewRequests.push(request); return this.previewDeferred.promise;
  }
  executeArchiveExtraction(): Promise<ArchiveExtractionOperationDto> {
    if (this.executeResult) return Promise.resolve(this.executeResult);
    return this.executeDeferred.promise;
  }
  getArchiveExtraction(operationId: string): Promise<ArchiveExtractionOperationDto> {
    this.statusRequests.push(operationId); return this.statusDeferred.promise;
  }
  cancelArchiveExtraction(operationId: string): Promise<ArchiveExtractionOperationDto> {
    this.cancelRequests.push(operationId); return this.cancelDeferred.promise;
  }
  resolvePreview(value: ArchiveExtractionPreviewDto): void { this.previewDeferred.resolve(value); this.previewDeferred = deferred(); }
  resolveExecute(value: ArchiveExtractionOperationDto): void { this.executeDeferred.resolve(value); this.executeDeferred = deferred(); }
  rejectExecute(error: unknown): void { this.executeDeferred.reject(error); this.executeDeferred = deferred(); }
  resolveStatus(value: ArchiveExtractionOperationDto): void { this.statusDeferred.resolve(value); this.statusDeferred = deferred(); }
  rejectStatus(error: unknown): void { this.statusDeferred.reject(error); this.statusDeferred = deferred(); }
  resolveCancel(value: ArchiveExtractionOperationDto): void { this.cancelDeferred.resolve(value); this.cancelDeferred = deferred(); }

  getSystemMetrics(): any { throw new Error('unused'); }
  getSources(): any { throw new Error('unused'); }
  listFiles(): any { throw new Error('unused'); }
  listArchive(): any { throw new Error('unused'); }
  getInfo(): any { throw new Error('unused'); }
  getUploadLimits(): any { throw new Error('unused'); }
  uploadFiles(): any { throw new Error('unused'); }
  previewBatchRename(): any { throw new Error('unused'); }
  executeBatchRename(): any { throw new Error('unused'); }
  undoBatchRename(): any { throw new Error('unused'); }
}

function deferred<T>() {
  let resolve!: (value: T) => void;
  let reject!: (reason: unknown) => void;
  const promise = new Promise<T>((res, rej) => { resolve = res; reject = rej; });
  return { promise, resolve, reject };
}
