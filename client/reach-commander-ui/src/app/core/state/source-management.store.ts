import { HttpErrorResponse } from '@angular/common/http';
import {
  DestroyRef,
  Inject,
  Injectable,
  InjectionToken,
  computed,
  signal,
} from '@angular/core';
import {
  CommanderApiPort,
  SourceAddRequestDto,
  SourceDto,
  SourceManagementCapabilityDto,
  SourceManagementOperationDto,
} from '../api/api.models';
import { ProtectedStateResetService } from '../auth/protected-state-reset.service';
import { CommanderStore } from './commander-store';

const initialPollMilliseconds = 1_000;
const maximumPollMilliseconds = 10_000;
const maximumReconnectAttempts = 24;
const maximumCatalogRefreshAttempts = 12;
const readRequestTimeoutMilliseconds = 15_000;

export interface SourceManagementScheduler {
  schedule(callback: () => Promise<void> | void, delayMilliseconds: number): unknown;
  cancel(handle: unknown): void;
}

export interface SourceManagementDeadlineTimer {
  schedule(callback: () => void, delayMilliseconds: number): unknown;
  cancel(handle: unknown): void;
}

class SourceManagementDeadlineExceededError extends Error {
  constructor() {
    super('The source-management read deadline was exceeded.');
    this.name = 'SourceManagementDeadlineExceededError';
  }
}

class SourceManagementDeadlineCancelledError extends Error {
  constructor() {
    super('The source-management read deadline was cancelled.');
    this.name = 'SourceManagementDeadlineCancelledError';
  }
}

interface PendingReadDeadline {
  handle?: unknown;
  cancel(): void;
}

export const SOURCE_MANAGEMENT_DEADLINE_TIMER = new InjectionToken<SourceManagementDeadlineTimer>(
  'SOURCE_MANAGEMENT_DEADLINE_TIMER',
  {
    providedIn: 'root',
    factory: () => ({
      schedule: (callback, delay) => setTimeout(callback, delay),
      cancel: (handle) => clearTimeout(handle as ReturnType<typeof setTimeout>),
    }),
  },
);

export const SOURCE_MANAGEMENT_SCHEDULER = new InjectionToken<SourceManagementScheduler>(
  'SOURCE_MANAGEMENT_SCHEDULER',
  {
    providedIn: 'root',
    factory: () => ({
      schedule: (callback, delay) => setTimeout(() => void callback(), delay),
      cancel: (handle) => clearTimeout(handle as ReturnType<typeof setTimeout>),
    }),
  },
);

export interface SourceManagementClientError {
  readonly code: string;
  readonly detail: string;
}

export interface SourceManagementStoreState {
  readonly capability: SourceManagementCapabilityDto | null;
  readonly capabilityPending: boolean;
  readonly dialogOpen: boolean;
  readonly mode: 'add' | 'remove';
  readonly removalSource: SourceDto | null;
  readonly operation: SourceManagementOperationDto | null;
  readonly pending: boolean;
  readonly reconnecting: boolean;
  readonly catalogRefreshed: boolean;
  readonly error: SourceManagementClientError | null;
  readonly requestToken: number;
}

@Injectable({ providedIn: 'root' })
export class SourceManagementStore {
  private readonly mutableState = signal<SourceManagementStoreState>(emptyState());
  private pollHandle: unknown | null = null;
  private generation = 0;
  private nextRequestToken = 0;
  private reconnectAttempts = 0;
  private catalogRefreshAttempts = 0;
  private readonly pendingReadDeadlines = new Set<PendingReadDeadline>();
  private started = false;
  private submissionInFlight = false;
  private disposed = false;

  readonly state = this.mutableState.asReadonly();
  readonly capability = computed(() => this.state().capability);
  readonly capabilityPending = computed(() => this.state().capabilityPending);
  readonly dialogOpen = computed(() => this.state().dialogOpen);
  readonly mode = computed(() => this.state().mode);
  readonly removalSource = computed(() => this.state().removalSource);
  readonly operation = computed(() => this.state().operation);
  readonly pending = computed(() => this.state().pending);
  readonly reconnecting = computed(() => this.state().reconnecting);
  readonly catalogRefreshed = computed(() => this.state().catalogRefreshed);
  readonly error = computed(() => this.state().error);
  readonly terminal = computed(() => {
    const phase = this.operation()?.phase;
    return phase === 'completed' || phase === 'rolledBack' || phase === 'failed' ||
      this.error()?.code === 'source_management_reconnect_timeout';
  });
  readonly canOpen = computed(() =>
    this.capability()?.supported === true && !this.pending() && !this.dialogOpen(),
  );
  readonly canRetryCapability = computed(() =>
    this.capability() === null && !this.capabilityPending() && !this.pending() &&
    this.error() !== null,
  );
  readonly disabledReason = computed(() => {
    if (this.capabilityPending()) {
      return 'Checking whether this installation supports managed host sources.';
    }
    if (this.pending()) {
      return 'A source-management operation is already in progress.';
    }
    if (this.capability()?.supported === false) {
      return this.capability()!.detail;
    }
    if (!this.capability()) {
      return this.error()?.detail ?? 'Source management is unavailable.';
    }
    return null;
  });

  constructor(
    private readonly api: CommanderApiPort,
    private readonly commander: CommanderStore,
    @Inject(SOURCE_MANAGEMENT_DEADLINE_TIMER)
    private readonly deadlineTimer: SourceManagementDeadlineTimer,
    @Inject(SOURCE_MANAGEMENT_SCHEDULER)
    private readonly scheduler: SourceManagementScheduler,
    protectedState: ProtectedStateResetService,
    destroyRef: DestroyRef,
  ) {
    const unregister = protectedState.register(() => this.reset());
    destroyRef.onDestroy(() => {
      unregister();
      this.dispose();
    });
  }

  async start(): Promise<void> {
    if (this.started || this.disposed) {
      return;
    }

    this.started = true;
    const generation = this.generation;
    const token = ++this.nextRequestToken;
    this.mutableState.update((state) => ({
      ...state,
      capabilityPending: true,
      error: null,
      requestToken: token,
    }));
    try {
      const capability = await this.withReadDeadline(
        this.api.getSourceManagementStatus(),
      );
      if (this.isCurrent(generation, token)) {
        this.mutableState.update((state) => ({
          ...state,
          capability: Object.freeze({ ...capability }),
          capabilityPending: false,
        }));
      }
    } catch (error: unknown) {
      if (this.isCurrent(generation, token)) {
        this.mutableState.update((state) => ({
          ...state,
          capabilityPending: false,
          error: error instanceof SourceManagementDeadlineExceededError
            ? {
              code: 'source_management_capability_timeout',
              detail: 'Source-management capability did not respond in time. Check the server connection, then retry.',
            }
            : safeError(error, 'Source-management capability could not be loaded.'),
        }));
        this.started = false;
      }
    }
  }

  open(): void {
    if (!this.canOpen()) {
      return;
    }
    this.mutableState.update((state) => ({
      ...state,
      dialogOpen: true,
      mode: 'add',
      removalSource: null,
      operation: null,
      pending: false,
      reconnecting: false,
      catalogRefreshed: false,
      error: null,
    }));
  }

  openRemoval(source: SourceDto): void {
    if (!this.canOpen()) {
      return;
    }
    this.mutableState.update((state) => ({
      ...state,
      dialogOpen: true,
      mode: 'remove',
      removalSource: Object.freeze({ ...source }),
      operation: null,
      pending: false,
      reconnecting: false,
      catalogRefreshed: false,
      error: null,
    }));
  }

  close(): void {
    if (!this.dialogOpen() || this.pending()) {
      return;
    }
    this.clearPoll();
    this.mutableState.update((state) => ({
      ...state,
      dialogOpen: false,
      mode: 'add',
      removalSource: null,
      operation: null,
      pending: false,
      reconnecting: false,
      catalogRefreshed: false,
      error: null,
    }));
  }

  async submit(request: SourceAddRequestDto): Promise<void> {
    await this.submitMutation(() => this.api.addSource(request));
  }

  async submitRemoval(): Promise<void> {
    const source = this.removalSource();
    if (!source || this.mode() !== 'remove') {
      return;
    }
    await this.submitMutation(() => this.api.removeSource(source.id));
  }

  private async submitMutation(
    submit: () => Promise<SourceManagementOperationDto>,
  ): Promise<void> {
    if (!this.dialogOpen() || this.submissionInFlight || this.pending() || this.disposed) {
      return;
    }

    this.submissionInFlight = true;
    this.reconnectAttempts = 0;
    this.catalogRefreshAttempts = 0;
    const generation = ++this.generation;
    const token = ++this.nextRequestToken;
    this.mutableState.update((state) => ({
      ...state,
      operation: null,
      pending: true,
      reconnecting: false,
      catalogRefreshed: false,
      error: null,
      requestToken: token,
    }));
    try {
      const operation = await submit();
      if (this.isCurrent(generation, token)) {
        await this.capture(operation, generation);
      }
    } catch (error: unknown) {
      if (this.isCurrent(generation, token)) {
        this.mutableState.update((state) => ({
          ...state,
          pending: false,
          reconnecting: false,
          error: safeError(
            error,
            this.mode() === 'remove'
              ? 'The source mapping could not be removed.'
              : 'The source could not be added.',
          ),
        }));
      }
    } finally {
      this.submissionInFlight = false;
    }
  }

  reset(): void {
    if (this.disposed) {
      return;
    }
    this.started = false;
    this.submissionInFlight = false;
    this.invalidatePolling();
    this.mutableState.set(emptyState(++this.nextRequestToken));
  }

  private async poll(generation: number): Promise<void> {
    this.pollHandle = null;
    const operationId = this.operation()?.operationId;
    if (!operationId || !this.isCurrentGeneration(generation) || this.terminal()) {
      return;
    }

    try {
      const operation = await this.withReadDeadline(
        this.api.getSourceManagementOperation(operationId),
      );
      if (!this.isCurrentGeneration(generation)) {
        return;
      }
      this.reconnectAttempts = 0;
      await this.capture(operation, generation);
    } catch (error: unknown) {
      if (!this.isCurrentGeneration(generation) || this.terminal()) {
        return;
      }
      if (!isReconnectable(error)) {
        this.mutableState.update((state) => ({
          ...state,
          pending: false,
          reconnecting: false,
          error: safeError(error, 'The source-management operation status could not be loaded.'),
        }));
        return;
      }

      this.reconnectAttempts++;
      if (this.reconnectAttempts >= maximumReconnectAttempts) {
        this.mutableState.update((state) => ({
          ...state,
          pending: false,
          reconnecting: false,
          error: {
            code: 'source_management_reconnect_timeout',
            detail: 'ReachCommander did not reconnect in time. Run reachcommander doctor on the Ubuntu host and review support diagnostics.',
          },
        }));
        return;
      }

      this.mutableState.update((state) => ({
        ...state,
        pending: true,
        reconnecting: true,
        error: null,
      }));
      this.schedulePoll(generation);
    }
  }

  private async capture(
    operation: SourceManagementOperationDto,
    generation: number,
  ): Promise<void> {
    this.clearPoll();
    const terminal = isTerminal(operation);
    const refreshingCatalog = operation.phase === 'completed';
    this.mutableState.update((state) => ({
      ...state,
      operation: Object.freeze({ ...operation }),
      pending: !terminal || refreshingCatalog,
      reconnecting: false,
      error: null,
    }));

    if (operation.phase === 'completed') {
      await this.refreshCompletedCatalog(operation, generation);
      return;
    }

    if (!terminal && this.isCurrentGeneration(generation)) {
      this.schedulePoll(generation);
    }
  }

  private async refreshCompletedCatalog(
    operation: SourceManagementOperationDto,
    generation: number,
  ): Promise<void> {
    if (!this.isCurrentGeneration(generation)) {
      return;
    }

    if (!operation.sourceId) {
      this.finishCatalogRefreshWithError(
        'source_management_catalog_refresh_failed',
        'The source was added, but its generated ID was unavailable. Refresh the page; if it is still missing, run reachcommander doctor on the Ubuntu host.',
      );
      return;
    }

    this.catalogRefreshAttempts++;
    try {
      const sources = await this.withReadDeadline(
        this.commander.reloadSourceCatalog(),
      );
      if (!this.isCurrentGeneration(generation)) {
        return;
      }
      const sourcePresent = sources.some((source) => source.id === operation.sourceId);
      if ((this.mode() === 'add' && sourcePresent) ||
          (this.mode() === 'remove' && !sourcePresent)) {
        this.catalogRefreshAttempts = 0;
        this.mutableState.update((state) => ({
          ...state,
          pending: false,
          reconnecting: false,
          catalogRefreshed: true,
          error: null,
        }));
        return;
      }
    } catch {
      if (!this.isCurrentGeneration(generation)) {
        return;
      }
    }

    if (this.catalogRefreshAttempts >= maximumCatalogRefreshAttempts) {
      this.finishCatalogRefreshWithError(
        'source_management_catalog_refresh_timeout',
        this.mode() === 'remove'
          ? 'The host completed the source change, but the removed mapping is still visible. Refresh the page; if it remains, run reachcommander doctor on the Ubuntu host.'
          : 'The host completed the source change, but the new source did not appear in time. Refresh the page; if it is still missing, run reachcommander doctor on the Ubuntu host.',
      );
      return;
    }

    this.mutableState.update((state) => ({
      ...state,
      pending: true,
      reconnecting: false,
      catalogRefreshed: false,
      error: null,
    }));
    this.scheduleCatalogRefresh(operation, generation);
  }

  private scheduleCatalogRefresh(
    operation: SourceManagementOperationDto,
    generation: number,
  ): void {
    this.clearPoll();
    const exponent = Math.max(0, this.catalogRefreshAttempts - 1);
    const delay = Math.min(initialPollMilliseconds * (2 ** exponent), maximumPollMilliseconds);
    this.pollHandle = this.scheduler.schedule(async () => {
      this.pollHandle = null;
      await this.refreshCompletedCatalog(operation, generation);
    }, delay);
  }

  private finishCatalogRefreshWithError(code: string, detail: string): void {
    this.clearPoll();
    this.mutableState.update((state) => ({
      ...state,
      pending: false,
      reconnecting: false,
      catalogRefreshed: false,
      error: { code, detail },
    }));
  }

  private schedulePoll(generation: number): void {
    this.clearPoll();
    const exponent = Math.max(0, this.reconnectAttempts - 1);
    const delay = Math.min(initialPollMilliseconds * (2 ** exponent), maximumPollMilliseconds);
    this.pollHandle = this.scheduler.schedule(() => this.poll(generation), delay);
  }

  private clearPoll(): void {
    if (this.pollHandle === null) {
      return;
    }
    this.scheduler.cancel(this.pollHandle);
    this.pollHandle = null;
  }

  private withReadDeadline<T>(request: Promise<T>): Promise<T> {
    return new Promise<T>((resolve, reject) => {
      let settled = false;
      const deadline: PendingReadDeadline = {
        cancel: () => settle(() => reject(new SourceManagementDeadlineCancelledError())),
      };
      const settle = (complete: () => void) => {
        if (settled) {
          return;
        }
        settled = true;
        if (deadline.handle !== undefined) {
          this.deadlineTimer.cancel(deadline.handle);
        }
        this.pendingReadDeadlines.delete(deadline);
        complete();
      };

      this.pendingReadDeadlines.add(deadline);
      deadline.handle = this.deadlineTimer.schedule(
        () => settle(() => reject(new SourceManagementDeadlineExceededError())),
        readRequestTimeoutMilliseconds,
      );
      if (settled) {
        this.deadlineTimer.cancel(deadline.handle);
        this.pendingReadDeadlines.delete(deadline);
      }
      request.then(
        (value) => settle(() => resolve(value)),
        (error: unknown) => settle(() => reject(error)),
      );
    });
  }

  private cancelReadDeadlines(): void {
    for (const deadline of [...this.pendingReadDeadlines]) {
      deadline.cancel();
    }
  }

  private invalidatePolling(): void {
    this.generation++;
    this.reconnectAttempts = 0;
    this.catalogRefreshAttempts = 0;
    this.clearPoll();
    this.cancelReadDeadlines();
  }

  private isCurrent(generation: number, token: number): boolean {
    return this.isCurrentGeneration(generation) && this.state().requestToken === token;
  }

  private isCurrentGeneration(generation: number): boolean {
    return !this.disposed && this.generation === generation;
  }

  private dispose(): void {
    this.disposed = true;
    this.invalidatePolling();
  }
}

function emptyState(requestToken = 0): SourceManagementStoreState {
  return {
    capability: null,
    capabilityPending: false,
    dialogOpen: false,
    mode: 'add',
    removalSource: null,
    operation: null,
    pending: false,
    reconnecting: false,
    catalogRefreshed: false,
    error: null,
    requestToken,
  };
}

function isTerminal(operation: SourceManagementOperationDto): boolean {
  return operation.phase === 'completed' ||
    operation.phase === 'rolledBack' ||
    operation.phase === 'failed';
}

function isReconnectable(error: unknown): boolean {
  return error instanceof SourceManagementDeadlineExceededError ||
    error instanceof TypeError ||
    (error instanceof HttpErrorResponse &&
      (error.status === 0 || error.status === 502 || error.status === 503 || error.status === 504));
}

function safeError(error: unknown, fallback: string): SourceManagementClientError {
  if (error instanceof HttpErrorResponse &&
      typeof error.error === 'object' && error.error !== null) {
    const payload = error.error as { code?: unknown; detail?: unknown };
    return {
      code: typeof payload.code === 'string' ? payload.code : 'source_management_request_failed',
      detail: typeof payload.detail === 'string' ? payload.detail : fallback,
    };
  }
  return { code: 'source_management_request_failed', detail: fallback };
}
