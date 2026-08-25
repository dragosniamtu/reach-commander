import { DestroyRef, Inject, Injectable, InjectionToken, computed, signal } from '@angular/core';
import {
  CommanderApiPort,
  FileOperationConflictDecision,
  FileOperationPreviewDto,
  FileOperationStatusDto,
} from '../../../core/api/api.models';
import { normalizeLogicalPath } from '../../../core/state/path-utils';
import {
  CapturedFileOperationContext,
  freezeFileOperationContext,
  TransferOperationKind,
} from '../../../core/state/file-operation.models';

const pollMilliseconds = 750;
const terminalPhases = new Set<FileOperationStatusDto['phase']>([
  'completed',
  'completedWithErrors',
  'cancelled',
  'failed',
  'interrupted',
]);

export interface FileOperationScheduler {
  schedule(callback: () => Promise<void> | void, delayMilliseconds: number): unknown;
  cancel(handle: unknown): void;
}

export const FILE_OPERATION_SCHEDULER = new InjectionToken<FileOperationScheduler>(
  'FILE_OPERATION_SCHEDULER',
  {
    providedIn: 'root',
    factory: () => ({
      schedule: (callback, delay) => setTimeout(() => void callback(), delay),
      cancel: (handle) => clearTimeout(handle as ReturnType<typeof setTimeout>),
    }),
  },
);

export type FileOperationDialog = 'closed' | 'confirm' | 'progress';
export type FileOperationPresentation = 'modal' | 'background';
export type FileOperationTerminalHandler = (
  status: FileOperationStatusDto,
  context: CapturedFileOperationContext | null,
) => void;

@Injectable({ providedIn: 'root' })
export class FileOperationStore {
  private readonly contextState = signal<CapturedFileOperationContext | null>(null);
  private readonly destinationState = signal('/');
  private readonly dialogState = signal<FileOperationDialog>('closed');
  private readonly presentationState = signal<FileOperationPresentation>('modal');
  private readonly previewState = signal<FileOperationPreviewDto | null>(null);
  private readonly taskState = signal<readonly FileOperationStatusDto[]>([]);
  private readonly conflictDecisionState = signal<
    ReadonlyMap<string, FileOperationConflictDecision>
  >(new Map());
  private readonly busyState = signal(false);
  private readonly errorState = signal<string | null>(null);
  private readonly activeOperationIdState = signal<string | null>(null);
  private readonly contextsByOperation = new Map<string, CapturedFileOperationContext>();
  private readonly terminalNotified = new Set<string>();
  private pollHandle: unknown | null = null;
  private pollGeneration = 0;
  private previewSequence = 0;
  private lifecycleGeneration = 0;
  private disposed = false;
  private terminalHandler: FileOperationTerminalHandler | null = null;

  readonly context = this.contextState.asReadonly();
  readonly destination = this.destinationState.asReadonly();
  readonly dialog = this.dialogState.asReadonly();
  readonly presentation = this.presentationState.asReadonly();
  readonly preview = this.previewState.asReadonly();
  readonly tasks = this.taskState.asReadonly();
  readonly conflictDecisions = this.conflictDecisionState.asReadonly();
  readonly busy = this.busyState.asReadonly();
  readonly error = this.errorState.asReadonly();
  readonly activeOperationId = this.activeOperationIdState.asReadonly();
  readonly activeTask = computed(() => {
    const operationId = this.activeOperationId();
    return this.tasks().find((task) => task.operationId === operationId) ?? null;
  });
  readonly queuedCount = computed(() =>
    this.tasks().filter((task) => !task.acknowledged && task.phase === 'queued').length,
  );
  readonly canSubmit = computed(() => {
    const preview = this.preview();
    const decisions = this.conflictDecisions();
    return !this.busy() && preview !== null &&
      preview.conflicts.every((conflict) => decisions.has(conflict.conflictId));
  });

  constructor(
    private readonly api: CommanderApiPort,
    @Inject(FILE_OPERATION_SCHEDULER) private readonly scheduler: FileOperationScheduler,
    destroyRef: DestroyRef,
  ) {
    destroyRef.onDestroy(() => this.dispose());
  }

  setTerminalHandler(handler: FileOperationTerminalHandler): void {
    this.terminalHandler = handler;
  }

  async open(
    kind: TransferOperationKind,
    context: CapturedFileOperationContext,
  ): Promise<void> {
    const captured = freezeFileOperationContext({ ...context, kind });
    this.contextState.set(captured);
    this.destinationState.set(captured.destinationLogicalDirectory);
    this.dialogState.set('confirm');
    this.presentationState.set('modal');
    this.previewState.set(null);
    this.conflictDecisionState.set(new Map());
    this.errorState.set(null);
    await this.requestPreview(captured, captured.destinationLogicalDirectory);
  }

  async setDestination(destination: string): Promise<void> {
    const normalized = normalizeLogicalPath(destination);
    const context = this.context();
    this.previewSequence += 1;
    this.destinationState.set(normalized ?? destination);
    this.previewState.set(null);
    this.conflictDecisionState.set(new Map());
    if (!normalized || !context) {
      this.busyState.set(false);
      this.errorState.set(normalized ? null : 'Enter a valid destination path.');
      return;
    }

    await this.requestPreview(context, normalized);
  }

  setConflictDecision(
    conflictId: string,
    decision: FileOperationConflictDecision,
    applyToRemaining = false,
  ): void {
    const preview = this.preview();
    const conflict = preview?.conflicts.find((candidate) => candidate.conflictId === conflictId);
    if (!preview || !conflict?.allowedDecisions.includes(decision)) {
      return;
    }

    const decisions = new Map(this.conflictDecisions());
    decisions.set(conflictId, decision);
    if (applyToRemaining) {
      for (const candidate of preview.conflicts) {
        if (!decisions.has(candidate.conflictId) && candidate.allowedDecisions.includes(decision)) {
          decisions.set(candidate.conflictId, decision);
        }
      }
    }
    this.conflictDecisionState.set(decisions);
  }

  applyDecisionToRemaining(decision: FileOperationConflictDecision): void {
    const preview = this.preview();
    if (!preview) {
      return;
    }

    const decisions = new Map(this.conflictDecisions());
    for (const conflict of preview.conflicts) {
      if (!decisions.has(conflict.conflictId) && conflict.allowedDecisions.includes(decision)) {
        decisions.set(conflict.conflictId, decision);
      }
    }
    this.conflictDecisionState.set(decisions);
  }

  async submit(): Promise<void> {
    const preview = this.preview();
    const context = this.context();
    if (!preview || !context || !this.canSubmit()) {
      return;
    }

    this.busyState.set(true);
    this.errorState.set(null);
    try {
      const decisions = this.conflictDecisions();
      const status = await this.api.submitFileOperation({
        planId: preview.planId,
        resolutions: preview.conflicts.map((conflict) => ({
          conflictId: conflict.conflictId,
          decision: decisions.get(conflict.conflictId)!,
        })),
      });
      this.contextsByOperation.set(status.operationId, context);
      this.activeOperationIdState.set(status.operationId);
      this.dialogState.set('progress');
      this.presentationState.set('modal');
      this.applyStatus(status, context);
    } catch (error: unknown) {
      this.errorState.set(safeError(error));
    } finally {
      this.busyState.set(false);
    }
  }

  track(
    status: FileOperationStatusDto,
    context: CapturedFileOperationContext | null = null,
  ): void {
    if (context) {
      this.contextsByOperation.set(status.operationId, freezeFileOperationContext(context));
    }
    this.applyStatus(status, context);
  }

  async restoreTasks(): Promise<void> {
    const generation = this.lifecycleGeneration;
    this.busyState.set(true);
    this.errorState.set(null);
    try {
      const tasks = await this.api.listFileOperations();
      if (generation !== this.lifecycleGeneration || this.disposed) {
        return;
      }
      this.taskState.set([...tasks]);
      this.schedulePollIfNeeded();
    } catch (error: unknown) {
      if (generation === this.lifecycleGeneration && !this.disposed) {
        this.errorState.set(safeError(error));
      }
    } finally {
      if (generation === this.lifecycleGeneration && !this.disposed) {
        this.busyState.set(false);
      }
    }
  }

  async cancel(operationId = this.activeOperationId()): Promise<void> {
    if (!operationId) {
      return;
    }
    const task = this.tasks().find((candidate) => candidate.operationId === operationId);
    if (!task || isTerminal(task)) {
      return;
    }

    try {
      const status = await this.api.cancelFileOperation(operationId);
      this.applyStatus(status, this.contextsByOperation.get(operationId) ?? null);
    } catch (error: unknown) {
      this.errorState.set(safeError(error));
    }
  }

  async acknowledge(operationId: string): Promise<void> {
    try {
      await this.api.acknowledgeFileOperation(operationId);
      this.taskState.update((tasks) =>
        tasks.filter((candidate) => candidate.operationId !== operationId),
      );
      this.contextsByOperation.delete(operationId);
      this.terminalNotified.delete(operationId);
      if (this.activeOperationId() === operationId) {
        this.activeOperationIdState.set(null);
        this.dialogState.set('closed');
      }
      this.schedulePollIfNeeded();
    } catch (error: unknown) {
      this.errorState.set(safeError(error));
    }
  }

  background(): void {
    if (this.dialog() === 'progress') {
      this.presentationState.set('background');
    }
  }

  restoreProgress(operationId: string): void {
    if (!this.tasks().some((task) => task.operationId === operationId)) {
      return;
    }
    this.activeOperationIdState.set(operationId);
    this.dialogState.set('progress');
    this.presentationState.set('modal');
  }

  closeConfirmation(): void {
    if (this.dialog() === 'confirm') {
      this.previewSequence += 1;
      this.dialogState.set('closed');
      this.contextState.set(null);
      this.previewState.set(null);
      this.conflictDecisionState.set(new Map());
      this.errorState.set(null);
      this.busyState.set(false);
    }
  }

  resetProtectedState(): void {
    this.lifecycleGeneration += 1;
    this.previewSequence += 1;
    this.invalidatePolling();
    this.contextState.set(null);
    this.destinationState.set('/');
    this.dialogState.set('closed');
    this.presentationState.set('modal');
    this.previewState.set(null);
    this.taskState.set([]);
    this.conflictDecisionState.set(new Map());
    this.busyState.set(false);
    this.errorState.set(null);
    this.activeOperationIdState.set(null);
    this.contextsByOperation.clear();
    this.terminalNotified.clear();
  }

  private async requestPreview(
    context: CapturedFileOperationContext,
    destination: string,
  ): Promise<void> {
    const sequence = ++this.previewSequence;
    this.busyState.set(true);
    this.errorState.set(null);
    try {
      const result = await this.api.previewFileOperation({
        kind: context.kind,
        sourceId: context.sourceId,
        logicalPaths: context.logicalPaths,
        destinationSourceId: context.destinationSourceId,
        destinationLogicalDirectory: destination,
      });
      if (sequence === this.previewSequence && this.dialog() === 'confirm') {
        this.previewState.set(result);
        this.conflictDecisionState.set(new Map());
      }
    } catch (error: unknown) {
      if (sequence === this.previewSequence && this.dialog() === 'confirm') {
        this.previewState.set(null);
        this.errorState.set(safeError(error));
      }
    } finally {
      if (sequence === this.previewSequence) {
        this.busyState.set(false);
      }
    }
  }

  private applyStatus(
    status: FileOperationStatusDto,
    context: CapturedFileOperationContext | null,
    schedule = true,
  ): void {
    const previous = this.tasks().find((task) => task.operationId === status.operationId);
    this.taskState.update((tasks) => {
      const index = tasks.findIndex((task) => task.operationId === status.operationId);
      if (index < 0) {
        return [...tasks, status];
      }
      const next = [...tasks];
      next[index] = status;
      return next;
    });

    if (isTerminal(status) && !this.terminalNotified.has(status.operationId) &&
        (!previous || !isTerminal(previous))) {
      this.terminalNotified.add(status.operationId);
      this.terminalHandler?.(
        status,
        context ?? this.contextsByOperation.get(status.operationId) ?? null,
      );
    }
    if (schedule) {
      this.schedulePollIfNeeded();
    }
  }

  private schedulePollIfNeeded(): void {
    if (this.disposed || this.pollHandle !== null) {
      return;
    }
    if (!this.tasks().some((task) => !task.acknowledged && !isTerminal(task))) {
      return;
    }

    const generation = this.pollGeneration;
    this.pollHandle = this.scheduler.schedule(
      () => this.poll(generation),
      pollMilliseconds,
    );
  }

  private async poll(generation: number): Promise<void> {
    this.pollHandle = null;
    if (generation !== this.pollGeneration || this.disposed) {
      return;
    }

    const running = this.tasks().filter((task) => !task.acknowledged && !isTerminal(task));
    const results = await Promise.all(running.map(async (task) => {
      try {
        return await this.api.getFileOperation(task.operationId);
      } catch (error: unknown) {
        this.errorState.set(safeError(error));
        return null;
      }
    }));
    if (generation !== this.pollGeneration || this.disposed) {
      return;
    }

    for (const result of results) {
      if (result) {
        this.applyStatus(
          result,
          this.contextsByOperation.get(result.operationId) ?? null,
          false,
        );
      }
    }
    this.schedulePollIfNeeded();
  }

  private invalidatePolling(): void {
    this.pollGeneration += 1;
    if (this.pollHandle !== null) {
      this.scheduler.cancel(this.pollHandle);
      this.pollHandle = null;
    }
  }

  private dispose(): void {
    this.disposed = true;
    this.lifecycleGeneration += 1;
    this.previewSequence += 1;
    this.invalidatePolling();
  }
}

function isTerminal(status: FileOperationStatusDto): boolean {
  return terminalPhases.has(status.phase);
}

function safeError(error: unknown): string {
  if (typeof error === 'object' && error !== null) {
    const candidate = error as Record<string, unknown>;
    const body = typeof candidate['error'] === 'object' && candidate['error'] !== null
      ? candidate['error'] as Record<string, unknown>
      : candidate;
    if (typeof body['detail'] === 'string') {
      return body['detail'];
    }
  }
  return 'The file operation request could not be completed.';
}
