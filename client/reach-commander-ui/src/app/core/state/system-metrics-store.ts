import { HttpErrorResponse } from '@angular/common/http';
import { computed, Injectable, signal } from '@angular/core';
import {
  CommanderApiPort,
  HardwareMetricsState,
  SystemMetricsDto,
} from '../api/api.models';

export interface SystemMetricsStoreState {
  readonly snapshot: SystemMetricsDto | null;
  readonly pending: boolean;
  readonly errorCode: 'metrics_not_ready' | 'request_failed' | null;
  readonly requestToken: number;
  readonly nowEpochMilliseconds: number;
}

@Injectable({ providedIn: 'root' })
export class SystemMetricsStore {
  private readonly mutableState = signal<SystemMetricsStoreState>(emptyMetricsState());
  private started = false;
  private nextRequestToken = 0;
  private timer: number | null = null;
  private inFlight: Promise<void> | null = null;
  private refreshAfterCurrent = false;

  readonly state = this.mutableState.asReadonly();
  readonly effectiveSnapshot = computed(() => this.state().snapshot);
  readonly effectiveState = computed<HardwareMetricsState | null>(() => {
    const state = this.state();
    const snapshot = state.snapshot;
    if (!snapshot || snapshot.state === 'disabled') {
      return snapshot?.state ?? null;
    }

    const sampledAt = Date.parse(snapshot.sampledAt);
    return Number.isFinite(sampledAt) &&
      state.nowEpochMilliseconds - sampledAt >= 15_000
      ? 'stale'
      : snapshot.state;
  });

  constructor(private readonly api: CommanderApiPort) {}

  start(): void {
    if (this.started) {
      return;
    }

    this.started = true;
    document.addEventListener('visibilitychange', this.onVisibilityChange);
    this.refresh();
  }

  stop(): void {
    if (!this.started) {
      return;
    }

    this.started = false;
    this.clearTimer();
    document.removeEventListener('visibilitychange', this.onVisibilityChange);
    this.refreshAfterCurrent = false;
    const requestToken = ++this.nextRequestToken;
    this.mutableState.update((state) => ({
      ...state,
      pending: false,
      requestToken,
      nowEpochMilliseconds: Date.now(),
    }));
  }

  reset(): void {
    this.stop();
    this.refreshAfterCurrent = false;
    const requestToken = ++this.nextRequestToken;
    this.mutableState.set(emptyMetricsState(requestToken));
  }

  refresh(): void {
    if (!this.started) {
      return;
    }

    this.clearTimer();
    if (this.inFlight) {
      this.refreshAfterCurrent = true;
      return;
    }

    this.beginRequest();
  }

  private readonly onVisibilityChange = (): void => {
    this.clearTimer();
    if (!this.started || document.visibilityState !== 'visible') {
      return;
    }

    if (this.inFlight) {
      this.refreshAfterCurrent = true;
      const requestToken = ++this.nextRequestToken;
      this.mutableState.update((state) => ({
        ...state,
        requestToken,
        nowEpochMilliseconds: Date.now(),
      }));
      return;
    }

    this.beginRequest();
  };

  private beginRequest(): void {
    const requestToken = ++this.nextRequestToken;
    this.mutableState.update((state) => ({
      ...state,
      pending: true,
      errorCode: null,
      requestToken,
      nowEpochMilliseconds: Date.now(),
    }));

    let request: Promise<SystemMetricsDto>;
    try {
      request = this.api.getSystemMetrics();
    } catch (error: unknown) {
      request = Promise.reject(error);
    }

    const operation = this.settleRequest(request, requestToken);
    this.inFlight = operation;
    void operation.then(() => this.requestSettled(operation));
  }

  private async settleRequest(
    request: Promise<SystemMetricsDto>,
    requestToken: number,
  ): Promise<void> {
    try {
      const snapshot = await request;
      if (!this.started || this.state().requestToken !== requestToken) {
        return;
      }

      this.mutableState.update((state) => ({
        ...state,
        snapshot,
        pending: false,
        errorCode: null,
        nowEpochMilliseconds: Date.now(),
      }));
    } catch (error: unknown) {
      if (!this.started || this.state().requestToken !== requestToken) {
        return;
      }

      this.mutableState.update((state) => ({
        ...state,
        pending: false,
        errorCode: this.errorCode(error),
        nowEpochMilliseconds: Date.now(),
      }));
    }
  }

  private requestSettled(operation: Promise<void>): void {
    if (this.inFlight !== operation) {
      return;
    }

    this.inFlight = null;
    if (!this.started) {
      return;
    }

    if (this.refreshAfterCurrent) {
      this.refreshAfterCurrent = false;
      this.beginRequest();
      return;
    }

    if (document.visibilityState === 'visible') {
      this.timer = window.setTimeout(() => {
        this.timer = null;
        this.refresh();
      }, 5_000);
    }
  }

  private clearTimer(): void {
    if (this.timer === null) {
      return;
    }

    window.clearTimeout(this.timer);
    this.timer = null;
  }

  private errorCode(error: unknown): 'metrics_not_ready' | 'request_failed' {
    return error instanceof HttpErrorResponse &&
      error.status === 503 &&
      typeof error.error === 'object' &&
      error.error !== null &&
      'code' in error.error &&
      error.error.code === 'metrics_not_ready'
      ? 'metrics_not_ready'
      : 'request_failed';
  }
}

function emptyMetricsState(requestToken = 0): SystemMetricsStoreState {
  return {
    snapshot: null,
    pending: false,
    errorCode: null,
    requestToken,
    nowEpochMilliseconds: Date.now(),
  };
}
