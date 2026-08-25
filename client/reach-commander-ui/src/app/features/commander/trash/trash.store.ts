import { Injectable, computed, signal } from '@angular/core';
import {
  CommanderApiPort,
  DeletePreviewDto,
  DeletePreviewRequestDto,
  FileOperationConflictDecision,
  RestorePreviewDto,
  TrashEntryDto,
} from '../../../core/api/api.models';
import { FileOperationStore } from '../file-operations/file-operation.store';

@Injectable({ providedIn: 'root' })
export class TrashStore {
  private readonly sourceFilterState = signal<string | null>(null);
  private readonly entryState = signal<readonly TrashEntryDto[]>([]);
  private readonly selectionState = signal<ReadonlySet<string>>(new Set());
  private readonly restorePreviewState = signal<RestorePreviewDto | null>(null);
  private readonly restoreDecisionState = signal<
    ReadonlyMap<string, FileOperationConflictDecision>
  >(new Map());
  private readonly deletePreviewState = signal<DeletePreviewDto | null>(null);
  private readonly deleteRequestState = signal<DeletePreviewRequestDto | null>(null);
  private readonly busyState = signal(false);
  private readonly errorState = signal<string | null>(null);
  private requestSequence = 0;
  private lifecycleGeneration = 0;

  readonly sourceFilter = this.sourceFilterState.asReadonly();
  readonly entries = this.entryState.asReadonly();
  readonly selection = this.selectionState.asReadonly();
  readonly restorePreview = this.restorePreviewState.asReadonly();
  readonly restoreConflictDecisions = this.restoreDecisionState.asReadonly();
  readonly deletePreview = this.deletePreviewState.asReadonly();
  readonly deleteRequest = this.deleteRequestState.asReadonly();
  readonly busy = this.busyState.asReadonly();
  readonly error = this.errorState.asReadonly();
  readonly canSubmitRestore = computed(() => {
    const preview = this.restorePreview();
    const decisions = this.restoreConflictDecisions();
    return !this.busy() && preview !== null &&
      preview.conflicts.every((conflict) => decisions.has(conflict.conflictId));
  });

  constructor(
    private readonly api: CommanderApiPort,
    private readonly operations: FileOperationStore,
  ) {}

  async load(): Promise<void> {
    const sequence = ++this.requestSequence;
    const generation = this.lifecycleGeneration;
    const sourceId = this.sourceFilter() ?? undefined;
    this.busyState.set(true);
    this.errorState.set(null);
    try {
      const entries = await this.api.listTrash(sourceId);
      if (sequence !== this.requestSequence || generation !== this.lifecycleGeneration) {
        return;
      }
      this.entryState.set([...entries]);
      const visibleIds = new Set(entries.map((entry) => entry.trashId));
      this.selectionState.update((selection) =>
        new Set([...selection].filter((trashId) => visibleIds.has(trashId))),
      );
    } catch (error: unknown) {
      if (sequence === this.requestSequence && generation === this.lifecycleGeneration) {
        this.errorState.set(safeError(error));
      }
    } finally {
      if (sequence === this.requestSequence && generation === this.lifecycleGeneration) {
        this.busyState.set(false);
      }
    }
  }

  async setSourceFilter(sourceId: string | null): Promise<void> {
    this.sourceFilterState.set(sourceId);
    this.selectionState.set(new Set());
    this.clearRestorePreview();
    await this.load();
  }

  toggleSelection(trashId: string): void {
    if (!this.entries().some((entry) => entry.trashId === trashId)) {
      return;
    }
    const selection = new Set(this.selection());
    selection.has(trashId) ? selection.delete(trashId) : selection.add(trashId);
    this.selectionState.set(selection);
    this.clearRestorePreview();
  }

  selectAll(): void {
    this.selectionState.set(new Set(this.entries().map((entry) => entry.trashId)));
    this.clearRestorePreview();
  }

  clearSelection(): void {
    this.selectionState.set(new Set());
    this.clearRestorePreview();
  }

  async previewSelectedRestore(): Promise<void> {
    const trashIds = this.selectedIds();
    if (trashIds.length === 0) {
      return;
    }
    const sequence = ++this.requestSequence;
    const generation = this.lifecycleGeneration;
    this.busyState.set(true);
    this.errorState.set(null);
    this.restorePreviewState.set(null);
    this.restoreDecisionState.set(new Map());
    try {
      const preview = await this.api.previewRestore({ trashIds });
      if (sequence === this.requestSequence && generation === this.lifecycleGeneration) {
        this.restorePreviewState.set(preview);
      }
    } catch (error: unknown) {
      if (sequence === this.requestSequence && generation === this.lifecycleGeneration) {
        this.errorState.set(safeError(error));
      }
    } finally {
      if (sequence === this.requestSequence && generation === this.lifecycleGeneration) {
        this.busyState.set(false);
      }
    }
  }

  setRestoreConflictDecision(
    conflictId: string,
    decision: FileOperationConflictDecision,
    applyToRemaining = false,
  ): void {
    const preview = this.restorePreview();
    const conflict = preview?.conflicts.find((candidate) => candidate.conflictId === conflictId);
    if (!preview || !conflict?.allowedDecisions.includes(decision)) {
      return;
    }

    const decisions = new Map(this.restoreConflictDecisions());
    decisions.set(conflictId, decision);
    if (applyToRemaining) {
      for (const candidate of preview.conflicts) {
        if (!decisions.has(candidate.conflictId) && candidate.allowedDecisions.includes(decision)) {
          decisions.set(candidate.conflictId, decision);
        }
      }
    }
    this.restoreDecisionState.set(decisions);
  }

  async submitRestore(): Promise<void> {
    const preview = this.restorePreview();
    if (!preview || !this.canSubmitRestore()) {
      return;
    }
    this.busyState.set(true);
    this.errorState.set(null);
    try {
      const decisions = this.restoreConflictDecisions();
      const operation = await this.api.submitRestore({
        planId: preview.planId,
        resolutions: preview.conflicts.map((conflict) => ({
          conflictId: conflict.conflictId,
          decision: decisions.get(conflict.conflictId)!,
        })),
      });
      this.operations.track(operation);
      this.clearRestorePreview();
    } catch (error: unknown) {
      this.errorState.set(safeError(error));
    } finally {
      this.busyState.set(false);
    }
  }

  async previewDelete(request: DeletePreviewRequestDto): Promise<void> {
    const capturedRequest: DeletePreviewRequestDto = Object.freeze({
      ...request,
      logicalPaths: Object.freeze([...request.logicalPaths]),
    });
    this.deleteRequestState.set(capturedRequest);
    const sequence = ++this.requestSequence;
    const generation = this.lifecycleGeneration;
    this.busyState.set(true);
    this.errorState.set(null);
    this.deletePreviewState.set(null);
    try {
      const preview = await this.api.previewDelete(capturedRequest);
      if (sequence === this.requestSequence && generation === this.lifecycleGeneration) {
        this.deletePreviewState.set(preview);
      }
    } catch (error: unknown) {
      if (sequence === this.requestSequence && generation === this.lifecycleGeneration) {
        this.errorState.set(safeError(error));
      }
    } finally {
      if (sequence === this.requestSequence && generation === this.lifecycleGeneration) {
        this.busyState.set(false);
      }
    }
  }

  async changeDeleteMode(mode: DeletePreviewRequestDto['mode']): Promise<void> {
    const request = this.deleteRequest();
    if (request && request.mode !== mode) {
      await this.previewDelete({ ...request, mode });
    }
  }

  clearDeletePreview(): void {
    this.requestSequence += 1;
    this.deletePreviewState.set(null);
    this.deleteRequestState.set(null);
    this.errorState.set(null);
    this.busyState.set(false);
  }

  async submitDelete(permanentDeleteConfirmed: boolean): Promise<void> {
    const preview = this.deletePreview();
    if (!preview || (preview.mode === 'permanent' && !permanentDeleteConfirmed)) {
      return;
    }
    this.busyState.set(true);
    this.errorState.set(null);
    try {
      const operation = await this.api.submitDelete({
        planId: preview.planId,
        permanentDeleteConfirmed,
      });
      this.operations.track(operation);
      this.deletePreviewState.set(null);
      this.deleteRequestState.set(null);
    } catch (error: unknown) {
      this.errorState.set(safeError(error));
    } finally {
      this.busyState.set(false);
    }
  }

  async permanentlyDeleteSelected(permanentDeleteConfirmed: boolean): Promise<void> {
    const trashIds = this.selectedIds();
    if (!permanentDeleteConfirmed || trashIds.length === 0) {
      return;
    }
    await this.runQueuedRequest(() => this.api.permanentlyDeleteTrash({
      trashIds,
      permanentDeleteConfirmed,
    }));
  }

  async emptyTrash(permanentDeleteConfirmed: boolean): Promise<void> {
    if (!permanentDeleteConfirmed) {
      return;
    }
    await this.runQueuedRequest(() => this.api.emptyTrash({
      sourceId: this.sourceFilter(),
      permanentDeleteConfirmed,
    }));
  }

  resetProtectedState(): void {
    this.lifecycleGeneration += 1;
    this.requestSequence += 1;
    this.sourceFilterState.set(null);
    this.entryState.set([]);
    this.selectionState.set(new Set());
    this.restorePreviewState.set(null);
    this.restoreDecisionState.set(new Map());
    this.deletePreviewState.set(null);
    this.deleteRequestState.set(null);
    this.busyState.set(false);
    this.errorState.set(null);
  }

  private async runQueuedRequest(
    request: () => ReturnType<CommanderApiPort['emptyTrash']>,
  ): Promise<void> {
    this.busyState.set(true);
    this.errorState.set(null);
    try {
      this.operations.track(await request());
    } catch (error: unknown) {
      this.errorState.set(safeError(error));
    } finally {
      this.busyState.set(false);
    }
  }

  private selectedIds(): readonly string[] {
    const selection = this.selection();
    return this.entries()
      .filter((entry) => selection.has(entry.trashId))
      .map((entry) => entry.trashId);
  }

  private clearRestorePreview(): void {
    this.restorePreviewState.set(null);
    this.restoreDecisionState.set(new Map());
  }
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
  return 'The Trash request could not be completed.';
}
