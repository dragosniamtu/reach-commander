import { TestBed } from '@angular/core/testing';
import { HttpErrorResponse } from '@angular/common/http';
import {
  CommanderApiPort,
  SourceAddRequestDto,
  SourceDto,
  SourceManagementCapabilityDto,
  SourceManagementOperationDto,
} from '../api/api.models';
import { ProtectedStateResetService } from '../auth/protected-state-reset.service';
import { CommanderStore } from './commander-store';
import {
  SOURCE_MANAGEMENT_DEADLINE_TIMER,
  SOURCE_MANAGEMENT_SCHEDULER,
  SourceManagementDeadlineTimer,
  SourceManagementScheduler,
  SourceManagementStore,
} from './source-management.store';

describe('SourceManagementStore', () => {
  let api: FakeSourceManagementApi;
  let deadline: ManualDeadline;
  let scheduler: ManualScheduler;
  let commander: { reloadSourceCatalog: ReturnType<typeof vi.fn> };
  let protectedState: ProtectedStateResetService;
  let store: SourceManagementStore;

  beforeEach(() => {
    api = new FakeSourceManagementApi();
    deadline = new ManualDeadline();
    scheduler = new ManualScheduler();
    commander = {
      reloadSourceCatalog: vi.fn(() => Promise.resolve([source('family-media')])),
    };
    TestBed.configureTestingModule({
      providers: [
        SourceManagementStore,
        ProtectedStateResetService,
        { provide: CommanderApiPort, useValue: api },
        { provide: CommanderStore, useValue: commander },
        { provide: SOURCE_MANAGEMENT_DEADLINE_TIMER, useValue: deadline },
        { provide: SOURCE_MANAGEMENT_SCHEDULER, useValue: scheduler },
      ],
    });
    protectedState = TestBed.inject(ProtectedStateResetService);
    store = TestBed.inject(SourceManagementStore);
  });

  it('loads support capability once and exposes the precise unsupported reason', async () => {
    api.capability = {
      supported: false,
      reasonCode: 'installer_upgrade_required',
      detail: 'Rerun the latest Ubuntu installer once to add host source management.',
    };

    await store.start();
    await store.start();

    expect(api.statusCount).toBe(1);
    expect(store.capability()).toEqual(api.capability);
    expect(store.canOpen()).toBe(false);
    expect(store.disabledReason()).toContain('Rerun the latest Ubuntu installer once');
  });

  it('allows a transient capability failure to be retried in the same session', async () => {
    api.statusResults.push(
      () => Promise.reject(new HttpErrorResponse({
        status: 503,
        error: {
          code: 'source_management_unavailable',
          detail: 'Source-management capability could not be loaded.',
        },
      })),
      () => Promise.resolve({
        supported: true,
        reasonCode: 'supported',
        detail: 'Source management is available.',
      }),
    );

    await store.start();
    expect(store.capability()).toBeNull();
    expect(store.disabledReason()).toContain('could not be loaded');
    expect(store.canRetryCapability()).toBe(true);

    await store.start();

    expect(api.statusCount).toBe(2);
    expect(store.capability()?.supported).toBe(true);
    expect(store.error()).toBeNull();
    expect(store.canRetryCapability()).toBe(false);
  });

  it('times out a never-settling capability read and ignores its late completion', async () => {
    const hanging = deferred<SourceManagementCapabilityDto>();
    api.statusResults.push(() => hanging.promise);

    const startup = store.start();
    await flushMicrotasks();
    expect(store.capabilityPending()).toBe(true);
    expect(deadline.pendingCount).toBe(1);

    deadline.expireNext();
    await startup;

    expect(store.capabilityPending()).toBe(false);
    expect(store.capability()).toBeNull();
    expect(store.canRetryCapability()).toBe(true);
    expect(store.error()?.code).toBe('source_management_capability_timeout');

    hanging.resolve({
      supported: false,
      reasonCode: 'late_unsupported',
      detail: 'This late response must not replace current state.',
    });
    await flushMicrotasks();
    expect(store.capability()).toBeNull();

    await store.start();
    expect(api.statusCount).toBe(2);
    expect(store.capability()?.supported).toBe(true);
  });

  it('polls an accepted operation to completion and refreshes the shared source catalog', async () => {
    const request: SourceAddRequestDto = {
      displayName: 'Family media', hostPath: '/srv/media/family', access: 'readOnly',
    };
    api.addResult = operation({ phase: 'accepted' });
    api.operationResults.push(
      () => Promise.resolve(operation({ phase: 'restarting' })),
      () => Promise.resolve(operation({ phase: 'completed', sourceId: 'family-media' })),
    );
    await store.start();
    store.open();

    await store.submit(request);

    expect(api.addRequests).toEqual([request]);
    expect(store.operation()?.phase).toBe('accepted');
    expect(scheduler.pendingCount).toBe(1);
    await scheduler.runNext();
    expect(store.operation()?.phase).toBe('restarting');
    await scheduler.runNext();

    expect(store.operation()?.phase).toBe('completed');
    expect(commander.reloadSourceCatalog).toHaveBeenCalledOnce();
    expect(store.catalogRefreshed()).toBe(true);
    expect(scheduler.pendingCount).toBe(0);
  });

  it('keeps the blocking dialog active until a completed operation reloads the catalog', async () => {
    const refresh = deferred<readonly SourceDto[]>();
    commander.reloadSourceCatalog.mockImplementationOnce(() => refresh.promise);
    api.addResult = operation({ phase: 'accepted' });
    api.operationResults.push(
      () => Promise.resolve(operation({ phase: 'completed', sourceId: 'family-media' })),
    );
    await store.start();
    store.open();
    await store.submit(sourceRequest());

    const completion = scheduler.runNext();
    await Promise.resolve();
    await Promise.resolve();
    expect(store.operation()?.phase).toBe('completed');
    expect(store.pending()).toBe(true);
    store.close();
    expect(store.dialogOpen()).toBe(true);

    refresh.resolve([source('family-media')]);
    await completion;
    expect(store.pending()).toBe(false);
    expect(store.catalogRefreshed()).toBe(true);
  });

  it('retries fresh catalog replacement until the generated source ID appears', async () => {
    commander.reloadSourceCatalog
      .mockResolvedValueOnce([source('downloads')])
      .mockResolvedValueOnce([source('downloads'), source('family-media')]);
    api.addResult = operation({ phase: 'accepted' });
    api.operationResults.push(
      () => Promise.resolve(operation({ phase: 'completed', sourceId: 'family-media' })),
    );
    await store.start();
    store.open();
    await store.submit(sourceRequest());

    await scheduler.runNext();

    expect(store.catalogRefreshed()).toBe(false);
    expect(store.pending()).toBe(true);
    expect(scheduler.pendingCount).toBe(1);
    await scheduler.runNext();

    expect(store.catalogRefreshed()).toBe(true);
    expect(store.pending()).toBe(false);
    expect(commander.reloadSourceCatalog).toHaveBeenCalledTimes(2);
  });

  it('stops bounded catalog retries with an actionable public error', async () => {
    commander.reloadSourceCatalog.mockResolvedValue([source('downloads')]);
    api.addResult = operation({ phase: 'accepted' });
    api.operationResults.push(
      () => Promise.resolve(operation({ phase: 'completed', sourceId: 'family-media' })),
    );
    await store.start();
    store.open();
    await store.submit(sourceRequest());

    await scheduler.runNext();
    for (let attempt = 1; attempt < 12; attempt++) {
      await scheduler.runNext();
    }

    expect(store.catalogRefreshed()).toBe(false);
    expect(store.pending()).toBe(false);
    expect(store.error()?.code).toBe('source_management_catalog_refresh_timeout');
    expect(store.error()?.detail).toMatch(/refresh the page/i);
    expect(store.error()?.detail).toContain('reachcommander doctor');
    expect(scheduler.pendingCount).toBe(0);
  });

  it('retains operation context while disconnected and reconnects without resubmitting', async () => {
    api.addResult = operation({ phase: 'accepted' });
    api.operationResults.push(
      () => Promise.reject(new HttpErrorResponse({ status: 0, statusText: 'Offline' })),
      () => Promise.resolve(operation({ phase: 'healthChecking' })),
    );
    await store.start();
    store.open();
    await store.submit(sourceRequest());

    await scheduler.runNext();
    expect(store.reconnecting()).toBe(true);
    expect(store.operation()?.phase).toBe('accepted');
    await scheduler.runNext();

    expect(store.reconnecting()).toBe(false);
    expect(store.operation()?.phase).toBe('healthChecking');
    expect(api.addRequests).toHaveLength(1);
  });

  it('routes a never-settling operation read through reconnect without accepting a late result', async () => {
    const hanging = deferred<SourceManagementOperationDto>();
    api.addResult = operation({ phase: 'accepted' });
    api.operationResults.push(
      () => hanging.promise,
      () => Promise.resolve(operation({ phase: 'healthChecking' })),
    );
    await store.start();
    store.open();
    await store.submit(sourceRequest());

    const timedAttempt = scheduler.runNext();
    await flushMicrotasks();
    expect(deadline.pendingCount).toBe(1);
    deadline.expireNext();
    await timedAttempt;

    expect(store.reconnecting()).toBe(true);
    expect(store.operation()?.phase).toBe('accepted');
    expect(scheduler.pendingCount).toBe(1);

    hanging.resolve(operation({ phase: 'failed', reasonCode: 'late_failure' }));
    await flushMicrotasks();
    expect(store.operation()?.phase).toBe('accepted');

    await scheduler.runNext();
    expect(store.reconnecting()).toBe(false);
    expect(store.operation()?.phase).toBe('healthChecking');
    expect(api.addRequests).toHaveLength(1);
  });

  it('routes a never-settling catalog read through its independent refresh retry', async () => {
    const hanging = deferred<readonly SourceDto[]>();
    commander.reloadSourceCatalog
      .mockImplementationOnce(() => hanging.promise)
      .mockResolvedValueOnce([source('downloads'), source('family-media')]);
    api.addResult = operation({ phase: 'completed', sourceId: 'family-media' });
    await store.start();
    store.open();

    const submission = store.submit(sourceRequest());
    await flushMicrotasks();
    expect(deadline.pendingCount).toBe(1);
    deadline.expireNext();
    await submission;

    expect(store.pending()).toBe(true);
    expect(store.reconnecting()).toBe(false);
    expect(store.catalogRefreshed()).toBe(false);
    expect(scheduler.pendingCount).toBe(1);

    hanging.reject(new Error('late private catalog failure'));
    await flushMicrotasks();
    expect(store.error()).toBeNull();

    await scheduler.runNext();
    expect(store.catalogRefreshed()).toBe(true);
    expect(store.pending()).toBe(false);
    expect(commander.reloadSourceCatalog).toHaveBeenCalledTimes(2);
  });

  it.each(['rolledBack', 'failed'] as const)(
    'keeps a terminal %s result and its bounded public detail visible',
    async (phase) => {
      api.addResult = operation({ phase: 'accepted' });
      api.operationResults.push(() => Promise.resolve(operation({
        phase,
        reasonCode: `source_${phase}`,
        detail: phase === 'rolledBack'
          ? 'The previous source configuration was restored.'
          : 'The source-management operation could not be completed.',
      })));
      await store.start();
      store.open();
      await store.submit(sourceRequest());
      await scheduler.runNext();

      expect(store.operation()?.phase).toBe(phase);
      expect(store.terminal()).toBe(true);
      expect(store.catalogRefreshed()).toBe(false);
      expect(store.operation()?.detail).not.toContain('/srv');
    },
  );

  it('stops reconnect polling with actionable timeout guidance', async () => {
    api.addResult = operation({ phase: 'accepted' });
    for (let attempt = 0; attempt < 24; attempt++) {
      api.operationResults.push(
        () => Promise.reject(new HttpErrorResponse({ status: 0, statusText: 'Offline' })),
      );
    }
    await store.start();
    store.open();
    await store.submit(sourceRequest());

    for (let attempt = 0; attempt < 24; attempt++) {
      await scheduler.runNext();
    }

    expect(store.terminal()).toBe(true);
    expect(store.error()?.code).toBe('source_management_reconnect_timeout');
    expect(store.error()?.detail).toContain('reachcommander doctor');
    expect(scheduler.pendingCount).toBe(0);
  });

  it('prevents duplicate submissions and exposes only API problem details', async () => {
    let rejectAdd!: (error: unknown) => void;
    api.addPromise = new Promise((_, reject) => { rejectAdd = reject; });
    await store.start();
    store.open();

    const first = store.submit(sourceRequest());
    const second = store.submit(sourceRequest());
    expect(api.addRequests).toHaveLength(1);
    expect(deadline.pendingCount).toBe(0);
    rejectAdd(new HttpErrorResponse({
      status: 400,
      error: {
        code: 'source_management_validation_failed',
        detail: 'Choose a more specific existing host folder.',
        privateDiagnostic: '/opt/reachcommander/compose.yaml',
      },
    }));
    await Promise.all([first, second]);

    expect(store.error()).toEqual({
      code: 'source_management_validation_failed',
      detail: 'Choose a more specific existing host folder.',
    });
    expect(JSON.stringify(store.state())).not.toContain('/opt/reachcommander');
  });

  it('clears capability, operation, and timers with protected state', async () => {
    api.addResult = operation({ phase: 'accepted' });
    await store.start();
    store.open();
    await store.submit(sourceRequest());

    protectedState.reset();

    expect(store.capability()).toBeNull();
    expect(store.operation()).toBeNull();
    expect(store.dialogOpen()).toBe(false);
    expect(scheduler.pendingCount).toBe(0);
    expect(deadline.pendingCount).toBe(0);
  });

  it('cancels a pending read deadline on protected reset and ignores late rejection', async () => {
    const hanging = deferred<SourceManagementCapabilityDto>();
    api.statusResults.push(() => hanging.promise);
    const startup = store.start();
    await flushMicrotasks();
    expect(deadline.pendingCount).toBe(1);

    protectedState.reset();
    expect(deadline.pendingCount).toBe(0);
    await startup;
    expect(store.state()).toEqual(expect.objectContaining({
      capability: null,
      capabilityPending: false,
      error: null,
    }));

    hanging.reject(new Error('late private capability failure'));
    await flushMicrotasks();
    expect(store.error()).toBeNull();
  });

  it('cancels a pending read deadline when the store is destroyed', async () => {
    const hanging = deferred<SourceManagementCapabilityDto>();
    api.statusResults.push(() => hanging.promise);
    const startup = store.start();
    await flushMicrotasks();
    expect(deadline.pendingCount).toBe(1);

    TestBed.resetTestingModule();
    expect(deadline.pendingCount).toBe(0);
    await startup;

    hanging.resolve({
      supported: false,
      reasonCode: 'late_destroyed_response',
      detail: 'This response belongs to a destroyed store.',
    });
    await flushMicrotasks();
    expect(store.capability()).toBeNull();
  });
});

class FakeSourceManagementApi {
  capability: SourceManagementCapabilityDto = {
    supported: true, reasonCode: 'supported', detail: 'Source management is available.',
  };
  addResult: SourceManagementOperationDto = operation();
  addPromise: Promise<SourceManagementOperationDto> | null = null;
  readonly addRequests: SourceAddRequestDto[] = [];
  readonly operationResults: Array<() => Promise<SourceManagementOperationDto>> = [];
  readonly statusResults: Array<() => Promise<SourceManagementCapabilityDto>> = [];
  statusCount = 0;

  getSourceManagementStatus(): Promise<SourceManagementCapabilityDto> {
    this.statusCount++;
    return this.statusResults.shift()?.() ?? Promise.resolve(this.capability);
  }

  addSource(request: SourceAddRequestDto): Promise<SourceManagementOperationDto> {
    this.addRequests.push(request);
    return this.addPromise ?? Promise.resolve(this.addResult);
  }

  getSourceManagementOperation(): Promise<SourceManagementOperationDto> {
    return this.operationResults.shift()?.() ?? Promise.resolve(this.addResult);
  }
}

function source(id: string): SourceDto {
  return {
    id,
    name: id,
    isAvailable: true,
    isReadOnly: false,
    totalBytes: 100,
    usedBytes: 25,
    freeBytes: 75,
    defaultLeft: false,
    defaultRight: false,
  };
}

class ManualScheduler implements SourceManagementScheduler {
  private tasks: Array<{ callback: () => Promise<void> | void; delay: number }> = [];
  readonly delays: number[] = [];

  get pendingCount(): number { return this.tasks.length; }

  schedule(callback: () => Promise<void> | void, delayMilliseconds: number): unknown {
    const task = { callback, delay: delayMilliseconds };
    this.tasks.push(task);
    this.delays.push(delayMilliseconds);
    return task;
  }

  cancel(handle: unknown): void {
    this.tasks = this.tasks.filter((task) => task !== handle);
  }

  async runNext(): Promise<void> {
    const task = this.tasks.shift();
    if (!task) throw new Error('No scheduled source-management poll.');
    await task.callback();
  }
}

class ManualDeadline implements SourceManagementDeadlineTimer {
  private requests: Array<{ callback: () => void }> = [];

  get pendingCount(): number { return this.requests.length; }

  schedule(callback: () => void, _delayMilliseconds: number): unknown {
    const request = { callback };
    this.requests.push(request);
    return request;
  }

  expireNext(): void {
    const request = this.requests.shift();
    if (!request) throw new Error('No pending source-management read deadline.');
    request.callback();
  }

  cancel(handle: unknown): void {
    this.requests = this.requests.filter((request) => request !== handle);
  }
}

function sourceRequest(): SourceAddRequestDto {
  return { displayName: 'Family media', hostPath: '/srv/media/family', access: 'readOnly' };
}

function operation(
  overrides: Partial<SourceManagementOperationDto> = {},
): SourceManagementOperationDto {
  return {
    operationId: '33333333-3333-4333-8333-333333333333',
    sourceId: null,
    displayName: 'Family media',
    phase: 'accepted',
    reasonCode: 'accepted',
    detail: 'The source-management operation was accepted.',
    createdAt: '2026-08-31T08:00:00Z',
    updatedAt: '2026-08-31T08:00:00Z',
    ...overrides,
  };
}

function deferred<T>() {
  let resolve!: (value: T | PromiseLike<T>) => void;
  let reject!: (reason?: unknown) => void;
  const promise = new Promise<T>((done, fail) => {
    resolve = done;
    reject = fail;
  });
  return { promise, resolve, reject };
}

async function flushMicrotasks(): Promise<void> {
  await Promise.resolve();
  await Promise.resolve();
}
