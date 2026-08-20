import { HttpErrorResponse } from '@angular/common/http';
import { DestroyRef, Inject, Injectable, InjectionToken, computed, signal } from '@angular/core';
import {
  ArchiveExtractionOperationDto,
  ArchiveExtractionPreviewDto,
  CommanderApiPort,
} from '../api/api.models';
import {
  ArchiveExtractionContext,
  ArchiveExtractionPhase,
  ArchiveExtractionSafeError,
} from './archive-extraction.models';
import { PanelSide } from './commander.models';

const pollMilliseconds = 500;
const retryableReviewCodes = new Set([
  'archive_capacity_reached',
  'archive_plan_expired',
  'archive_plan_stale',
  'archive_destination_changed',
  'archive_destination_conflict',
]);
const executeRetryCodes = new Set(['archive_capacity_reached']);
const terminalPollingCodes = new Set(['archive_plan_not_found']);

export interface ArchiveExtractionScheduler {
  schedule(callback: () => Promise<void> | void, delayMilliseconds: number): unknown;
  cancel(handle: unknown): void;
}

export const ARCHIVE_EXTRACTION_SCHEDULER = new InjectionToken<ArchiveExtractionScheduler>(
  'ARCHIVE_EXTRACTION_SCHEDULER',
  {
    providedIn: 'root',
    factory: () => ({
      schedule: (callback, delay) => setTimeout(() => void callback(), delay),
      cancel: (handle) => clearTimeout(handle as ReturnType<typeof setTimeout>),
    }),
  },
);

export interface ArchiveExtractionState {
  readonly phase: ArchiveExtractionPhase;
  readonly context: ArchiveExtractionContext | null;
  readonly preview: ArchiveExtractionPreviewDto | null;
  readonly operation: ArchiveExtractionOperationDto | null;
  readonly error: ArchiveExtractionSafeError | null;
  readonly requestToken: number;
}

@Injectable({ providedIn: 'root' })
export class ArchiveExtractionStore {
  private readonly mutableState = signal<ArchiveExtractionState>(closedState());
  private pollHandle: unknown | null = null;
  private pollGeneration = 0;
  private nextRequestToken = 0;
  private completionHandler: ((source: PanelSide, destination: PanelSide) => void) | null = null;

  readonly state = this.mutableState.asReadonly();
  readonly canExecute = computed(() => {
    const state = this.state();
    return state.phase === 'review' && state.preview?.canExecute === true &&
      (state.error === null || executeRetryCodes.has(state.error.code));
  });
  readonly canCancel = computed(() => {
    const state = this.state();
    return state.phase === 'running' && state.operation?.canCancel === true &&
      state.operation.state !== 'finalizing';
  });

  constructor(
    private readonly api: CommanderApiPort,
    @Inject(ARCHIVE_EXTRACTION_SCHEDULER) private readonly scheduler: ArchiveExtractionScheduler,
    destroyRef: DestroyRef,
  ) {
    destroyRef.onDestroy(() => this.dispose());
  }

  setCompletionHandler(handler: (source: PanelSide, destination: PanelSide) => void): void {
    this.completionHandler = handler;
  }

  async open(context: ArchiveExtractionContext): Promise<void> {
    this.invalidatePolling();
    const captured = Object.freeze({
      ...context,
      entryPaths: Object.freeze([...context.entryPaths]),
    });
    const requestToken = ++this.nextRequestToken;
    this.mutableState.set({
      phase: 'previewing',
      context: captured,
      preview: null,
      operation: null,
      error: null,
      requestToken,
    });
    await this.preview(requestToken, captured);
  }

  async reviewAgain(): Promise<void> {
    const context = this.state().context;
    if (context) {
      await this.open(context);
    }
  }

  async execute(): Promise<void> {
    const state = this.state();
    if (!this.canExecute() || !state.preview || !state.context) {
      return;
    }

    const token = state.requestToken;
    const context = state.context;
    this.mutableState.set({ ...state, phase: 'starting', error: null });
    try {
      const operation = await this.api.executeArchiveExtraction(state.preview.planId);
      if (!this.isCurrent(token, context)) {
        return;
      }
      this.applyOperation(operation, token, context);
    } catch (error: unknown) {
      if (!this.isCurrent(token, context)) {
        return;
      }
      const safe = safeProblem(error);
      this.mutableState.set({
        ...this.state(),
        phase: retryableReviewCodes.has(safe.code) ? 'review' : 'failed',
        error: safe,
      });
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
      const operation = await this.api.cancelArchiveExtraction(state.operation.operationId);
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
    this.invalidatePolling();
    this.mutableState.set(closedState(++this.nextRequestToken));
  }

  private async preview(token: number, context: ArchiveExtractionContext): Promise<void> {
    try {
      const preview = await this.api.previewArchiveExtraction({
        sourceId: context.sourceId,
        archivePath: context.archivePath,
        internalDirectory: context.internalDirectory,
        entryPaths: context.entryPaths,
        extractAll: context.extractAll,
        destinationSourceId: context.destinationSourceId,
        destinationPath: context.destinationPath,
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

  private schedulePoll(token: number, context: ArchiveExtractionContext): void {
    this.clearPoll();
    const generation = this.pollGeneration;
    this.pollHandle = this.scheduler.schedule(
      () => this.poll(token, context, generation),
      pollMilliseconds,
    );
  }

  private async poll(
    token: number,
    context: ArchiveExtractionContext,
    generation: number,
  ): Promise<void> {
    this.pollHandle = null;
    const operationId = this.state().operation?.operationId;
    if (!operationId || !this.isCurrentPoll(token, context, generation)) {
      return;
    }

    try {
      const operation = await this.api.getArchiveExtraction(operationId);
      if (this.isCurrentPoll(token, context, generation)) {
        this.applyOperation(operation, token, context);
      }
    } catch (error: unknown) {
      if (this.isCurrentPoll(token, context, generation)) {
        const safe = safeProblem(error);
        if (terminalPollingCodes.has(safe.code)) {
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
    operation: ArchiveExtractionOperationDto,
    token: number,
    context: ArchiveExtractionContext,
  ): void {
    const phase = operationPhase(operation);
    this.mutableState.set({
      ...this.state(),
      phase,
      operation,
      error: operation.errorCode
        ? { code: operation.errorCode, detail: operation.errorDetail ?? 'Extraction failed.' }
        : operationFailure(phase, operation.errorDetail),
    });
    if (phase === 'running') {
      this.schedulePoll(token, context);
    } else {
      this.clearPoll();
      if (phase === 'completed') {
        this.completionHandler?.(context.sourcePanelSide, context.destinationPanelSide);
      }
    }
  }

  private isCurrent(token: number, context: ArchiveExtractionContext): boolean {
    const state = this.state();
    return state.requestToken === token && state.context === context && state.phase !== 'closed';
  }

  private isCurrentPoll(
    token: number,
    context: ArchiveExtractionContext,
    generation: number,
  ): boolean {
    return generation === this.pollGeneration && this.isCurrent(token, context);
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

  private dispose(): void {
    this.invalidatePolling();
    this.nextRequestToken++;
  }
}

function operationPhase(operation: ArchiveExtractionOperationDto): ArchiveExtractionPhase {
  switch (operation.state) {
    case 'queued':
    case 'extracting':
    case 'finalizing':
      return 'running';
    case 'completed':
      return 'completed';
    case 'cancelled':
      return 'cancelled';
    case 'recoveryRequired':
      return 'recoveryRequired';
    case 'failed':
      return 'failed';
  }
}

function safeProblem(error: unknown): ArchiveExtractionSafeError {
  if (error instanceof HttpErrorResponse && isRecord(error.error)) {
    return {
      code: typeof error.error['code'] === 'string' ? error.error['code'] : 'request_failed',
      detail: typeof error.error['detail'] === 'string'
        ? error.error['detail']
        : 'The extraction request could not be completed.',
    };
  }
  return { code: 'request_failed', detail: 'The extraction request could not be completed.' };
}

function operationFailure(
  phase: ArchiveExtractionPhase,
  detail: string | null,
): ArchiveExtractionSafeError | null {
  if (phase !== 'failed' && phase !== 'recoveryRequired') {
    return null;
  }
  return {
    code: phase === 'recoveryRequired' ? 'archive_recovery_required' : 'archive_extraction_failed',
    detail: detail ?? (phase === 'recoveryRequired'
      ? 'Extraction requires administrator recovery.'
      : 'Extraction failed.'),
  };
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null;
}

function closedState(requestToken = 0): ArchiveExtractionState {
  return {
    phase: 'closed',
    context: null,
    preview: null,
    operation: null,
    error: null,
    requestToken,
  };
}
