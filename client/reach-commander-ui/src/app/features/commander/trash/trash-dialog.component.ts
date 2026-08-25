import { A11yModule } from '@angular/cdk/a11y';
import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, HostListener, OnInit, computed, inject, input, output, signal } from '@angular/core';
import { FileOperationConflictDecision, SourceDto } from '../../../core/api/api.models';
import { FileSizePipe } from '../../../shared/pipes/file-size.pipe';
import { PERMANENT_DELETE_WARNING } from './delete-dialog.component';
import { TrashStore } from './trash.store';

@Component({
  selector: 'app-trash-dialog',
  imports: [A11yModule, DatePipe, FileSizePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './trash-dialog.component.html',
  styleUrl: './trash-dialog.component.scss',
})
export class TrashDialogComponent implements OnInit {
  readonly store = inject(TrashStore);
  readonly sources = input.required<readonly SourceDto[]>();
  readonly closeRequested = output<void>();
  readonly permanentDeleteConfirmation = signal(false);
  readonly permanentDeleteConfirmed = signal(false);
  readonly emptyConfirmation = signal(false);
  readonly emptyConfirmed = signal(false);
  readonly lastRestoreDecision = signal<FileOperationConflictDecision | null>(null);
  readonly warning = PERMANENT_DELETE_WARNING;
  readonly selectedCount = computed(() => this.store.selection().size);

  ngOnInit(): void {
    void this.store.load();
  }

  sourceName(sourceId: string): string {
    return this.sources().find((source) => source.id === sourceId)?.name ?? sourceId;
  }

  filterChanged(event: Event): void {
    const value = (event.target as HTMLSelectElement).value;
    this.cancelDestructiveConfirmations();
    void this.store.setSourceFilter(value || null);
  }

  toggle(trashId: string): void {
    this.cancelDestructiveConfirmations();
    this.store.toggleSelection(trashId);
  }

  previewRestore(): void {
    void this.store.previewSelectedRestore();
  }

  restoreDecisionChanged(conflictId: string, event: Event): void {
    const value = (event.target as HTMLSelectElement).value;
    if (!isDecision(value)) return;
    this.lastRestoreDecision.set(value);
    this.store.setRestoreConflictDecision(conflictId, value);
  }

  applyRestoreRemaining(event: Event): void {
    if (!(event.target as HTMLInputElement).checked) return;
    const decision = this.lastRestoreDecision();
    if (!decision) return;
    const preview = this.store.restorePreview();
    const firstUnresolved = preview?.conflicts.find(
      (conflict) => !this.store.restoreConflictDecisions().has(conflict.conflictId) &&
        conflict.allowedDecisions.includes(decision),
    );
    if (firstUnresolved) {
      this.store.setRestoreConflictDecision(firstUnresolved.conflictId, decision, true);
    }
  }

  restore(): void { void this.store.submitRestore(); }

  showPermanentDeleteConfirmation(): void {
    if (this.selectedCount() > 0) {
      this.permanentDeleteConfirmation.set(true);
      this.emptyConfirmation.set(false);
    }
  }

  confirmPermanentDelete(): void {
    if (this.permanentDeleteConfirmed()) {
      void this.store.permanentlyDeleteSelected(true);
    }
  }

  showEmptyConfirmation(): void {
    this.emptyConfirmation.set(true);
    this.permanentDeleteConfirmation.set(false);
  }

  cancelEmptyConfirmation(): void {
    this.emptyConfirmation.set(false);
    this.emptyConfirmed.set(false);
  }

  confirmEmpty(): void {
    if (this.emptyConfirmed()) {
      void this.store.emptyTrash(true);
    }
  }

  emptyLabel(): string {
    const sourceId = this.store.sourceFilter();
    return sourceId ? `Empty Trash for ${this.sourceName(sourceId)}` : 'Empty Trash for all sources';
  }

  requestClose(): void {
    if (!this.store.busy()) {
      this.closeRequested.emit();
    }
  }

  @HostListener('keydown', ['$event'])
  handleKeydown(event: KeyboardEvent): void {
    event.stopPropagation();
    if (event.key === 'Escape') {
      event.preventDefault();
      if (this.emptyConfirmation()) this.cancelEmptyConfirmation();
      else if (this.permanentDeleteConfirmation()) this.cancelDestructiveConfirmations();
      else this.requestClose();
    }
  }

  private cancelDestructiveConfirmations(): void {
    this.permanentDeleteConfirmation.set(false);
    this.permanentDeleteConfirmed.set(false);
    this.emptyConfirmation.set(false);
    this.emptyConfirmed.set(false);
  }
}

function isDecision(value: string): value is FileOperationConflictDecision {
  return value === 'overwrite' || value === 'skip' || value === 'createUniqueName';
}
