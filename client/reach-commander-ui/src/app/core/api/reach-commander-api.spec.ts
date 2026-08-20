import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ReachCommanderApi } from './reach-commander-api';
import { HttpEventType } from '@angular/common/http';
import { firstValueFrom, toArray } from 'rxjs';
import { SystemMetricsDto, UploadLimitsDto, UploadResultDto } from './api.models';

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
});

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
