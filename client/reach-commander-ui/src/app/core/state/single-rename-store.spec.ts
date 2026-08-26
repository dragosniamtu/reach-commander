import { HttpErrorResponse } from '@angular/common/http';
import { EMPTY, Observable } from 'rxjs';
import {
  ArchiveDirectoryDto,
  ArchiveExtractionOperationDto,
  ArchiveExtractionPreviewDto,
  ArchiveExtractionPreviewRequestDto,
  BatchRenameOperationDto,
  BatchRenamePreviewDto,
  BatchRenamePreviewRequestDto,
  ExactRenamePreviewRequestDto,
  FileEntryDto,
  SourceDto,
  SystemMetricsDto,
  UploadEvent,
  UploadLimitsDto,
} from '../api/api.models';
import { CommanderApiTestBase } from '../../testing/commander-api-test-base';
import { SingleRenameContext } from './single-rename.models';
import { SingleRenameStore } from './single-rename-store';

describe('SingleRenameStore', () => {
  let api: FakeSingleRenameApi;
  let store: SingleRenameStore;

  beforeEach(() => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date('2026-08-26T08:00:00Z'));
    api = new FakeSingleRenameApi();
    store = new SingleRenameStore(api);
  });

  afterEach(() => {
    store.close();
    vi.useRealTimers();
  });

  it('debounces literal names and ignores a late preview', async () => {
    const first = deferred<BatchRenamePreviewDto>();
    const second = deferred<BatchRenamePreviewDto>();
    api.previewHandler = (request) =>
      request.newName === 'first.txt' ? first.promise : second.promise;
    store.open(context());
    store.setName('first.txt');
    await vi.advanceTimersByTimeAsync(250);
    store.setName('[N]-literal.txt');
    await vi.advanceTimersByTimeAsync(250);

    second.resolve(previewResponse('[N]-literal.txt'));
    await settlePromises();
    first.resolve(previewResponse('first.txt'));
    await settlePromises();

    expect(store.state().preview?.rows[0]?.newName).toBe('[N]-literal.txt');
    expect(api.previewRequests.at(-1)?.newName).toBe('[N]-literal.txt');
  });

  it('keeps conflicts non-executable and preserves the requested name', async () => {
    api.previewHandler = () =>
      Promise.resolve(
        previewResponse('taken.txt', {
          canExecute: false,
          invalidCount: 1,
          changedCount: 0,
          rows: [
            previewRow(
              'taken.txt',
              'conflict',
              'The destination name is already in use.',
            ),
          ],
        }),
      );
    store.open(context());
    store.setName('taken.txt');
    await vi.advanceTimersByTimeAsync(250);

    expect(store.canExecute()).toBe(false);
    expect(store.state().newName).toBe('taken.txt');
    expect(store.state().preview?.rows[0]?.message).toContain('already in use');
  });

  it('executes only the current plan and emits one logical completion', async () => {
    const completed = vi.fn();
    store.setCompletionHandler(completed);
    api.previewHandler = () => Promise.resolve(previewResponse('renamed.txt'));
    api.executeHandler = () => Promise.resolve(operationResponse('/Movies/renamed.txt'));
    store.open(context());
    store.setName('renamed.txt');
    await vi.advanceTimersByTimeAsync(250);

    expect(await store.execute()).toBe(true);
    expect(completed).toHaveBeenCalledWith(
      expect.objectContaining({ newLogicalPath: '/Movies/renamed.txt' }),
    );
    expect(api.executedPlanIds).toEqual(['11111111-1111-4111-8111-111111111111']);
  });

  it('does not preview an empty name and invalidates the previous plan immediately', async () => {
    api.previewHandler = (request) =>
      Promise.resolve(
        previewResponse(request.newName, {
          canExecute: false,
          changedCount: 0,
          unchangedCount: 1,
          rows: [previewRow(request.newName, 'unchanged')],
        }),
      );
    store.open(context());
    await vi.advanceTimersByTimeAsync(250);
    expect(store.canExecute()).toBe(false);

    store.setName('');
    await vi.advanceTimersByTimeAsync(250);

    expect(api.previewRequests).toHaveLength(1);
    expect(store.state().preview).toBeNull();
    expect(store.state().previewPending).toBe(false);
  });

  it('marks an authoritative preview stale when its expiry arrives', async () => {
    api.previewHandler = () =>
      Promise.resolve(
        previewResponse('renamed.txt', { expiresAt: '2026-08-26T08:00:01Z' }),
      );
    store.open(context());
    store.setName('renamed.txt');
    await vi.advanceTimersByTimeAsync(250);

    expect(store.canExecute()).toBe(true);
    await vi.advanceTimersByTimeAsync(1_000);
    expect(store.canExecute()).toBe(false);
    expect(store.state().preview?.rows[0]?.status).toBe('stale');
    expect(store.state().errorCode).toBe('rename_plan_expired');
  });

  it('maps only stable problem codes and never retains server detail', async () => {
    api.previewHandler = () =>
      Promise.reject(
        new HttpErrorResponse({
          status: 409,
          error: { code: 'rename_plan_stale', detail: 'D:\\private\\file.txt' },
        }),
      );
    store.open(context());
    store.setName('renamed.txt');
    await vi.advanceTimersByTimeAsync(250);

    expect(store.state().errorCode).toBe('rename_plan_stale');
    expect(JSON.stringify(store.state())).not.toContain('private');
  });

  it('ignores a preview response after close and resets protected state', async () => {
    const preview = deferred<BatchRenamePreviewDto>();
    api.previewHandler = () => preview.promise;
    store.open(context());
    store.setName('renamed.txt');
    await vi.advanceTimersByTimeAsync(250);
    store.close();
    preview.resolve(previewResponse('renamed.txt'));
    await settlePromises();

    expect(store.state().open).toBe(false);
    expect(store.state().context).toBeNull();
    expect(store.state().newName).toBe('');
    expect(store.state().preview).toBeNull();
  });
});

class FakeSingleRenameApi extends CommanderApiTestBase {
  readonly previewRequests: ExactRenamePreviewRequestDto[] = [];
  readonly executedPlanIds: string[] = [];
  previewHandler: (
    request: ExactRenamePreviewRequestDto,
  ) => Promise<BatchRenamePreviewDto> = (request) =>
    Promise.resolve(previewResponse(request.newName));
  executeHandler: (planId: string) => Promise<BatchRenameOperationDto> = () =>
    Promise.resolve(operationResponse('/Movies/renamed.txt'));

  override previewRename(
    request: ExactRenamePreviewRequestDto,
  ): Promise<BatchRenamePreviewDto> {
    this.previewRequests.push(request);
    return this.previewHandler(request);
  }

  override executeBatchRename(planId: string): Promise<BatchRenameOperationDto> {
    this.executedPlanIds.push(planId);
    return this.executeHandler(planId);
  }

  override previewBatchRename(
    _request: BatchRenamePreviewRequestDto,
  ): Promise<BatchRenamePreviewDto> {
    throw new Error('Not used');
  }

  override undoBatchRename(_operationId: string): Promise<BatchRenameOperationDto> {
    throw new Error('Not used');
  }

  override getSystemMetrics(): Promise<SystemMetricsDto> {
    throw new Error('Not used');
  }

  override getSources(): Promise<readonly SourceDto[]> {
    return Promise.resolve([]);
  }

  override listFiles(): Promise<readonly FileEntryDto[]> {
    return Promise.resolve([]);
  }

  override listArchive(): Promise<ArchiveDirectoryDto> {
    throw new Error('Not used');
  }

  override getInfo(): Promise<FileEntryDto> {
    throw new Error('Not used');
  }

  override getUploadLimits(): Promise<UploadLimitsDto> {
    throw new Error('Not used');
  }

  override uploadFiles(): Observable<UploadEvent> {
    return EMPTY;
  }

  override previewArchiveExtraction(
    _request: ArchiveExtractionPreviewRequestDto,
  ): Promise<ArchiveExtractionPreviewDto> {
    throw new Error('Not used');
  }

  override executeArchiveExtraction(_planId: string): Promise<ArchiveExtractionOperationDto> {
    throw new Error('Not used');
  }

  override getArchiveExtraction(_operationId: string): Promise<ArchiveExtractionOperationDto> {
    throw new Error('Not used');
  }

  override cancelArchiveExtraction(_operationId: string): Promise<ArchiveExtractionOperationDto> {
    throw new Error('Not used');
  }
}

function context(overrides: Partial<SingleRenameContext> = {}): SingleRenameContext {
  return {
    panelSide: 'left',
    sourceId: 'media',
    sourceName: 'Media',
    directoryPath: '/Movies',
    entry: fileEntry('holiday.txt'),
    isAvailable: true,
    isReadOnly: false,
    ...overrides,
  };
}

function fileEntry(name: string): FileEntryDto {
  return {
    name,
    relativePath: `/Movies/${name}`,
    type: 'file',
    size: 7,
    modifiedAt: '2026-08-26T07:00:00Z',
    extension: 'txt',
    isReadOnly: false,
    isSymbolicLink: false,
    attributes: 'Normal',
    archiveFormatHint: null,
    archiveRole: null,
  };
}

function previewRow(
  newName: string,
  status: 'ready' | 'unchanged' | 'invalid' | 'conflict' | 'stale' = 'ready',
  message: string | null = null,
) {
  return {
    sourcePath: '/Movies/holiday.txt',
    oldName: 'holiday.txt',
    oldExtension: 'txt',
    newName,
    type: 'file' as const,
    size: 7,
    modifiedAt: '2026-08-26T07:00:00Z',
    status,
    message,
  };
}

function previewResponse(
  newName: string,
  overrides: Partial<BatchRenamePreviewDto> = {},
): BatchRenamePreviewDto {
  return {
    planId: '11111111-1111-4111-8111-111111111111',
    expiresAt: '2026-08-26T08:10:00Z',
    rows: [previewRow(newName)],
    canExecute: true,
    changedCount: 1,
    unchangedCount: 0,
    invalidCount: 0,
    ...overrides,
  };
}

function operationResponse(newLogicalPath: string): BatchRenameOperationDto {
  return {
    operationId: '22222222-2222-4222-8222-222222222222',
    status: 'completed',
    rows: [
      {
        oldPath: '/Movies/holiday.txt',
        newPath: newLogicalPath,
        currentPath: newLogicalPath,
        oldName: 'holiday.txt',
        newName: newLogicalPath.split('/').at(-1)!,
        currentName: newLogicalPath.split('/').at(-1)!,
        type: 'file',
        result: 'completed',
        message: null,
      },
    ],
    compensationAttempted: false,
    recoveryRequired: false,
    undoAvailable: true,
    undoExpiresAt: '2026-08-26T08:30:00Z',
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
