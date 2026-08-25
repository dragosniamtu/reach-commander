import { HttpErrorResponse } from '@angular/common/http';
import {
  DestroyRef,
  Inject,
  Injectable,
  InjectionToken,
  computed,
  signal,
} from '@angular/core';
import { CommanderApiPort, SystemUpdateStatusDto } from '../api/api.models';
import { ProtectedStateResetService } from '../auth/protected-state-reset.service';
import { PwaService } from '../pwa/pwa.service';

const initialPollMilliseconds = 1_000;
const maximumPollMilliseconds = 15_000;
const maximumReconnectAttempts = 24;

export interface SystemUpdateScheduler {
  schedule(callback: () => Promise<void> | void, delayMilliseconds: number): unknown;
  cancel(handle: unknown): void;
}

export const SYSTEM_UPDATE_SCHEDULER = new InjectionToken<SystemUpdateScheduler>(
  'SYSTEM_UPDATE_SCHEDULER',
  {
    providedIn: 'root',
    factory: () => ({
      schedule: (callback, delay) => setTimeout(() => void callback(), delay),
      cancel: (handle) => clearTimeout(handle as ReturnType<typeof setTimeout>),
    }),
  },
);

export interface SystemUpdateResultStorage {
  getItem(key: string): string | null;
  setItem(key: string, value: string): void;
}

export const SYSTEM_UPDATE_RESULT_STORAGE = new InjectionToken<SystemUpdateResultStorage>(
  'SYSTEM_UPDATE_RESULT_STORAGE',
  {
    providedIn: 'root',
    factory: () => globalThis.sessionStorage,
  },
);

export interface SystemUpdateClientError {
  readonly code: string;
  readonly detail: string;
}

export interface SystemUpdateStoreState {
  readonly status: SystemUpdateStatusDto | null;
  readonly pending: boolean;
  readonly reconnecting: boolean;
  readonly error: SystemUpdateClientError | null;
  readonly requestToken: number;
}

@Injectable({ providedIn: 'root' })
export class SystemUpdateStore {
  private readonly mutableState = signal<SystemUpdateStoreState>(emptyState());
  private readonly refreshedResults = new Set<string>();
  private pollHandle: unknown | null = null;
  private generation = 0;
  private nextRequestToken = 0;
  private reconnectAttempts = 0;
  private started = false;
  private checkInFlight = false;
  private applyInFlight = false;
  private disposed = false;

  readonly state = this.mutableState.asReadonly();
  readonly status = computed(() => this.state().status);
  readonly pending = computed(() => this.state().pending);
  readonly reconnecting = computed(() => this.state().reconnecting);
  readonly error = computed(() => this.state().error);
  readonly applying = computed(() => this.status()?.phase === 'applying');
  readonly canApply = computed(() => this.status()?.canApply === true && !this.pending());

  constructor(
    private readonly api: CommanderApiPort,
    @Inject(SYSTEM_UPDATE_SCHEDULER) private readonly scheduler: SystemUpdateScheduler,
    @Inject(SYSTEM_UPDATE_RESULT_STORAGE) private readonly resultStorage: SystemUpdateResultStorage,
    private readonly pwa: PwaService,
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
      pending: true,
      error: null,
      requestToken: token,
    }));
    try {
      const cached = await this.api.getSystemUpdate();
      if (!this.isCurrent(generation, token)) {
        return;
      }

      this.capture(cached);
    } catch (error: unknown) {
      if (!this.isCurrent(generation, token)) {
        return;
      }

      this.mutableState.update((state) => ({
        ...state,
        pending: false,
        error: safeError(error),
      }));
    }

    if (this.isCurrentGeneration(generation)) {
      await this.check();
    }
  }

  async check(): Promise<void> {
    if (this.checkInFlight || this.applyInFlight || this.disposed) {
      return;
    }

    this.checkInFlight = true;
    const generation = this.generation;
    const token = ++this.nextRequestToken;
    this.mutableState.update((state) => ({
      ...state,
      pending: true,
      error: null,
      requestToken: token,
    }));
    try {
      const status = await this.api.checkSystemUpdate();
      if (this.isCurrent(generation, token)) {
        this.capture(status);
      }
    } catch (error: unknown) {
      if (this.isCurrent(generation, token)) {
        this.mutableState.update((state) => ({
          ...state,
          pending: false,
          error: safeError(error),
        }));
      }
    } finally {
      this.checkInFlight = false;
    }
  }

  async apply(): Promise<void> {
    const current = this.status();
    if (!current?.canApply || this.applyInFlight || this.disposed) {
      return;
    }

    this.applyInFlight = true;
    this.clearPoll();
    this.reconnectAttempts = 0;
    const generation = ++this.generation;
    const token = ++this.nextRequestToken;
    const applying = Object.freeze({
      ...current,
      phase: 'applying' as const,
      canApply: false,
      reasonCode: 'update_applying',
      detail: 'ReachCommander is applying the trusted update.',
      operationId: null,
    });
    this.mutableState.set({
      status: applying,
      pending: true,
      reconnecting: false,
      error: null,
      requestToken: token,
    });

    try {
      const status = await this.api.applySystemUpdate();
      if (this.isCurrent(generation, token)) {
        this.capture(status);
      }
    } catch (error: unknown) {
      if (!this.isCurrent(generation, token)) {
        return;
      }

      if (isConnectionLoss(error)) {
        this.mutableState.update((state) => ({
          ...state,
          pending: false,
          reconnecting: true,
          error: null,
        }));
        this.schedulePoll(generation);
      } else {
        this.mutableState.set({
          status: current,
          pending: false,
          reconnecting: false,
          error: safeError(error),
          requestToken: token,
        });
      }
    } finally {
      this.applyInFlight = false;
    }
  }

  capture(status: SystemUpdateStatusDto): void {
    if (this.disposed) {
      return;
    }

    this.clearPoll();
    this.mutableState.update((state) => ({
      ...state,
      status: Object.freeze({ ...status }),
      pending: false,
      reconnecting: false,
      error: null,
    }));

    if (status.phase === 'applying') {
      this.schedulePoll(this.generation);
    } else if (status.phase === 'completed') {
      this.refreshCompletedShell(status);
    }
  }

  dismissTerminal(): void {
    const phase = this.status()?.phase;
    if (phase !== 'rolledBack' && phase !== 'failed') {
      return;
    }

    this.mutableState.update((state) => ({ ...state, status: null, error: null }));
  }

  reset(): void {
    if (this.disposed) {
      return;
    }

    this.started = false;
    this.checkInFlight = false;
    this.applyInFlight = false;
    this.invalidatePolling();
    this.mutableState.set(emptyState(++this.nextRequestToken));
  }

  private async poll(generation: number): Promise<void> {
    this.pollHandle = null;
    if (!this.isCurrentGeneration(generation) || this.status()?.phase !== 'applying') {
      return;
    }

    try {
      const status = await this.api.getSystemUpdate();
      if (!this.isCurrentGeneration(generation)) {
        return;
      }

      this.reconnectAttempts = 0;
      this.capture(status);
    } catch (error: unknown) {
      if (!this.isCurrentGeneration(generation) || this.status()?.phase !== 'applying') {
        return;
      }

      this.reconnectAttempts++;
      if (this.reconnectAttempts >= maximumReconnectAttempts) {
        const current = this.status()!;
        this.mutableState.update((state) => ({
          ...state,
          status: Object.freeze({
            ...current,
            phase: 'failed',
            updateAvailable: false,
            canApply: false,
            reasonCode: 'system_update_reconnect_timeout',
            detail: 'The update status could not be recovered. Run reachcommander doctor on the host.',
          }),
          reconnecting: false,
          error: safeError(error),
        }));
        return;
      }

      this.mutableState.update((state) => ({
        ...state,
        pending: false,
        reconnecting: true,
        error: null,
      }));
      this.schedulePoll(generation);
    }
  }

  private schedulePoll(generation: number): void {
    this.clearPoll();
    const exponent = Math.max(0, this.reconnectAttempts - 1);
    const delay = Math.min(
      initialPollMilliseconds * (2 ** exponent),
      maximumPollMilliseconds,
    );
    this.pollHandle = this.scheduler.schedule(() => this.poll(generation), delay);
  }

  private refreshCompletedShell(status: SystemUpdateStatusDto): void {
    const resultId = status.operationId ?? status.updatedAt;
    if (this.refreshedResults.has(resultId) || this.readStoredResult() === resultId) {
      return;
    }

    this.refreshedResults.add(resultId);
    try {
      this.resultStorage.setItem('reachcommander.systemUpdateRefreshed', resultId);
    } catch {
      // In-memory de-duplication still prevents repeated activation in restricted storage modes.
    }
    void this.pwa.refreshAfterSystemUpdate();
  }

  private readStoredResult(): string | null {
    try {
      return this.resultStorage.getItem('reachcommander.systemUpdateRefreshed');
    } catch {
      return null;
    }
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

function emptyState(requestToken = 0): SystemUpdateStoreState {
  return {
    status: null,
    pending: false,
    reconnecting: false,
    error: null,
    requestToken,
  };
}

function isConnectionLoss(error: unknown): boolean {
  return error instanceof TypeError ||
    (error instanceof HttpErrorResponse && error.status === 0);
}

function safeError(error: unknown): SystemUpdateClientError {
  if (error instanceof HttpErrorResponse &&
      typeof error.error === 'object' &&
      error.error !== null) {
    const payload = error.error as { code?: unknown; detail?: unknown };
    return {
      code: typeof payload.code === 'string' ? payload.code : 'system_update_request_failed',
      detail: typeof payload.detail === 'string'
        ? payload.detail
        : 'The system update request could not be completed.',
    };
  }

  return {
    code: 'system_update_request_failed',
    detail: 'The system update request could not be completed.',
  };
}
