import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ReachCommanderApi } from './reach-commander-api';
import { HttpEventType } from '@angular/common/http';
import { firstValueFrom, toArray } from 'rxjs';
import {
  BatchRenameOperationDto,
  BatchRenamePreviewDto,
  BatchRenamePreviewRequestDto,
  SystemMetricsDto,
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
});

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
