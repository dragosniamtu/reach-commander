import { HttpErrorResponse } from '@angular/common/http';
import { computed, Injectable, signal } from '@angular/core';
import { BatchRenamePreviewDto, BatchRenameRulesDto, CommanderApiPort } from '../api/api.models';
import { MultiRenameContext, MultiRenameState } from './multi-rename.models';

const previewDebounceMilliseconds = 250;
const knownProblemCodes = new Set([
  'invalid_rename_rule',
  'batch_too_large',
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
export class MultiRenameStore {
  private readonly mutableState = signal<MultiRenameState>(closedState());
  private nextRequestToken = 0;
  private debounceTimer: ReturnType<typeof setTimeout> | null = null;
  private expiryTimer: ReturnType<typeof setTimeout> | null = null;

  readonly state = this.mutableState.asReadonly();
  readonly canExecute = computed(() => {
    const state = this.state();
    return (
      state.open &&
      state.disabledReason === null &&
      !state.previewPending &&
      !state.actionPending &&
      state.operation === null &&
      state.preview?.canExecute === true &&
      state.preview.changedCount > 0
    );
  });
  readonly canUndo = computed(() => {
    const operation = this.state().operation;
    return (
      !this.state().actionPending && operation?.status === 'completed' && operation.undoAvailable
    );
  });

  constructor(private readonly api: CommanderApiPort) {}

  open(context: MultiRenameContext): void {
    this.clearTimers();
    const capturedContext: MultiRenameContext = Object.freeze({
      ...context,
      entries: Object.freeze([...context.entries]),
    });
    const requestToken = ++this.nextRequestToken;
    const disabledReason = context.isAvailable
      ? context.isReadOnly
        ? 'This source is read-only.'
        : context.entries.length === 0
          ? 'Select at least one entry to rename.'
          : null
      : 'This source is unavailable.';
    this.mutableState.set({
      open: true,
      context: capturedContext,
      rules: defaultRules(),
      preview: null,
      operation: null,
      previewPending: disabledReason === null,
      actionPending: false,
      disabledReason,
      errorCode: null,
      requestToken,
    });
    if (disabledReason === null) {
      this.schedulePreview(requestToken, capturedContext);
    }
  }

  updateRules(update: Partial<BatchRenameRulesDto>): void {
    const state = this.state();
    if (!state.open || state.context === null || state.operation !== null || state.disabledReason) {
      return;
    }

    this.clearDebounceTimer();
    this.clearExpiryTimer();
    const requestToken = ++this.nextRequestToken;
    const rules = Object.freeze({ ...state.rules, ...update });
    this.mutableState.set({
      ...state,
      rules,
      operation: null,
      previewPending: true,
      actionPending: false,
      errorCode: null,
      requestToken,
    });
    this.schedulePreview(requestToken, state.context);
  }

  async execute(): Promise<boolean> {
    const state = this.state();
    if (!this.canExecute() || state.preview === null || state.context === null) {
      return false;
    }

    const context = state.context;
    this.mutableState.set({ ...state, actionPending: true, errorCode: null });
    try {
      const operation = await this.api.executeBatchRename(state.preview.planId);
      const current = this.state();
      if (!current.open || current.context !== context) {
        return false;
      }

      this.clearExpiryTimer();
      this.mutableState.set({
        ...current,
        operation,
        previewPending: false,
        actionPending: false,
        errorCode: null,
      });
      return true;
    } catch (error: unknown) {
      this.failAction(error, context);
      return false;
    }
  }

  async undo(): Promise<boolean> {
    const state = this.state();
    if (!this.canUndo() || state.operation === null || state.context === null) {
      return false;
    }

    const context = state.context;
    this.mutableState.set({ ...state, actionPending: true, errorCode: null });
    try {
      const operation = await this.api.undoBatchRename(state.operation.operationId);
      const current = this.state();
      if (!current.open || current.context !== context) {
        return false;
      }

      this.mutableState.set({
        ...current,
        operation,
        actionPending: false,
        errorCode: null,
      });
      return true;
    } catch (error: unknown) {
      this.failAction(error, context);
      return false;
    }
  }

  close(): void {
    this.clearTimers();
    this.mutableState.set(closedState(++this.nextRequestToken));
  }

  private schedulePreview(requestToken: number, context: MultiRenameContext): void {
    this.debounceTimer = setTimeout(() => {
      this.debounceTimer = null;
      void this.requestPreview(requestToken, context);
    }, previewDebounceMilliseconds);
  }

  private async requestPreview(requestToken: number, context: MultiRenameContext): Promise<void> {
    const state = this.state();
    if (!state.open || state.requestToken !== requestToken || state.context !== context) {
      return;
    }

    try {
      const preview = await this.api.previewBatchRename({
        sourceId: context.sourceId,
        directoryPath: context.directoryPath,
        entryPaths: context.entries.map((entry) => entry.relativePath),
        rules: state.rules,
      });
      const current = this.state();
      if (!current.open || current.requestToken !== requestToken || current.context !== context) {
        return;
      }

      this.mutableState.set({
        ...current,
        preview,
        previewPending: false,
        errorCode: null,
      });
      this.scheduleExpiry(preview, requestToken, context);
    } catch (error: unknown) {
      const current = this.state();
      if (current.open && current.requestToken === requestToken && current.context === context) {
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
    context: MultiRenameContext,
  ): void {
    this.clearExpiryTimer();
    const expiresAt = Date.parse(preview.expiresAt);
    const delay = Number.isFinite(expiresAt) ? Math.max(0, expiresAt - Date.now()) : 0;
    this.expiryTimer = setTimeout(() => {
      this.expiryTimer = null;
      const current = this.state();
      if (
        current.requestToken !== requestToken ||
        current.context !== context ||
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
            message: 'This preview has expired. Update a rule to refresh it.',
          })),
          canExecute: false,
          invalidCount: current.preview.rows.length,
        },
        errorCode: 'rename_plan_expired',
      });
    }, delay);
  }

  private failAction(error: unknown, context: MultiRenameContext): void {
    const current = this.state();
    if (current.open && current.context === context) {
      this.mutableState.set({
        ...current,
        actionPending: false,
        errorCode: problemCode(error),
      });
    }
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

function defaultRules(): BatchRenameRulesDto {
  return {
    nameMask: '[N]',
    extensionMask: '[E]',
    searchFor: '',
    replaceWith: '',
    useRegex: false,
    matchCase: false,
    replaceInExtension: false,
    caseMode: 'unchanged',
    counterStart: 1,
    counterStep: 1,
    counterDigits: 1,
  };
}

function closedState(requestToken = 0): MultiRenameState {
  return {
    open: false,
    context: null,
    rules: defaultRules(),
    preview: null,
    operation: null,
    previewPending: false,
    actionPending: false,
    disabledReason: null,
    errorCode: null,
    requestToken,
  };
}
