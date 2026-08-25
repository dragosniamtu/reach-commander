import {
  DeletePreviewDto,
  FileOperationConflictDecision,
  FileOperationStatusDto,
  RestorePreviewDto,
  TrashEntryDto,
} from '../../../core/api/api.models';
import { CommanderApiTestBase } from '../../../testing/commander-api-test-base';
import { FileOperationStore } from '../file-operations/file-operation.store';
import { TrashStore } from './trash.store';

describe('TrashStore', () => {
  let api: FakeTrashApi;
  let operations: { track: ReturnType<typeof vi.fn> };
  let store: TrashStore;

  beforeEach(() => {
    api = new FakeTrashApi();
    operations = { track: vi.fn() };
    store = new TrashStore(api, operations as unknown as FileOperationStore);
  });

  it('filters by source and owns stable multi-selection', async () => {
    api.trash = [trashEntry('one', 'downloads'), trashEntry('two', 'media')];
    await store.load();
    store.toggleSelection('one');
    store.toggleSelection('two');
    expect([...store.selection()]).toEqual(['one', 'two']);

    await store.setSourceFilter('media');

    expect(api.listRequests).toEqual([undefined, 'media']);
    expect(store.entries().map((entry) => entry.trashId)).toEqual(['two']);
    expect(store.selection()).toEqual(new Set());
  });

  it('previews selected restores, resolves every conflict, and tracks the returned job', async () => {
    api.trash = [trashEntry('one', 'downloads'), trashEntry('two', 'media')];
    api.restorePreview = restorePreview();
    await store.load();
    store.selectAll();
    await store.previewSelectedRestore();

    expect(api.restorePreviewRequests).toEqual([{ trashIds: ['one', 'two'] }]);
    expect(store.canSubmitRestore()).toBe(false);
    store.setRestoreConflictDecision('conflict-one', 'createUniqueName', true);
    expect(store.canSubmitRestore()).toBe(true);

    await store.submitRestore();
    expect(api.restoreSubmissions).toEqual([{
      planId: 'restore-plan',
      resolutions: [
        { conflictId: 'conflict-one', decision: 'createUniqueName' },
        { conflictId: 'conflict-two', decision: 'createUniqueName' },
      ],
    }]);
    expect(operations.track).toHaveBeenCalledWith(
      expect.objectContaining({ operationId: 'restore-operation', kind: 'restore' }),
    );
  });

  it('previews panel deletion and forwards its confirmation to the operation queue', async () => {
    api.deletePreview = deletePreview();
    await store.previewDelete({
      sourceId: 'media', logicalPaths: ['/Movies/one.mkv'], mode: 'trash',
    });
    await store.submitDelete(false);

    expect(api.deleteSubmissions).toEqual([{
      planId: 'delete-plan', permanentDeleteConfirmed: false,
    }]);
    expect(operations.track).toHaveBeenCalledWith(
      expect.objectContaining({ operationId: 'trash-operation', kind: 'trash' }),
    );
  });

  it('requires explicit confirmation data for permanent item deletion and Empty Trash', async () => {
    api.trash = [trashEntry('one', 'downloads')];
    await store.load();
    store.toggleSelection('one');

    await store.permanentlyDeleteSelected(true);
    await store.emptyTrash(true);

    expect(api.permanentDeleteRequests).toEqual([{
      trashIds: ['one'], permanentDeleteConfirmed: true,
    }]);
    expect(api.emptyRequests).toEqual([{
      sourceId: null, permanentDeleteConfirmed: true,
    }]);
    expect(operations.track).toHaveBeenCalledTimes(2);
  });

  it('reset clears protected state and discards an older listing response', async () => {
    const listing = deferred<readonly TrashEntryDto[]>();
    api.listResult = listing.promise;
    const loading = store.load();
    store.resetProtectedState();
    listing.resolve([trashEntry('old', 'downloads')]);
    await loading;

    expect(store.entries()).toEqual([]);
    expect(store.selection()).toEqual(new Set());
    expect(store.restorePreview()).toBeNull();
    expect(store.deletePreview()).toBeNull();
  });
});

function trashEntry(trashId: string, sourceId: string): TrashEntryDto {
  return {
    trashId, sourceId, originalLogicalPath: `/${trashId}.txt`, name: `${trashId}.txt`,
    type: 'file', size: 1, deletedAt: '2026-08-25T09:00:00Z',
  };
}

function restorePreview(): RestorePreviewDto {
  return {
    planId: 'restore-plan', expiresAt: '2026-08-25T10:00:00Z', entries: [],
    parentsToCreate: ['/Restored'], conflicts: [
      conflict('conflict-one', ['overwrite', 'skip', 'createUniqueName']),
      conflict('conflict-two', ['skip', 'createUniqueName']),
    ],
  };
}

function conflict(id: string, allowedDecisions: readonly FileOperationConflictDecision[]) {
  return {
    conflictId: id, sourceLogicalPath: `/${id}.txt`, destinationLogicalPath: `/${id}.txt`,
    sourceType: 'file' as const, destinationType: 'file' as const, allowedDecisions,
  };
}

function deletePreview(): DeletePreviewDto {
  return {
    planId: 'delete-plan', expiresAt: '2026-08-25T10:00:00Z', mode: 'trash',
    trashAvailable: true, trashUnavailableReason: null, totalItems: 1, totalBytes: 1,
  };
}

function operation(
  operationId: string,
  kind: FileOperationStatusDto['kind'],
): FileOperationStatusDto {
  return {
    operationId, kind, phase: 'queued', queuePosition: 0,
    createdAt: '2026-08-25T09:00:00Z', updatedAt: '2026-08-25T09:00:00Z',
    progress: {
      currentLogicalName: null, completedItems: 0, totalItems: 1,
      completedBytes: 0, totalBytes: 1, percentage: 0, bytesPerSecond: null,
      elapsed: '00:00:00', estimatedRemaining: null,
    },
    outcomes: [], warnings: [], acknowledged: false,
  };
}

class FakeTrashApi extends CommanderApiTestBase {
  trash: readonly TrashEntryDto[] = [];
  listResult: Promise<readonly TrashEntryDto[]> | null = null;
  readonly listRequests: Array<string | undefined> = [];
  restorePreview = restorePreview();
  deletePreview = deletePreview();
  readonly restorePreviewRequests: any[] = [];
  readonly restoreSubmissions: any[] = [];
  readonly deleteSubmissions: any[] = [];
  readonly permanentDeleteRequests: any[] = [];
  readonly emptyRequests: any[] = [];

  override listTrash(sourceId?: string): Promise<readonly TrashEntryDto[]> {
    this.listRequests.push(sourceId);
    return this.listResult ?? Promise.resolve(
      sourceId ? this.trash.filter((entry) => entry.sourceId === sourceId) : this.trash,
    );
  }
  override previewRestore(request: any): Promise<RestorePreviewDto> {
    this.restorePreviewRequests.push(request);
    return Promise.resolve(this.restorePreview);
  }
  override submitRestore(request: any): Promise<FileOperationStatusDto> {
    this.restoreSubmissions.push(request);
    return Promise.resolve(operation('restore-operation', 'restore'));
  }
  override previewDelete(): Promise<DeletePreviewDto> {
    return Promise.resolve(this.deletePreview);
  }
  override submitDelete(request: any): Promise<FileOperationStatusDto> {
    this.deleteSubmissions.push(request);
    return Promise.resolve(operation('trash-operation', 'trash'));
  }
  override permanentlyDeleteTrash(request: any): Promise<FileOperationStatusDto> {
    this.permanentDeleteRequests.push(request);
    return Promise.resolve(operation('delete-operation', 'permanentDelete'));
  }
  override emptyTrash(request: any): Promise<FileOperationStatusDto> {
    this.emptyRequests.push(request);
    return Promise.resolve(operation('empty-operation', 'emptyTrash'));
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
