import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ReachCommanderApi } from './reach-commander-api';
import { SystemMetricsDto } from './api.models';

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
      (candidate) => candidate.url === '/api/files' &&
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
