import { HttpErrorResponse } from '@angular/common/http';
import { EMPTY, Observable } from 'rxjs';
import {
  BatchRenameOperationDto,
  BatchRenamePreviewDto,
  BatchRenamePreviewRequestDto,
  FileEntryDto,
  SourceDto,
  SystemMetricsDto,
  UploadEvent,
  UploadLimitsDto,
} from '../api/api.models';
import { MultiRenameContext } from './multi-rename.models';
import { MultiRenameStore } from './multi-rename-store';
import { CommanderApiTestBase } from '../../testing/commander-api-test-base';

describe('MultiRenameStore', () => {
  let api: FakeMultiRenameApi;
  let store: MultiRenameStore;

  beforeEach(() => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date('2026-08-20T08:00:00Z'));
    api = new FakeMultiRenameApi();
    store = new MultiRenameStore(api);
  });

  afterEach(() => {
    store.close();
    vi.useRealTimers();
  });

  it('debounces rule edits and discards a stale preview response', async () => {
    const first = deferred<BatchRenamePreviewDto>();
    const second = deferred<BatchRenamePreviewDto>();
    api.previewHandler = (request) =>
      request.rules.nameMask === 'first' ? first.promise : second.promise;
    store.open(context());
    store.updateRules({ nameMask: 'first' });
    await vi.advanceTimersByTimeAsync(250);
    store.updateRules({ nameMask: 'second' });
    await vi.advanceTimersByTimeAsync(250);

    second.resolve(previewResponse({ rows: [previewRow('second-001.txt')] }));
    await settlePromises();
    first.resolve(previewResponse({ rows: [previewRow('first-001.txt')] }));
    await settlePromises();

    expect(store.state().preview?.rows[0]?.newName).toBe('second-001.txt');
  });

  it('enables Start only for a current executable preview with changes', async () => {
    api.previewHandler = () =>
      Promise.resolve(previewResponse({ canExecute: true, changedCount: 2 }));
    store.open(context());
    await vi.advanceTimersByTimeAsync(250);

    expect(store.canExecute()).toBe(true);
    store.updateRules({ nameMask: '[N]' });
    expect(store.canExecute()).toBe(false);
  });

  it('shows a disabled read-only state without requesting preview', () => {
    store.open(context({ isReadOnly: true }));

    expect(store.state().disabledReason).toContain('read-only');
    expect(api.previewRequests).toHaveLength(0);
  });

  it('marks an authoritative preview stale when its expiry arrives', async () => {
    api.previewHandler = () =>
      Promise.resolve(previewResponse({ expiresAt: '2026-08-20T08:00:01Z' }));
    store.open(context());
    await vi.advanceTimersByTimeAsync(250);

    expect(store.canExecute()).toBe(true);
    await vi.advanceTimersByTimeAsync(1_000);
    expect(store.canExecute()).toBe(false);
    expect(store.state().preview?.rows[0]?.status).toBe('stale');
    expect(store.state().errorCode).toBe('rename_plan_expired');
  });

  it('executes and undoes only the current authoritative operation', async () => {
    api.previewHandler = () => Promise.resolve(previewResponse());
    api.executeHandler = () => Promise.resolve(operationResponse());
    api.undoHandler = () =>
      Promise.resolve(operationResponse({ status: 'undone', undoAvailable: false }));
    store.open(context());
    await vi.advanceTimersByTimeAsync(250);

    expect(await store.execute()).toBe(true);
    expect(store.canUndo()).toBe(true);
    expect(await store.undo()).toBe(true);
    expect(store.state().operation?.status).toBe('undone');
    expect(store.canUndo()).toBe(false);
  });

  it('maps only stable Problem Details codes and never exposes server detail', async () => {
    api.previewHandler = () =>
      Promise.reject(
        new HttpErrorResponse({
          status: 409,
          error: { code: 'rename_plan_stale', detail: 'D:\\private\\file.txt' },
        }),
      );
    store.open(context());
    await vi.advanceTimersByTimeAsync(250);

    expect(store.state().errorCode).toBe('rename_plan_stale');
    expect(JSON.stringify(store.state())).not.toContain('private');
  });
});

class FakeMultiRenameApi extends CommanderApiTestBase {
  async listArchive(): Promise<never> {
    throw new Error('Not used by these tests');
  }
  readonly previewRequests: BatchRenamePreviewRequestDto[] = [];
  previewHandler: (request: BatchRenamePreviewRequestDto) => Promise<BatchRenamePreviewDto> = () =>
    Promise.resolve(previewResponse());
  executeHandler: (planId: string) => Promise<BatchRenameOperationDto> = () =>
    Promise.resolve(operationResponse());
  undoHandler: (operationId: string) => Promise<BatchRenameOperationDto> = () =>
    Promise.resolve(operationResponse({ status: 'undone', undoAvailable: false }));

  previewBatchRename(request: BatchRenamePreviewRequestDto): Promise<BatchRenamePreviewDto> {
    this.previewRequests.push(request);
    return this.previewHandler(request);
  }

  executeBatchRename(planId: string): Promise<BatchRenameOperationDto> {
    return this.executeHandler(planId);
  }

  undoBatchRename(operationId: string): Promise<BatchRenameOperationDto> {
    return this.undoHandler(operationId);
  }

  async getSystemMetrics(): Promise<SystemMetricsDto> {
    throw new Error('Not used');
  }

  async getSources(): Promise<readonly SourceDto[]> {
    return [];
  }

  async listFiles(): Promise<readonly FileEntryDto[]> {
    return [];
  }

  async getInfo(): Promise<FileEntryDto> {
    throw new Error('Not used');
  }

  async getUploadLimits(): Promise<UploadLimitsDto> {
    throw new Error('Not used');
  }

  uploadFiles(): Observable<UploadEvent> {
    return EMPTY;
  }

  async previewArchiveExtraction(): Promise<never> { throw new Error('Not used by these tests'); }
  async executeArchiveExtraction(): Promise<never> { throw new Error('Not used by these tests'); }
  async getArchiveExtraction(): Promise<never> { throw new Error('Not used by these tests'); }
  async cancelArchiveExtraction(): Promise<never> { throw new Error('Not used by these tests'); }
}

function context(overrides: Partial<MultiRenameContext> = {}): MultiRenameContext {
  return {
    panelSide: 'left',
    sourceId: 'media',
    sourceName: 'Media',
    directoryPath: '/Movies',
    entries: [entry('holiday.txt')],
    isAvailable: true,
    isReadOnly: false,
    ...overrides,
  };
}

function entry(name: string): FileEntryDto {
  return {
    name,
    relativePath: `/Movies/${name}`,
    type: 'file',
    size: 1,
    modifiedAt: '2026-08-20T07:00:00Z',
    extension: 'txt',
    isReadOnly: false,
    isSymbolicLink: false,
    attributes: 'Normal',
    archiveFormatHint: null,
    archiveRole: null,
  };
}

function previewRow(newName: string) {
  return {
    sourcePath: '/Movies/holiday.txt',
    oldName: 'holiday.txt',
    oldExtension: 'txt',
    newName,
    type: 'file' as const,
    size: 1,
    modifiedAt: '2026-08-20T07:00:00Z',
    status: 'ready' as const,
    message: null,
  };
}

function previewResponse(overrides: Partial<BatchRenamePreviewDto> = {}): BatchRenamePreviewDto {
  return {
    planId: '11111111-1111-4111-8111-111111111111',
    expiresAt: '2026-08-20T08:10:00Z',
    rows: [previewRow('Archive-001.txt')],
    canExecute: true,
    changedCount: 1,
    unchangedCount: 0,
    invalidCount: 0,
    ...overrides,
  };
}

function operationResponse(
  overrides: Partial<BatchRenameOperationDto> = {},
): BatchRenameOperationDto {
  return {
    operationId: '22222222-2222-4222-8222-222222222222',
    status: 'completed',
    rows: [],
    compensationAttempted: false,
    recoveryRequired: false,
    undoAvailable: true,
    undoExpiresAt: '2026-08-20T08:30:00Z',
    ...overrides,
  };
}

function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((accept) => {
    resolve = accept;
  });
  return { promise, resolve };
}

async function settlePromises(): Promise<void> {
  await Promise.resolve();
  await Promise.resolve();
}
