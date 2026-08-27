import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ReachCommanderApi } from './reach-commander-api';
import { HttpEventType } from '@angular/common/http';
import { firstValueFrom, toArray } from 'rxjs';
import {
  ArchiveDirectoryDto,
  ArchiveExtractionOperationDto,
  ArchiveExtractionPreviewDto,
  ArchiveExtractionPreviewRequestDto,
  BatchRenameOperationDto,
  BatchRenamePreviewDto,
  BatchRenamePreviewRequestDto,
  DeletePreviewRequestDto,
  ExactRenamePreviewRequestDto,
  FileOperationPreviewRequestDto,
  FileOperationStatusDto,
  RestorePreviewRequestDto,
  SystemMetricsDto,
  SystemUpdateStatusDto,
  UploadLimitsDto,
  UploadResultDto,
} from './api.models';

describe('ReachCommanderApi', () => {
  let api: ReachCommanderApi;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [ReachCommanderApi, provideHttpClient(), provideHttpClientTesting()],
    });
    api = TestBed.inject(ReachCommanderApi);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('sends only source id and logical path when listing files', async () => {
    const result = api.listFiles('media', '/Movies & TV');
    const request = http.expectOne(
      (candidate) =>
        candidate.url === '/api/files' &&
        candidate.params.get('sourceId') === 'media' &&
        candidate.params.get('path') === '/Movies & TV',
    );

    expect(request.request.method).toBe('GET');
    expect(request.request.params.keys().sort()).toEqual(['path', 'sourceId']);
    request.flush([]);

    await expect(result).resolves.toEqual([]);
  });

  it('URL-encodes the source, archive, and virtual path when listing an archive', async () => {
    const expected: ArchiveDirectoryDto = {
      sourceId: 'media library',
      archivePath: '/Family & Friends/photos 2025.7z',
      path: '/Summer & Winter',
      format: 'sevenZip',
      volumeCount: 1,
      isReadOnly: true,
      entries: [{
        name: 'photo.jpg',
        relativePath: '/Summer & Winter/photo.jpg',
        type: 'file',
        size: 12,
        modifiedAt: null,
        extension: 'jpg',
        isReadOnly: true,
        isSymbolicLink: false,
        attributes: 'Archive',
        archiveFormatHint: null,
        archiveRole: null,
      }],
    };
    const result = api.listArchive(
      'media library',
      '/Family & Friends/photos 2025.7z',
      '/Summer & Winter',
    );
    const request = http.expectOne(
      (candidate) =>
        candidate.url === '/api/archives/entries' &&
        candidate.params.get('sourceId') === 'media library' &&
        candidate.params.get('archivePath') === '/Family & Friends/photos 2025.7z' &&
        candidate.params.get('path') === '/Summer & Winter',
    );

    expect(request.request.method).toBe('GET');
    expect(request.request.params.keys().sort()).toEqual(['archivePath', 'path', 'sourceId']);
    expect(request.request.urlWithParams).toContain('sourceId=media%20library');
    expect(request.request.urlWithParams).toContain('archivePath=/Family%20%26%20Friends/photos%202025.7z');
    expect(request.request.urlWithParams).toContain('path=/Summer%20%26%20Winter');
    request.flush({
      ...expected,
      entries: [{
        path: '/Summer & Winter/photo.jpg',
        name: 'photo.jpg',
        type: 'file',
        size: 12,
        modifiedAt: null,
        extension: 'jpg',
        attributes: 'Archive',
      }],
    });

    await expect(result).resolves.toEqual(expected);
  });

  it('requests source metadata from the stable API route', async () => {
    const result = api.getSources();
    const request = http.expectOne('/api/sources');

    expect(request.request.method).toBe('GET');
    request.flush([]);

    await expect(result).resolves.toEqual([]);
  });

  it('requests the cached system snapshot from the read-only route', async () => {
    const expected = systemMetricsResponse();
    const result = api.getSystemMetrics();
    const request = http.expectOne('/api/system-metrics');

    expect(request.request.method).toBe('GET');
    expect(request.request.params.keys()).toEqual([]);
    request.flush(expected);

    await expect(result).resolves.toEqual(expected);
  });

  it('uses target-free system update status, check, and apply requests', async () => {
    const current = systemUpdateStatus({ phase: 'current' });
    const available = systemUpdateStatus({
      phase: 'available',
      updateAvailable: true,
      canApply: true,
      targetVersion: 'v1.4.0',
    });
    const applying = systemUpdateStatus({
      phase: 'applying',
      progressStage: 'installing',
      updateAvailable: true,
      operationId: 'operation-1',
      targetVersion: 'v1.4.0',
    });

    const get = api.getSystemUpdate();
    const getRequest = http.expectOne('/api/system-update');
    expect(getRequest.request.method).toBe('GET');
    getRequest.flush(current);
    await expect(get).resolves.toEqual(current);

    const check = api.checkSystemUpdate();
    const checkRequest = http.expectOne('/api/system-update/check');
    expect(checkRequest.request.method).toBe('POST');
    expect(checkRequest.request.body).toBeNull();
    checkRequest.flush(available);
    await expect(check).resolves.toEqual(available);

    const apply = api.applySystemUpdate();
    const applyRequest = http.expectOne('/api/system-update/apply');
    expect(applyRequest.request.method).toBe('POST');
    expect(applyRequest.request.body).toBeNull();
    applyRequest.flush(applying);
    await expect(apply).resolves.toEqual(applying);
  });

  it('requests effective upload limits from the stable API route', async () => {
    const expected: UploadLimitsDto = {
      maxFileBytes: 8,
      maxBatchBytes: 12,
      maxFilesPerBatch: 2,
    };
    const result = api.getUploadLimits();
    const request = http.expectOne('/api/uploads/limits');

    expect(request.request.method).toBe('GET');
    request.flush(expected);

    await expect(result).resolves.toEqual(expected);
  });

  it('streams repeated file parts and maps progress plus the final response', async () => {
    const files = [new File(['one'], 'one.txt'), new File([], 'empty.bin')];
    const expected: UploadResultDto = {
      uploadedCount: 2,
      totalBytes: 3,
      files: [
        { name: 'one.txt', relativePath: '/Movies & TV/one.txt', size: 3 },
        { name: 'empty.bin', relativePath: '/Movies & TV/empty.bin', size: 0 },
      ],
    };
    const result = firstValueFrom(
      api
        .uploadFiles({ sourceId: 'media library', directoryPath: '/Movies & TV' }, files)
        .pipe(toArray()),
    );
    const request = http.expectOne(
      (candidate) =>
        candidate.url === '/api/uploads' &&
        candidate.params.get('sourceId') === 'media library' &&
        candidate.params.get('path') === '/Movies & TV',
    );

    expect(request.request.method).toBe('POST');
    expect(request.request.reportProgress).toBe(true);
    expect(request.request.params.keys().sort()).toEqual(['path', 'sourceId']);
    expect(request.request.urlWithParams).toContain('sourceId=media%20library');
    expect(request.request.urlWithParams).toContain('path=/Movies%20%26%20TV');
    expect(request.request.body).toBeInstanceOf(FormData);
    expect((request.request.body as FormData).getAll('files')).toEqual(files);

    request.event({ type: HttpEventType.UploadProgress, loaded: 2, total: 3 });
    request.flush(expected, { status: 201, statusText: 'Created' });

    await expect(result).resolves.toEqual([
      { kind: 'progress', loadedBytes: 2, totalBytes: 3 },
      { kind: 'completed', result: expected },
    ]);
  });

  it('posts only logical values when previewing a batch rename', async () => {
    const body = previewRequest();
    const expected = previewResponse();
    const result = api.previewBatchRename(body);
    const request = http.expectOne('/api/batch-renames/preview');

    expect(request.request.method).toBe('POST');
    expect(request.request.body).toEqual(body);
    expect(JSON.stringify(request.request.body)).not.toContain('physical');
    request.flush(expected);

    await expect(result).resolves.toEqual(expected);
  });

  it('posts only literal logical values when previewing one rename', async () => {
    const body: ExactRenamePreviewRequestDto = {
      sourceId: 'media library',
      directoryPath: '/Movies & TV',
      entryPath: '/Movies & TV/[old].mkv',
      newName: '[N]-literal.mkv',
    };
    const expected = previewResponse();
    const result = api.previewRename(body);
    const request = http.expectOne('/api/renames/preview');

    expect(request.request.method).toBe('POST');
    expect(request.request.body).toEqual(body);
    expect(JSON.stringify(request.request.body)).not.toContain('physical');
    request.flush(expected);

    await expect(result).resolves.toEqual(expected);
  });

  it('uses identifier-only routes for execute and undo', async () => {
    const planId = '11111111-1111-4111-8111-111111111111';
    const operationId = '22222222-2222-4222-8222-222222222222';
    const execute = api.executeBatchRename(planId);
    const executeRequest = http.expectOne(`/api/batch-renames/${planId}/execute`);
    expect(executeRequest.request.method).toBe('POST');
    expect(executeRequest.request.body).toEqual({});
    executeRequest.flush(operationResponse());
    await expect(execute).resolves.toEqual(operationResponse());

    const undo = api.undoBatchRename(operationId);
    const undoRequest = http.expectOne(`/api/batch-renames/${operationId}/undo`);
    expect(undoRequest.request.method).toBe('POST');
    expect(undoRequest.request.body).toEqual({});
    const undone = operationResponse({ status: 'undone', undoAvailable: false });
    undoRequest.flush(undone);
    await expect(undo).resolves.toEqual(undone);
  });

  it('posts only logical archive extraction preview values', async () => {
    const body: ArchiveExtractionPreviewRequestDto = {
      sourceId: 'media library',
      archivePath: '/Family & Friends/photos.7z',
      internalDirectory: '/Family',
      entryPaths: ['/Family/2025'],
      extractAll: false,
      destinationSourceId: 'archive disk',
      destinationPath: '/Photos & Videos',
    };
    const expected = archivePreviewResponse();

    const result = api.previewArchiveExtraction(body);
    const request = http.expectOne('/api/archive-extractions/preview');

    expect(request.request.method).toBe('POST');
    expect(request.request.body).toEqual(body);
    expect(JSON.stringify(request.request.body)).not.toContain('physical');
    request.flush(expected);
    await expect(result).resolves.toEqual(expected);
  });

  it('uses encoded identifier-only extraction lifecycle routes and empty bodies', async () => {
    const planId = 'plan/with spaces';
    const operationId = 'operation/with spaces';
    const expected = archiveOperationResponse();

    const execute = api.executeArchiveExtraction(planId);
    const executeRequest = http.expectOne('/api/archive-extractions/plan%2Fwith%20spaces/execute');
    expect(executeRequest.request.method).toBe('POST');
    expect(executeRequest.request.body).toBeNull();
    executeRequest.flush(expected);
    await expect(execute).resolves.toEqual(expected);

    const status = api.getArchiveExtraction(operationId);
    const statusRequest = http.expectOne('/api/archive-extractions/operation%2Fwith%20spaces');
    expect(statusRequest.request.method).toBe('GET');
    statusRequest.flush(expected);
    await expect(status).resolves.toEqual(expected);

    const cancel = api.cancelArchiveExtraction(operationId);
    const cancelRequest = http.expectOne(
      '/api/archive-extractions/operation%2Fwith%20spaces/cancel',
    );
    expect(cancelRequest.request.method).toBe('POST');
    expect(cancelRequest.request.body).toBeNull();
    cancelRequest.flush(expected);
    await expect(cancel).resolves.toEqual(expected);
  });

  it('uses logical-only queued file operation routes', async () => {
    const previewBody: FileOperationPreviewRequestDto = {
      kind: 'copy',
      sourceId: 'media',
      logicalPaths: ['/Movies/movie.mkv'],
      destinationSourceId: 'downloads',
      destinationLogicalDirectory: '/Complete',
    };
    const preview = api.previewFileOperation(previewBody);
    const previewRequest = http.expectOne('/api/file-operations/preview');
    expect(previewRequest.request.method).toBe('POST');
    expect(previewRequest.request.body).toEqual(previewBody);
    previewRequest.flush({
      planId: 'plan-id',
      expiresAt: '2026-08-25T12:10:00Z',
      ...previewBody,
      totalItems: 1,
      totalBytes: 9,
      conflicts: [],
      warnings: [],
    });
    await preview;

    const submission = { planId: 'plan-id', resolutions: [] } as const;
    const submit = api.submitFileOperation(submission);
    const submitRequest = http.expectOne('/api/file-operations');
    expect(submitRequest.request.method).toBe('POST');
    expect(submitRequest.request.body).toEqual(submission);
    submitRequest.flush(fileOperationStatus());
    await expect(submit).resolves.toEqual(fileOperationStatus());

    const list = api.listFileOperations();
    const listRequest = http.expectOne('/api/file-operations');
    expect(listRequest.request.method).toBe('GET');
    listRequest.flush([fileOperationStatus()]);
    await expect(list).resolves.toEqual([fileOperationStatus()]);

    const operationId = 'operation/with spaces';
    const status = api.getFileOperation(operationId);
    const statusRequest = http.expectOne('/api/file-operations/operation%2Fwith%20spaces');
    expect(statusRequest.request.method).toBe('GET');
    statusRequest.flush(fileOperationStatus());
    await status;

    const cancel = api.cancelFileOperation(operationId);
    const cancelRequest = http.expectOne(
      '/api/file-operations/operation%2Fwith%20spaces/cancel',
    );
    expect(cancelRequest.request.method).toBe('POST');
    expect(cancelRequest.request.body).toBeNull();
    cancelRequest.flush(fileOperationStatus({ phase: 'cancelled' }));
    await cancel;

    const acknowledge = api.acknowledgeFileOperation(operationId);
    const acknowledgeRequest = http.expectOne('/api/file-operations/operation%2Fwith%20spaces');
    expect(acknowledgeRequest.request.method).toBe('DELETE');
    expect(acknowledgeRequest.request.body).toBeNull();
    acknowledgeRequest.flush(null, { status: 204, statusText: 'No Content' });
    await expect(acknowledge).resolves.toBeUndefined();
  });

  it('uses the directory and managed Trash lifecycle routes', async () => {
    const createBody = { sourceId: 'media', parentLogicalPath: '/Photos', name: 'Family' };
    const create = api.createDirectory(createBody);
    const createRequest = http.expectOne('/api/directories');
    expect(createRequest.request.method).toBe('POST');
    expect(createRequest.request.body).toEqual(createBody);
    createRequest.flush({ name: 'Family', relativePath: '/Photos/Family' });
    await create;

    const deleteBody: DeletePreviewRequestDto = {
      sourceId: 'media',
      logicalPaths: ['/Photos/photo.jpg'],
      mode: 'trash',
    };
    const deletePreview = api.previewDelete(deleteBody);
    const deletePreviewRequest = http.expectOne('/api/trash/preview-delete');
    expect(deletePreviewRequest.request.method).toBe('POST');
    expect(deletePreviewRequest.request.body).toEqual(deleteBody);
    deletePreviewRequest.flush({
      planId: 'delete-plan',
      expiresAt: '2026-08-25T12:10:00Z',
      mode: 'trash',
      trashAvailable: true,
      trashUnavailableReason: null,
      totalItems: 1,
      totalBytes: 5,
    });
    await deletePreview;

    const submitDelete = api.submitDelete({
      planId: 'delete-plan',
      permanentDeleteConfirmed: false,
    });
    const submitDeleteRequest = http.expectOne('/api/trash/delete');
    expect(submitDeleteRequest.request.method).toBe('POST');
    submitDeleteRequest.flush(fileOperationStatus({ kind: 'trash' }));
    await submitDelete;

    const list = api.listTrash('media library');
    const listRequest = http.expectOne(
      (candidate) =>
        candidate.url === '/api/trash' && candidate.params.get('sourceId') === 'media library',
    );
    expect(listRequest.request.method).toBe('GET');
    listRequest.flush([]);
    await expect(list).resolves.toEqual([]);

    const restoreBody: RestorePreviewRequestDto = { trashIds: ['trash-id'] };
    const restorePreview = api.previewRestore(restoreBody);
    const restorePreviewRequest = http.expectOne('/api/trash/preview-restore');
    expect(restorePreviewRequest.request.body).toEqual(restoreBody);
    restorePreviewRequest.flush({
      planId: 'restore-plan',
      expiresAt: '2026-08-25T12:10:00Z',
      entries: [],
      conflicts: [],
      parentsToCreate: [],
    });
    await restorePreview;

    const restore = api.submitRestore({ planId: 'restore-plan', resolutions: [] });
    const restoreRequest = http.expectOne('/api/trash/restore');
    expect(restoreRequest.request.method).toBe('POST');
    restoreRequest.flush(fileOperationStatus({ kind: 'restore' }));
    await restore;

    const permanent = api.permanentlyDeleteTrash({
      trashIds: ['trash-id'],
      permanentDeleteConfirmed: true,
    });
    const permanentRequest = http.expectOne('/api/trash/items');
    expect(permanentRequest.request.method).toBe('DELETE');
    expect(permanentRequest.request.body).toEqual({
      trashIds: ['trash-id'],
      permanentDeleteConfirmed: true,
    });
    permanentRequest.flush(fileOperationStatus({ kind: 'permanentDelete' }));
    await permanent;

    const empty = api.emptyTrash({ sourceId: 'media', permanentDeleteConfirmed: true });
    const emptyRequest = http.expectOne('/api/trash');
    expect(emptyRequest.request.method).toBe('DELETE');
    expect(emptyRequest.request.body).toEqual({
      sourceId: 'media',
      permanentDeleteConfirmed: true,
    });
    emptyRequest.flush(fileOperationStatus({ kind: 'emptyTrash' }));
    await empty;
  });
});

function fileOperationStatus(
  overrides: Partial<FileOperationStatusDto> = {},
): FileOperationStatusDto {
  return {
    operationId: 'operation-id',
    kind: 'copy',
    phase: 'queued',
    queuePosition: 1,
    createdAt: '2026-08-25T12:00:00Z',
    updatedAt: '2026-08-25T12:00:00Z',
    progress: {
      currentLogicalName: null,
      completedItems: 0,
      totalItems: 1,
      completedBytes: 0,
      totalBytes: 9,
      percentage: 0,
      bytesPerSecond: null,
      elapsed: '00:00:00',
      estimatedRemaining: null,
    },
    outcomes: [],
    warnings: [],
    acknowledged: false,
    ...overrides,
  };
}

function archivePreviewResponse(): ArchiveExtractionPreviewDto {
  return {
    planId: 'plan-id',
    expiresAt: '2026-08-20T08:10:00Z',
    format: 'sevenZip',
    volumeCount: 1,
    selectedRoots: ['2025'],
    fileCount: 1,
    directoryCount: 1,
    totalExtractedBytes: 12,
    destinationSourceId: 'archive disk',
    destinationPath: '/Photos & Videos',
    conflicts: [],
    violations: [],
    canExecute: true,
  };
}

function archiveOperationResponse(
  overrides: Partial<ArchiveExtractionOperationDto> = {},
): ArchiveExtractionOperationDto {
  return {
    operationId: 'operation-id',
    state: 'extracting',
    completedFiles: 0,
    totalFiles: 1,
    extractedBytes: 0,
    totalBytes: 12,
    percent: 0,
    currentEntryName: 'photo.jpg',
    canCancel: true,
    compensationState: 'notRequired',
    recoveryNames: [],
    errorCode: null,
    errorDetail: null,
    ...overrides,
  };
}

function previewRequest(): BatchRenamePreviewRequestDto {
  return {
    sourceId: 'media',
    directoryPath: '/Movies',
    entryPaths: ['/Movies/holiday.jpg'],
    rules: {
      nameMask: 'Archive-[C]',
      extensionMask: '[E]',
      searchFor: '',
      replaceWith: '',
      useRegex: false,
      matchCase: false,
      replaceInExtension: false,
      caseMode: 'unchanged',
      counterStart: 1,
      counterStep: 1,
      counterDigits: 3,
    },
  };
}

function previewResponse(): BatchRenamePreviewDto {
  return {
    planId: '11111111-1111-4111-8111-111111111111',
    expiresAt: '2026-08-20T08:10:00Z',
    rows: [
      {
        sourcePath: '/Movies/holiday.jpg',
        oldName: 'holiday.jpg',
        oldExtension: 'jpg',
        newName: 'Archive-001.jpg',
        type: 'file',
        size: 12,
        modifiedAt: '2026-08-20T08:00:00Z',
        status: 'ready',
        message: null,
      },
    ],
    canExecute: true,
    changedCount: 1,
    unchangedCount: 0,
    invalidCount: 0,
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

function systemMetricsResponse(): SystemMetricsDto {
  return {
    sampledAt: '2026-08-19T12:00:00Z',
    state: 'healthy',
    hostUptimeSeconds: 3600,
    cpu: {
      utilizationPercent: 25,
      temperatureCelsius: 55,
      warningTemperatureCelsius: 90,
      criticalTemperatureCelsius: 100,
      alarm: false,
      fault: false,
    },
    memory: {
      usedBytes: 60,
      availableBytes: 40,
      totalBytes: 100,
      utilizationPercent: 60,
    },
    storage: [],
    gpus: [],
    fans: [],
    network: null,
    collectors: [],
  };
}

function systemUpdateStatus(
  overrides: Partial<SystemUpdateStatusDto> = {},
): SystemUpdateStatusDto {
  return {
    protocolVersion: 1,
    supported: true,
    channel: 'stable',
    currentVersion: 'v1.3.0',
    targetVersion: null,
    phase: 'current',
    progressStage: null,
    updateAvailable: false,
    canApply: false,
    reasonCode: 'up_to_date',
    detail: 'ReachCommander is up to date.',
    operationId: null,
    lastCheckedAt: '2026-08-25T10:00:00Z',
    updatedAt: '2026-08-25T10:00:00Z',
    ...overrides,
  };
}
