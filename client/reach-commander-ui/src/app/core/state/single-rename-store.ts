import { HttpErrorResponse } from '@angular/common/http';
import { computed, Injectable, signal } from '@angular/core';
import { BatchRenamePreviewDto, CommanderApiPort } from '../api/api.models';
import {
  SingleRenameCompletion,
  SingleRenameContext,
  SingleRenameState,
} from './single-rename.models';

const previewDebounceMilliseconds = 250;
const knownProblemCodes = new Set([
  'invalid_rename_rule',
  'source_read_only',
  'source_not_found',
  'source_unavailable',
  'invalid_path',
  'path_forbidden',
  'entry_not_found',
  'rename_plan_not_found',
  'rename_plan_expired',
  'rename_plan_stale',
  'rename_recovery_required',
]);

@Injectable({ providedIn: 'root' })
export class SingleRenameStore {
  private readonly mutableState = signal<SingleRenameState>(closedState());
  private nextRequestToken = 0;
  private debounceTimer: ReturnType<typeof setTimeout> | null = null;
  private expiryTimer: ReturnType<typeof setTimeout> | null = null;
  private completionHandler: ((completion: SingleRenameCompletion) => void) | null = null;

  readonly state = this.mutableState.asReadonly();
  readonly canExecute = computed(() => {
    const state = this.state();
    return (
      state.open &&
      state.context !== null &&
      state.preview?.canExecute === true &&
      state.preview.changedCount === 1 &&
      state.operation === null &&
      !state.previewPending &&
      !state.actionPending
    );
  });

  constructor(private readonly api: CommanderApiPort) {}

  setCompletionHandler(handler: (completion: SingleRenameCompletion) => void): void {
    this.completionHandler = handler;
  }

  open(context: SingleRenameContext): void {
    this.clearTimers();
    const capturedContext: SingleRenameContext = Object.freeze({
      ...context,
      entry: Object.freeze({ ...context.entry }),
    });
    const requestToken = ++this.nextRequestToken;
    this.mutableState.set({
      open: true,
      context: capturedContext,
      newName: capturedContext.entry.name,
      preview: null,
      operation: null,
      previewPending: true,
      actionPending: false,
      errorCode: null,
      requestToken,
    });
    this.schedulePreview(requestToken, capturedContext, capturedContext.entry.name);
  }

  setName(newName: string): void {
    const state = this.state();
    if (!state.open || state.context === null || state.actionPending) {
      return;
    }

    this.clearTimers();
    const requestToken = ++this.nextRequestToken;
    const previewPending = newName.length > 0;
    this.mutableState.set({
      ...state,
      newName,
      preview: null,
      operation: null,
      previewPending,
      actionPending: false,
      errorCode: null,
      requestToken,
    });
    if (previewPending) {
      this.schedulePreview(requestToken, state.context, newName);
    }
  }

  async execute(): Promise<boolean> {
    const state = this.state();
    if (!this.canExecute() || state.context === null || state.preview === null) {
      return false;
    }

    const context = state.context;
    const requestToken = state.requestToken;
    const planId = state.preview.planId;
    this.mutableState.set({ ...state, actionPending: true, errorCode: null });
    try {
      const operation = await this.api.executeBatchRename(planId);
      const current = this.state();
      if (
        !current.open ||
        current.context !== context ||
        current.requestToken !== requestToken ||
        current.preview?.planId !== planId
      ) {
        return false;
      }

      const completedRows = operation.rows.filter((row) => row.result === 'completed');
      const completed = operation.status === 'completed' && completedRows.length === 1;
      this.clearExpiryTimer();
      this.mutableState.set({
        ...current,
        operation,
        previewPending: false,
        actionPending: false,
        errorCode: completed
          ? null
          : operation.recoveryRequired
            ? 'rename_recovery_required'
            : 'request_failed',
      });
      if (!completed) {
        return false;
      }

      this.completionHandler?.({
        context,
        newLogicalPath: completedRows[0]!.newPath,
      });
      return true;
    } catch (error: unknown) {
      const current = this.state();
      if (
        current.open &&
        current.context === context &&
        current.requestToken === requestToken
      ) {
        this.mutableState.set({
          ...current,
          actionPending: false,
          errorCode: problemCode(error),
        });
      }
      return false;
    }
  }

  close(): void {
    this.clearTimers();
    this.mutableState.set(closedState(++this.nextRequestToken));
  }

  private schedulePreview(
    requestToken: number,
    context: SingleRenameContext,
    newName: string,
  ): void {
    this.debounceTimer = setTimeout(() => {
      this.debounceTimer = null;
      void this.requestPreview(requestToken, context, newName);
    }, previewDebounceMilliseconds);
  }

  private async requestPreview(
    requestToken: number,
    context: SingleRenameContext,
    newName: string,
  ): Promise<void> {
    const state = this.state();
    if (
      !state.open ||
      state.context !== context ||
      state.requestToken !== requestToken ||
      state.newName !== newName
    ) {
      return;
    }

    try {
      const preview = await this.api.previewRename({
        sourceId: context.sourceId,
        directoryPath: context.directoryPath,
        entryPath: context.entry.relativePath,
        newName,
      });
      const current = this.state();
      if (
        !current.open ||
        current.context !== context ||
        current.requestToken !== requestToken ||
        current.newName !== newName
      ) {
        return;
      }

      this.mutableState.set({
        ...current,
        preview,
        previewPending: false,
        errorCode: null,
      });
      this.scheduleExpiry(preview, requestToken, context, newName);
    } catch (error: unknown) {
      const current = this.state();
      if (
        current.open &&
        current.context === context &&
        current.requestToken === requestToken &&
        current.newName === newName
      ) {
        this.mutableState.set({
          ...current,
          preview: null,
          previewPending: false,
          errorCode: problemCode(error),
        });
      }
    }
  }

  private scheduleExpiry(
    preview: BatchRenamePreviewDto,
    requestToken: number,
    context: SingleRenameContext,
    newName: string,
  ): void {
    this.clearExpiryTimer();
    const expiresAt = Date.parse(preview.expiresAt);
    const delay = Number.isFinite(expiresAt) ? Math.max(0, expiresAt - Date.now()) : 0;
    this.expiryTimer = setTimeout(() => {
      this.expiryTimer = null;
      const current = this.state();
      if (
        !current.open ||
        current.context !== context ||
        current.requestToken !== requestToken ||
        current.newName !== newName ||
        current.preview?.planId !== preview.planId ||
        current.operation !== null
      ) {
        return;
      }

      this.mutableState.set({
        ...current,
        preview: {
          ...current.preview,
          rows: current.preview.rows.map((row) => ({
            ...row,
            status: 'stale' as const,
            message: 'This preview has expired. Enter the name again to refresh it.',
          })),
          canExecute: false,
          changedCount: 0,
          invalidCount: current.preview.rows.length,
        },
        errorCode: 'rename_plan_expired',
      });
    }, delay);
  }

  private clearTimers(): void {
    this.clearDebounceTimer();
    this.clearExpiryTimer();
  }

  private clearDebounceTimer(): void {
    if (this.debounceTimer !== null) {
      clearTimeout(this.debounceTimer);
      this.debounceTimer = null;
    }
  }

  private clearExpiryTimer(): void {
    if (this.expiryTimer !== null) {
      clearTimeout(this.expiryTimer);
      this.expiryTimer = null;
    }
  }
}

function problemCode(error: unknown): string {
  if (
    error instanceof HttpErrorResponse &&
    typeof error.error === 'object' &&
    error.error !== null &&
    'code' in error.error &&
    typeof error.error.code === 'string' &&
    knownProblemCodes.has(error.error.code)
  ) {
    return error.error.code;
  }

  return 'request_failed';
}

function closedState(requestToken = 0): SingleRenameState {
  return {
    open: false,
    context: null,
    newName: '',
    preview: null,
    operation: null,
    previewPending: false,
    actionPending: false,
    errorCode: null,
    requestToken,
  };
}
