import { TestBed } from '@angular/core/testing';
import { CommanderApiPort, SystemUpdateStatusDto } from '../api/api.models';
import { ProtectedStateResetService } from '../auth/protected-state-reset.service';
import { PwaService } from '../pwa/pwa.service';
import {
  SYSTEM_UPDATE_SCHEDULER,
  SYSTEM_UPDATE_RESULT_STORAGE,
  SystemUpdateScheduler,
  SystemUpdateStore,
} from './system-update.store';

describe('SystemUpdateStore', () => {
  let api: FakeSystemUpdateApi;
  let scheduler: ManualScheduler;
  let pwa: { refreshAfterSystemUpdate: ReturnType<typeof vi.fn> };
  let protectedState: ProtectedStateResetService;
  let resultStorage: MemoryResultStorage;
  let store: SystemUpdateStore;

  beforeEach(() => {
    api = new FakeSystemUpdateApi();
    scheduler = new ManualScheduler();
    pwa = { refreshAfterSystemUpdate: vi.fn(() => Promise.resolve()) };
    resultStorage = new MemoryResultStorage();
    TestBed.configureTestingModule({
      providers: [
        SystemUpdateStore,
        ProtectedStateResetService,
        { provide: CommanderApiPort, useValue: api },
        { provide: SYSTEM_UPDATE_SCHEDULER, useValue: scheduler },
        { provide: SYSTEM_UPDATE_RESULT_STORAGE, useValue: resultStorage },
        { provide: PwaService, useValue: pwa },
      ],
    });
    protectedState = TestBed.inject(ProtectedStateResetService);
    store = TestBed.inject(SystemUpdateStore);
  });

  it('loads cached status then performs one fresh check without a six-hour browser timer', async () => {
    api.getResults.push(() => Promise.resolve(status({ phase: 'current' })));
    api.checkResult = status({
      phase: 'available',
      targetVersion: 'v1.4.0',
      updateAvailable: true,
      canApply: true,
    });

    await store.start();

    expect(api.getCount).toBe(1);
    expect(api.checkCount).toBe(1);
    expect(store.status()?.phase).toBe('available');
    expect(store.canApply()).toBe(true);
    expect(scheduler.pendingCount).toBe(0);
  });

  it('retains applying state across disconnects and completes after server recovery', async () => {
    api.applyResult = status({
      phase: 'applying',
      operationId: 'operation-1',
      targetVersion: 'v1.4.0',
      updateAvailable: true,
    });
    store.capture(status({
      phase: 'available',
      targetVersion: 'v1.4.0',
      updateAvailable: true,
      canApply: true,
    }));
    await store.apply();
    api.getResults.push(
      () => Promise.reject(new TypeError('Failed to fetch')),
      () => Promise.resolve(status({
        phase: 'completed',
        currentVersion: 'v1.3.0',
        targetVersion: 'v1.4.0',
        operationId: 'operation-1',
      })),
    );

    await scheduler.runNext();
    expect(store.reconnecting()).toBe(true);
    expect(store.status()?.phase).toBe('applying');
    await scheduler.runNext();

    expect(store.status()?.phase).toBe('completed');
    expect(pwa.refreshAfterSystemUpdate).toHaveBeenCalledOnce();
    expect(scheduler.pendingCount).toBe(0);
    expect(resultStorage.getItem('reachcommander.systemUpdateRefreshed')).toBe('operation-1');
  });

  it('treats a disconnect during Apply as expected and never sends a second Apply', async () => {
    api.applyError = new TypeError('Failed to fetch');
    store.capture(status({
      phase: 'available',
      targetVersion: 'v1.4.0',
      updateAvailable: true,
      canApply: true,
    }));

    await store.apply();

    expect(api.applyCount).toBe(1);
    expect(store.status()?.phase).toBe('applying');
    expect(store.reconnecting()).toBe(true);
    expect(scheduler.pendingCount).toBe(1);
  });

  it('caps exponential reconnect delays', async () => {
    api.applyResult = status({
      phase: 'applying',
      targetVersion: 'v1.4.0',
      operationId: 'operation-1',
    });
    store.capture(status({
      phase: 'available',
      targetVersion: 'v1.4.0',
      updateAvailable: true,
      canApply: true,
    }));
    await store.apply();
    for (let index = 0; index < 8; index++) {
      api.getResults.push(() => Promise.reject(new TypeError('offline')));
      await scheduler.runNext();
    }

    expect(Math.max(...scheduler.delays)).toBe(15_000);
    expect(scheduler.delays).toEqual(expect.arrayContaining([1_000, 2_000, 4_000, 8_000, 15_000]));
  });

  it.each(['rolledBack', 'failed'] as const)(
    'retains %s until dismissed',
    async (phase) => {
      store.capture(status({ phase, operationId: 'operation-1' }));

      expect(store.status()?.phase).toBe(phase);
      store.dismissTerminal();
      expect(store.status()?.phase).toBe(phase);
      expect(store.overlayVisible()).toBe(false);
    },
  );

  it('resets protected UI state without calling Apply or a host cancellation', () => {
    store.capture(status({
      phase: 'applying',
      targetVersion: 'v1.4.0',
      operationId: 'operation-1',
    }));

    protectedState.reset();

    expect(store.status()).toBeNull();
    expect(api.applyCount).toBe(0);
    expect(scheduler.pendingCount).toBe(0);
  });

  it('cleans its single timer up on destroy', async () => {
    api.applyResult = status({
      phase: 'applying',
      targetVersion: 'v1.4.0',
      operationId: 'operation-1',
    });
    store.capture(status({
      phase: 'available',
      targetVersion: 'v1.4.0',
      updateAvailable: true,
      canApply: true,
    }));
    await store.apply();

    TestBed.resetTestingModule();

    expect(scheduler.cancelCount).toBeGreaterThan(0);
  });

  it('does not block or reload again for a completion already refreshed in this tab', () => {
    resultStorage.setItem('reachcommander.systemUpdateRefreshed', 'operation-1');

    store.capture(status({
      phase: 'completed',
      operationId: 'operation-1',
      targetVersion: 'v1.4.0',
    }));

    expect(store.overlayVisible()).toBe(false);
    expect(store.status()?.phase).toBe('completed');
    expect(pwa.refreshAfterSystemUpdate).not.toHaveBeenCalled();
  });
});

class FakeSystemUpdateApi {
  readonly getResults: Array<() => Promise<SystemUpdateStatusDto>> = [];
  checkResult = status({ phase: 'current' });
  applyResult = status({
    phase: 'applying',
    targetVersion: 'v1.4.0',
    operationId: 'operation-1',
  });
  applyError: unknown = null;
  getCount = 0;
  checkCount = 0;
  applyCount = 0;

  getSystemUpdate(): Promise<SystemUpdateStatusDto> {
    this.getCount++;
    return (this.getResults.shift() ?? (() => Promise.resolve(this.checkResult)))();
  }

  checkSystemUpdate(): Promise<SystemUpdateStatusDto> {
    this.checkCount++;
    return Promise.resolve(this.checkResult);
  }

  applySystemUpdate(): Promise<SystemUpdateStatusDto> {
    this.applyCount++;
    return this.applyError === null
      ? Promise.resolve(this.applyResult)
      : Promise.reject(this.applyError);
  }
}

class ManualScheduler implements SystemUpdateScheduler {
  private readonly tasks: Array<{ callback: () => Promise<void> | void; handle: object }> = [];
  readonly delays: number[] = [];
  cancelCount = 0;

  get pendingCount(): number {
    return this.tasks.length;
  }

  schedule(callback: () => Promise<void> | void, delayMilliseconds: number): unknown {
    const handle = {};
    this.tasks.push({ callback, handle });
    this.delays.push(delayMilliseconds);
    return handle;
  }

  cancel(handle: unknown): void {
    this.cancelCount++;
    const index = this.tasks.findIndex((task) => task.handle === handle);
    if (index >= 0) {
      this.tasks.splice(index, 1);
    }
  }

  async runNext(): Promise<void> {
    const next = this.tasks.shift();
    if (!next) {
      throw new Error('No scheduled system update callback.');
    }

    await next.callback();
  }
}

class MemoryResultStorage {
  private readonly values = new Map<string, string>();

  getItem(key: string): string | null {
    return this.values.get(key) ?? null;
  }

  setItem(key: string, value: string): void {
    this.values.set(key, value);
  }
}

function status(overrides: Partial<SystemUpdateStatusDto>): SystemUpdateStatusDto {
  return {
    protocolVersion: 1,
    supported: true,
    channel: 'stable',
    currentVersion: 'v1.3.0',
    targetVersion: null,
    phase: 'current',
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
