import { DestroyRef } from '@angular/core';
import {
  FileOperationConflictDecision,
  FileOperationPreviewDto,
  FileOperationStatusDto,
} from '../../../core/api/api.models';
import { CommanderApiTestBase } from '../../../testing/commander-api-test-base';
import { CapturedFileOperationContext } from '../../../core/state/file-operation.models';
import { FileOperationScheduler, FileOperationStore } from './file-operation.store';

describe('FileOperationStore', () => {
  let api: FakeFileOperationApi;
  let scheduler: FakeScheduler;
  let store: FileOperationStore;
  let destroy: () => void;

  beforeEach(() => {
    api = new FakeFileOperationApi();
    scheduler = new FakeScheduler();
    const callbacks: Array<() => void> = [];
    destroy = () => callbacks.splice(0).forEach((callback) => callback());
    store = new FileOperationStore(api, scheduler, {
      destroyed: false,
      onDestroy: (callback: () => void) => {
        callbacks.push(callback);
        return () => undefined;
      },
    } as unknown as DestroyRef);
  });

  it('previews an immutable capture and ignores a slower obsolete destination response', async () => {
    const context = operationContext();
    const first = deferred<FileOperationPreviewDto>();
    api.previewResults.push(first.promise, Promise.resolve(preview({
      planId: 'new-plan',
      destinationLogicalDirectory: '/New',
    })));

    const opening = store.open('copy', context);
    context.logicalPaths.push('/later.txt');
    const changing = store.setDestination('/New');
    first.resolve(preview({ planId: 'old-plan', destinationLogicalDirectory: '/Old' }));
    await Promise.all([opening, changing]);

    expect(store.context()?.logicalPaths).toEqual(['/alpha.txt']);
    expect(store.preview()?.planId).toBe('new-plan');
    expect(store.destination()).toBe('/New');
    expect(api.previewRequests[1]?.logicalPaths).toEqual(['/alpha.txt']);
  });

  it('requires every conflict decision and can apply one allowed decision to remaining rows', async () => {
    api.previewResults.push(Promise.resolve(previewWithConflicts()));
    await store.open('copy', operationContext());

    expect(store.canSubmit()).toBe(false);
    store.setConflictDecision('one', 'createUniqueName', true);

    expect([...store.conflictDecisions().entries()]).toEqual([
      ['one', 'createUniqueName'],
      ['two', 'createUniqueName'],
    ]);
    expect(store.canSubmit()).toBe(true);
  });

  it('submits resolutions, blocks in a modal, backgrounds, and restores progress', async () => {
    api.previewResults.push(Promise.resolve(previewWithConflicts()));
    api.submitResult = status({ phase: 'queued' });
    await store.open('copy', operationContext());
    store.setConflictDecision('one', 'skip', true);

    await store.submit();
    expect(api.submissions[0]).toEqual({
      planId: 'plan-id',
      resolutions: [
        { conflictId: 'one', decision: 'skip' },
        { conflictId: 'two', decision: 'skip' },
      ],
    });
    expect(store.dialog()).toBe('progress');
    expect(store.presentation()).toBe('modal');
    expect(scheduler.delays).toEqual([750]);

    store.background();
    expect(store.presentation()).toBe('background');
    store.restoreProgress('operation-id');
    expect(store.presentation()).toBe('modal');
  });

  it('uses one polling timer and refreshes once on a terminal transition', async () => {
    const completed = vi.fn();
    store.setTerminalHandler(completed);
    store.track(status({ phase: 'queued' }));
    store.track(status({ operationId: 'second', phase: 'running' }));
    expect(scheduler.pending).toBe(1);

    api.statusResults.set('operation-id', status({ phase: 'completed' }));
    api.statusResults.set('second', status({ operationId: 'second', phase: 'running' }));
    await scheduler.runNext();

    expect(completed).toHaveBeenCalledOnce();
    expect(completed).toHaveBeenCalledWith(
      expect.objectContaining({ operationId: 'operation-id', phase: 'completed' }),
      null,
    );
    expect(scheduler.pending).toBe(1);

    store.track(status({ phase: 'completed' }));
    expect(completed).toHaveBeenCalledOnce();
  });

  it('restores server jobs and reset clears polling without cancelling them', async () => {
    api.listResult = [status({ phase: 'running' })];
    await store.restoreTasks();

    expect(store.tasks()).toHaveLength(1);
    expect(scheduler.pending).toBe(1);
    store.resetProtectedState();

    expect(store.tasks()).toEqual([]);
    expect(scheduler.pending).toBe(0);
    expect(api.cancelRequests).toEqual([]);
  });

  it('cancels polling on destruction and acknowledges terminal tasks', async () => {
    store.track(status({ phase: 'running' }));
    expect(scheduler.pending).toBe(1);
    destroy();
    expect(scheduler.pending).toBe(0);

    store.track(status({ phase: 'completed' }));
    await store.acknowledge('operation-id');
    expect(api.acknowledgeRequests).toEqual(['operation-id']);
    expect(store.tasks()).toEqual([]);
  });
});

function operationContext(): CapturedFileOperationContext & { logicalPaths: string[] } {
  return {
    kind: 'copy', sourceId: 'downloads', logicalPaths: ['/alpha.txt'],
    destinationSourceId: 'media', destinationLogicalDirectory: '/Old',
    selectedNames: ['alpha.txt'], knownTotalBytes: 1,
  };
}

function preview(overrides: Partial<FileOperationPreviewDto> = {}): FileOperationPreviewDto {
  return {
    kind: 'copy', sourceId: 'downloads', logicalPaths: ['/alpha.txt'],
    destinationSourceId: 'media', destinationLogicalDirectory: '/Old',
    planId: 'plan-id', expiresAt: '2026-08-25T10:00:00Z', totalItems: 1, totalBytes: 1,
    conflicts: [], warnings: [], ...overrides,
  };
}

function previewWithConflicts(): FileOperationPreviewDto {
  return preview({ conflicts: [
    conflict('one', '/one.txt', ['overwrite', 'skip', 'createUniqueName']),
    conflict('two', '/two.txt', ['skip', 'createUniqueName']),
  ] });
}

function conflict(
  conflictId: string,
  destinationLogicalPath: string,
  allowedDecisions: readonly FileOperationConflictDecision[],
) {
  return {
    conflictId, sourceLogicalPath: destinationLogicalPath, destinationLogicalPath,
    sourceType: 'file' as const, destinationType: 'file' as const, allowedDecisions,
  };
}

function status(overrides: Partial<FileOperationStatusDto> = {}): FileOperationStatusDto {
  return {
    operationId: 'operation-id', kind: 'copy', phase: 'running', queuePosition: 0,
    createdAt: '2026-08-25T09:00:00Z', updatedAt: '2026-08-25T09:00:01Z',
    progress: {
      currentLogicalName: 'alpha.txt', completedItems: 0, totalItems: 1,
      completedBytes: 0, totalBytes: 1, percentage: 0, bytesPerSecond: null,
      elapsed: '00:00:01', estimatedRemaining: null,
    },
    outcomes: [], warnings: [], acknowledged: false, ...overrides,
  };
}

class FakeScheduler implements FileOperationScheduler {
  private callbacks: Array<() => Promise<void> | void> = [];
  readonly delays: number[] = [];
  get pending(): number { return this.callbacks.length; }
  schedule(callback: () => Promise<void> | void, delay: number): unknown {
    this.callbacks.push(callback);
    this.delays.push(delay);
    return callback;
  }
  cancel(handle: unknown): void {
    this.callbacks = this.callbacks.filter((callback) => callback !== handle);
  }
  async runNext(): Promise<void> { await this.callbacks.shift()?.(); }
}

class FakeFileOperationApi extends CommanderApiTestBase {
  readonly previewRequests: any[] = [];
  readonly previewResults: Array<Promise<FileOperationPreviewDto>> = [];
  readonly submissions: any[] = [];
  readonly statusResults = new Map<string, FileOperationStatusDto>();
  readonly cancelRequests: string[] = [];
  readonly acknowledgeRequests: string[] = [];
  submitResult = status({ phase: 'queued' });
  listResult: readonly FileOperationStatusDto[] = [];

  override previewFileOperation(request: any): Promise<FileOperationPreviewDto> {
    this.previewRequests.push(request);
    return this.previewResults.shift() ?? Promise.resolve(preview());
  }
  override submitFileOperation(request: any): Promise<FileOperationStatusDto> {
    this.submissions.push(request);
    return Promise.resolve(this.submitResult);
  }
  override listFileOperations(): Promise<readonly FileOperationStatusDto[]> {
    return Promise.resolve(this.listResult);
  }
  override getFileOperation(operationId: string): Promise<FileOperationStatusDto> {
    return Promise.resolve(this.statusResults.get(operationId) ?? status({ operationId }));
  }
  override cancelFileOperation(operationId: string): Promise<FileOperationStatusDto> {
    this.cancelRequests.push(operationId);
    return Promise.resolve(status({ operationId, phase: 'cancelled' }));
  }
  override acknowledgeFileOperation(operationId: string): Promise<void> {
    this.acknowledgeRequests.push(operationId);
    return Promise.resolve();
  }

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
  let reject!: (reason?: unknown) => void;
  const promise = new Promise<T>((complete, fail) => { resolve = complete; reject = fail; });
  return { promise, resolve, reject };
}
