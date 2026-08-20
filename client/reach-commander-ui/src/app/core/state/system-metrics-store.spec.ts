import { HttpErrorResponse } from '@angular/common/http';
import { EMPTY, Observable } from 'rxjs';
import {
  CommanderApiPort,
  FileEntryDto,
  SourceDto,
  SystemMetricsDto,
  UploadEvent,
  UploadLimitsDto,
} from '../api/api.models';
import { SystemMetricsStore } from './system-metrics-store';

describe('SystemMetricsStore', () => {
  let api: FakeMetricsApi;
  let store: SystemMetricsStore;

  beforeEach(() => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date('2026-08-19T12:00:00Z'));
    api = new FakeMetricsApi();
    store = new SystemMetricsStore(api);
  });

  afterEach(() => {
    store.stop();
    vi.useRealTimers();
  });

  it('loads immediately, then polls once every five seconds without overlap', async () => {
    const first = deferred<SystemMetricsDto>();
    api.metricsHandler = () => first.promise;
    store.start();

    expect(api.metricsRequests).toBe(1);
    await vi.advanceTimersByTimeAsync(10_000);
    expect(api.metricsRequests).toBe(1);

    first.resolve(systemMetricsResponse());
    await Promise.resolve();
    await vi.advanceTimersByTimeAsync(4_999);
    expect(api.metricsRequests).toBe(1);
    await vi.advanceTimersByTimeAsync(1);
    expect(api.metricsRequests).toBe(2);
  });

  it('preserves the last snapshot and derives stale after fifteen seconds of failures', async () => {
    api.metricsHandler = () =>
      Promise.resolve(
        systemMetricsResponse({
          sampledAt: '2026-08-19T12:00:00Z',
          state: 'healthy',
        }),
      );
    store.start();
    await Promise.resolve();

    api.metricsHandler = () => Promise.reject(new Error('offline'));
    await vi.advanceTimersByTimeAsync(16_000);

    expect(store.effectiveSnapshot()).not.toBeNull();
    expect(store.effectiveState()).toBe('stale');
    expect(store.state().errorCode).toBe('request_failed');
  });

  it('queues one immediate refresh on visibility return without overlapping the in-flight request', async () => {
    const first = deferred<SystemMetricsDto>();
    const second = deferred<SystemMetricsDto>();
    api.metricsHandler = () => (api.metricsRequests === 1 ? first.promise : second.promise);
    store.start();
    Object.defineProperty(document, 'visibilityState', { configurable: true, value: 'visible' });
    document.dispatchEvent(new Event('visibilitychange'));

    expect(api.metricsRequests).toBe(1);
    first.resolve(systemMetricsResponse({ sampledAt: '2026-08-19T11:59:55Z' }));
    await Promise.resolve();
    await Promise.resolve();
    expect(api.metricsRequests).toBe(2);

    second.resolve(systemMetricsResponse({ sampledAt: '2026-08-19T12:00:05Z' }));
    await Promise.resolve();
    await Promise.resolve();

    expect(store.state().snapshot?.sampledAt).toBe('2026-08-19T12:00:05Z');
  });

  it('discards a response from a stopped polling lifecycle', async () => {
    const late = deferred<SystemMetricsDto>();
    api.metricsHandler = () => late.promise;
    store.start();
    store.stop();
    late.resolve(systemMetricsResponse({ sampledAt: '2026-08-19T11:59:55Z' }));
    await Promise.resolve();

    expect(store.state().snapshot).toBeNull();
  });

  it('maps only the safe not-ready problem code and preserves the previous snapshot', async () => {
    api.metricsHandler = () => Promise.resolve(systemMetricsResponse());
    store.start();
    await Promise.resolve();
    await Promise.resolve();

    api.metricsHandler = () =>
      Promise.reject(
        new HttpErrorResponse({
          status: 503,
          error: { code: 'metrics_not_ready', detail: 'must not be displayed' },
        }),
      );
    await vi.advanceTimersByTimeAsync(5_000);

    expect(store.state().snapshot).not.toBeNull();
    expect(store.state().errorCode).toBe('metrics_not_ready');
  });
});

class FakeMetricsApi extends CommanderApiPort {
  async listArchive(): Promise<never> {
    throw new Error('Not used by these tests');
  }
  metricsRequests = 0;
  metricsHandler: () => Promise<SystemMetricsDto> = () => Promise.resolve(systemMetricsResponse());

  getSystemMetrics(): Promise<SystemMetricsDto> {
    this.metricsRequests++;
    return this.metricsHandler();
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
    return { maxFileBytes: 10, maxBatchBytes: 20, maxFilesPerBatch: 2 };
  }

  uploadFiles(): Observable<UploadEvent> {
    return EMPTY;
  }

  async previewBatchRename(): Promise<never> {
    throw new Error('Not used by this test');
  }

  async executeBatchRename(): Promise<never> {
    throw new Error('Not used by this test');
  }

  async undoBatchRename(): Promise<never> {
    throw new Error('Not used by this test');
  }
}

function deferred<T>(): {
  readonly promise: Promise<T>;
  readonly resolve: (value: T) => void;
} {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((accept) => {
    resolve = accept;
  });
  return { promise, resolve };
}

function systemMetricsResponse(overrides: Partial<SystemMetricsDto> = {}): SystemMetricsDto {
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
    ...overrides,
  };
}
