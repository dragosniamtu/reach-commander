import { HttpErrorResponse } from '@angular/common/http';
import { DestroyRef, Inject, Injectable, InjectionToken, computed, signal } from '@angular/core';
import {
  CommanderApiPort,
  TextEncodingKind,
  TextEncodingOperationDto,
  TextEncodingPreviewDto,
} from '../api/api.models';
import {
  TextEncodingContext,
  TextEncodingPhase,
  TextEncodingSafeError,
} from './text-encoding.models';

const previewDebounceMilliseconds = 250;
const pollMilliseconds = 500;

export interface TextEncodingScheduler {
  schedule(callback: () => Promise<void> | void, delayMilliseconds: number): unknown;
  cancel(handle: unknown): void;
}

export const TEXT_ENCODING_SCHEDULER = new InjectionToken<TextEncodingScheduler>(
  'TEXT_ENCODING_SCHEDULER',
  {
    providedIn: 'root',
    factory: () => ({
      schedule: (callback, delay) => setTimeout(() => void callback(), delay),
      cancel: (handle) => clearTimeout(handle as ReturnType<typeof setTimeout>),
    }),
  },
);

export interface TextEncodingState {
  readonly phase: TextEncodingPhase;
  readonly context: TextEncodingContext | null;
  readonly sourceEncoding: TextEncodingKind;
  readonly outputEncoding: TextEncodingKind;
  readonly preview: TextEncodingPreviewDto | null;
  readonly operation: TextEncodingOperationDto | null;
  readonly error: TextEncodingSafeError | null;
  readonly requestToken: number;
}

@Injectable({ providedIn: 'root' })
export class TextEncodingStore {
  private readonly mutableState = signal<TextEncodingState>(closedState());
  private previewHandle: unknown | null = null;
  private pollHandle: unknown | null = null;
  private pollGeneration = 0;
  private nextRequestToken = 0;
  private completionHandler: ((context: TextEncodingContext) => void) | null = null;
  private readonly completedOperationIds = new Set<string>();

  readonly state = this.mutableState.asReadonly();
  readonly canExecute = computed(() => {
    const state = this.state();
    return state.phase === 'review' &&
      state.preview?.canExecute === true &&
      !isExpired(state.preview.expiresAt);
  });
  readonly canCancel = computed(() => {
    const state = this.state();
    return state.phase === 'running' && state.operation?.canCancel === true;
  });

  constructor(
    private readonly api: CommanderApiPort,
    @Inject(TEXT_ENCODING_SCHEDULER) private readonly scheduler: TextEncodingScheduler,
    destroyRef: DestroyRef,
  ) {
    destroyRef.onDestroy(() => this.dispose());
  }

  setCompletionHandler(handler: (context: TextEncodingContext) => void): void {
    this.completionHandler = handler;
  }

  async open(context: TextEncodingContext): Promise<void> {
    this.invalidateAllScheduledWork();
    const captured = freezeContext(context);
    const requestToken = ++this.nextRequestToken;
    this.mutableState.set({
      phase: 'previewing',
      context: captured,
      sourceEncoding: 'auto',
      outputEncoding: 'utf8',
      preview: null,
      operation: null,
      error: null,
      requestToken,
    });
    await this.preview(requestToken, captured);
  }

  setSourceEncoding(sourceEncoding: TextEncodingKind): void {
    const state = this.state();
    if (!state.context || state.phase === 'closed' || state.sourceEncoding === sourceEncoding) {
      return;
    }
    this.schedulePreview(state.context, sourceEncoding, state.outputEncoding);
  }

  setOutputEncoding(outputEncoding: TextEncodingKind): void {
    const state = this.state();
    if (!state.context || state.phase === 'closed' || state.outputEncoding === outputEncoding) {
      return;
    }
    this.schedulePreview(state.context, state.sourceEncoding, outputEncoding);
  }

  async reviewAgain(): Promise<void> {
    const state = this.state();
    if (!state.context) {
      return;
    }

    this.clearPreview();
    this.invalidatePolling();
    const requestToken = ++this.nextRequestToken;
    this.mutableState.set({
      ...state,
      phase: 'previewing',
      preview: null,
      operation: null,
      error: null,
      requestToken,
    });
    await this.preview(requestToken, state.context);
  }

  async execute(): Promise<void> {
    const state = this.state();
    if (!state.context || !state.preview || state.phase !== 'review') {
      return;
    }
    if (isExpired(state.preview.expiresAt)) {
      this.mutableState.set({
        ...state,
        error: {
          code: 'text_encoding_plan_expired',
          detail: 'The encoding preview expired. Review the files again.',
        },
      });
      return;
    }
    if (!state.preview.canExecute) {
      return;
    }

    const token = state.requestToken;
    const context = state.context;
    this.mutableState.set({ ...state, phase: 'starting', error: null });
    try {
      const operation = await this.api.executeTextEncoding(state.preview.planId);
      if (this.isCurrent(token, context)) {
        this.applyOperation(operation, token, context);
      }
    } catch (error: unknown) {
      if (this.isCurrent(token, context)) {
        const safe = safeProblem(error);
        this.mutableState.set({
          ...this.state(),
          phase: safe.code === 'text_encoding_capacity_reached' ? 'review' : 'failed',
          error: safe,
        });
      }
    }
  }

  async cancel(): Promise<void> {
    const state = this.state();
    if (!this.canCancel() || !state.operation || !state.context) {
      return;
    }

    this.invalidatePolling();
    const token = state.requestToken;
    const context = state.context;
    this.mutableState.set({ ...state, phase: 'cancelling', error: null });
    try {
      const operation = await this.api.cancelTextEncodingOperation(state.operation.operationId);
      if (this.isCurrent(token, context)) {
        this.applyOperation(operation, token, context);
      }
    } catch (error: unknown) {
      if (this.isCurrent(token, context)) {
        this.mutableState.set({ ...this.state(), phase: 'running', error: safeProblem(error) });
        this.schedulePoll(token, context);
      }
    }
  }

  close(): void {
    this.invalidateAllScheduledWork();
    this.mutableState.set(closedState(++this.nextRequestToken));
  }

  private schedulePreview(
    context: TextEncodingContext,
    sourceEncoding: TextEncodingKind,
    outputEncoding: TextEncodingKind,
  ): void {
    this.clearPreview();
    this.invalidatePolling();
    const requestToken = ++this.nextRequestToken;
    this.mutableState.set({
      ...this.state(),
      phase: 'previewing',
      sourceEncoding,
      outputEncoding,
      preview: null,
      operation: null,
      error: null,
      requestToken,
    });
    this.previewHandle = this.scheduler.schedule(async () => {
      this.previewHandle = null;
      await this.preview(requestToken, context);
    }, previewDebounceMilliseconds);
  }

  private async preview(token: number, context: TextEncodingContext): Promise<void> {
    const state = this.state();
    try {
      const preview = await this.api.previewTextEncoding({
        sourceId: context.sourceId,
        filePaths: context.entries.map((entry) => entry.relativePath),
        sourceEncoding: state.sourceEncoding,
        outputEncoding: state.outputEncoding,
      });
      if (this.isCurrent(token, context)) {
        this.mutableState.set({
          ...this.state(),
          phase: 'review',
          preview,
          operation: null,
          error: null,
        });
      }
    } catch (error: unknown) {
      if (this.isCurrent(token, context)) {
        this.mutableState.set({ ...this.state(), phase: 'failed', error: safeProblem(error) });
      }
    }
  }

  private schedulePoll(token: number, context: TextEncodingContext): void {
    this.clearPoll();
    const generation = this.pollGeneration;
    this.pollHandle = this.scheduler.schedule(
      () => this.poll(token, context, generation),
      pollMilliseconds,
    );
  }

  private async poll(
    token: number,
    context: TextEncodingContext,
    generation: number,
  ): Promise<void> {
    this.pollHandle = null;
    const operationId = this.state().operation?.operationId;
    if (!operationId || !this.isCurrentPoll(token, context, generation)) {
      return;
    }

    try {
      const operation = await this.api.getTextEncodingOperation(operationId);
      if (this.isCurrentPoll(token, context, generation)) {
        this.applyOperation(operation, token, context);
      }
    } catch (error: unknown) {
      if (this.isCurrentPoll(token, context, generation)) {
        const safe = safeProblem(error);
        if (safe.code === 'text_encoding_operation_not_found' ||
            safe.code === 'text_encoding_operation_expired') {
          this.mutableState.set({ ...this.state(), phase: 'failed', error: safe });
          this.clearPoll();
        } else {
          this.mutableState.set({ ...this.state(), error: safe });
          this.schedulePoll(token, context);
        }
      }
    }
  }

  private applyOperation(
    operation: TextEncodingOperationDto,
    token: number,
    context: TextEncodingContext,
  ): void {
    const phase = operationPhase(operation);
    this.mutableState.set({
      ...this.state(),
      phase,
      operation,
      error: operation.errorCode
        ? {
            code: operation.errorCode,
            detail: operation.errorDetail ?? 'The encoding operation failed.',
          }
        : null,
    });
    if (phase === 'running' || phase === 'cancelling') {
      this.schedulePoll(token, context);
      return;
    }

    this.clearPoll();
    if (phase !== 'starting' && phase !== 'review' && phase !== 'previewing' &&
        phase !== 'closed' && !this.completedOperationIds.has(operation.operationId)) {
      this.completedOperationIds.add(operation.operationId);
      this.completionHandler?.(context);
    }
  }

  private isCurrent(token: number, context: TextEncodingContext): boolean {
    const state = this.state();
    return state.requestToken === token && state.context === context && state.phase !== 'closed';
  }

  private isCurrentPoll(
    token: number,
    context: TextEncodingContext,
    generation: number,
  ): boolean {
    return generation === this.pollGeneration && this.isCurrent(token, context);
  }

  private clearPreview(): void {
    if (this.previewHandle !== null) {
      this.scheduler.cancel(this.previewHandle);
      this.previewHandle = null;
    }
  }

  private clearPoll(): void {
    if (this.pollHandle !== null) {
      this.scheduler.cancel(this.pollHandle);
      this.pollHandle = null;
    }
  }

  private invalidatePolling(): void {
    this.pollGeneration++;
    this.clearPoll();
  }

  private invalidateAllScheduledWork(): void {
    this.clearPreview();
    this.invalidatePolling();
  }

  private dispose(): void {
    this.invalidateAllScheduledWork();
    this.nextRequestToken++;
  }
}

function operationPhase(operation: TextEncodingOperationDto): TextEncodingPhase {
  switch (operation.state) {
    case 'queued':
    case 'running':
      return 'running';
    case 'cancelRequested':
      return 'cancelling';
    case 'completed':
      return 'completed';
    case 'completedWithErrors':
      return 'completedWithErrors';
    case 'cancelled':
      return 'cancelled';
    case 'failed':
      return 'failed';
  }
}

function safeProblem(error: unknown): TextEncodingSafeError {
  if (error instanceof HttpErrorResponse && isRecord(error.error)) {
    return {
      code: typeof error.error['code'] === 'string' ? error.error['code'] : 'request_failed',
      detail: typeof error.error['detail'] === 'string'
        ? error.error['detail']
        : 'The text encoding request could not be completed.',
    };
  }
  return {
    code: 'request_failed',
    detail: 'The text encoding request could not be completed.',
  };
}

function isExpired(expiresAt: string): boolean {
  const expires = Date.parse(expiresAt);
  return !Number.isFinite(expires) || expires <= Date.now();
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null;
}

function freezeContext(context: TextEncodingContext): TextEncodingContext {
  return Object.freeze({
    ...context,
    entries: Object.freeze(context.entries.map((entry) => Object.freeze({ ...entry }))),
  });
}

function closedState(requestToken = 0): TextEncodingState {
  return {
    phase: 'closed',
    context: null,
    sourceEncoding: 'auto',
    outputEncoding: 'utf8',
    preview: null,
    operation: null,
    error: null,
    requestToken,
  };
}
