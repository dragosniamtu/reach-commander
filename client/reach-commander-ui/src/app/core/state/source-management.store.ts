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
  SourceManagementCapabilityDto,
  SourceManagementOperationDto,
} from '../api/api.models';
import { ProtectedStateResetService } from '../auth/protected-state-reset.service';
import { CommanderStore } from './commander-store';

const initialPollMilliseconds = 1_000;
const maximumPollMilliseconds = 10_000;
const maximumReconnectAttempts = 24;

export interface SourceManagementScheduler {
  schedule(callback: () => Promise<void> | void, delayMilliseconds: number): unknown;
  cancel(handle: unknown): void;
}

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
  private started = false;
  private submissionInFlight = false;
  private disposed = false;

  readonly state = this.mutableState.asReadonly();
  readonly capability = computed(() => this.state().capability);
  readonly capabilityPending = computed(() => this.state().capabilityPending);
  readonly dialogOpen = computed(() => this.state().dialogOpen);
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
      const capability = await this.api.getSourceManagementStatus();
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
          error: safeError(error, 'Source-management capability could not be loaded.'),
        }));
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
      operation: null,
      pending: false,
      reconnecting: false,
      catalogRefreshed: false,
      error: null,
    }));
  }

  async submit(request: SourceAddRequestDto): Promise<void> {
    if (!this.dialogOpen() || this.submissionInFlight || this.pending() || this.disposed) {
      return;
    }

    this.submissionInFlight = true;
    this.reconnectAttempts = 0;
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
      const operation = await this.api.addSource(request);
      if (this.isCurrent(generation, token)) {
        await this.capture(operation, generation);
      }
    } catch (error: unknown) {
      if (this.isCurrent(generation, token)) {
        this.mutableState.update((state) => ({
          ...state,
          pending: false,
          reconnecting: false,
          error: safeError(error, 'The source could not be added.'),
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
      const operation = await this.api.getSourceManagementOperation(operationId);
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
      try {
        await this.commander.reloadSourceCatalog();
        if (this.isCurrentGeneration(generation)) {
          this.mutableState.update((state) => ({
            ...state,
            pending: false,
            catalogRefreshed: true,
          }));
        }
      } catch (error: unknown) {
        if (this.isCurrentGeneration(generation)) {
          this.mutableState.update((state) => ({
            ...state,
            pending: false,
            catalogRefreshed: false,
            error: safeError(error, 'The source was added, but the source list could not be refreshed.'),
          }));
        }
      }
      return;
    }

    if (!terminal && this.isCurrentGeneration(generation)) {
      this.schedulePoll(generation);
    }
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

  private invalidatePolling(): void {
    this.generation++;
    this.reconnectAttempts = 0;
    this.clearPoll();
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
  return error instanceof TypeError ||
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
