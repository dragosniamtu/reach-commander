import { A11yModule } from '@angular/cdk/a11y';
import {
  ChangeDetectionStrategy,
  Component,
  HostListener,
  computed,
  inject,
  output,
  signal,
} from '@angular/core';
import { BatchRenameCaseMode } from '../../core/api/api.models';
import { MultiRenameStore } from '../../core/state/multi-rename-store';
import { PanelSide } from '../../core/state/commander.models';
import { RenameMaskFieldComponent, RenameMaskToken } from './rename-mask-field.component';
import { MultiRenamePreviewTableComponent } from './multi-rename-preview-table.component';

const errorMessages: Readonly<Record<string, string>> = {
  invalid_rename_rule: 'Review the rename masks and search settings.',
  batch_too_large: 'Too many entries are selected for one rename operation.',
  source_read_only: 'This source is read-only.',
  source_not_found: 'The selected source no longer exists.',
  source_unavailable: 'The selected source is unavailable.',
  invalid_path: 'One or more selected paths are not valid.',
  path_forbidden: 'One or more selected paths are not allowed.',
  entry_not_found: 'One or more selected entries no longer exist.',
  rename_plan_not_found: 'This preview is no longer available. Update a rule to refresh it.',
  rename_plan_expired: 'This preview has expired. Update a rule to refresh it.',
  rename_plan_stale: 'The folder changed. Update a rule to create a fresh preview.',
  rename_recovery_required: 'Recovery is required. Review the logical recovery list.',
  request_failed: 'The rename request could not be completed.',
};

@Component({
  selector: 'app-multi-rename-dialog',
  imports: [A11yModule, RenameMaskFieldComponent, MultiRenamePreviewTableComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './multi-rename-dialog.component.html',
  styleUrl: './multi-rename-dialog.component.scss',
})
export class MultiRenameDialogComponent {
  readonly store = inject(MultiRenameStore);
  readonly closeRequested = output<void>();
  readonly filesystemChanged = output<PanelSide>();
  readonly recoveryReviewed = signal(false);
  readonly context = computed(() => this.store.state().context);
  readonly rulesLocked = computed(
    () => this.store.state().operation !== null || this.store.state().actionPending,
  );
  readonly canClose = computed(() => {
    const state = this.store.state();
    return (
      !state.previewPending &&
      !state.actionPending &&
      (!state.operation?.recoveryRequired || this.recoveryReviewed())
    );
  });
  readonly summary = computed(() => {
    const state = this.store.state();
    const operation = state.operation;
    if (operation?.status === 'completed') {
      const count = operation.rows.filter((row) => row.result === 'completed').length;
      return `${count} ${count === 1 ? 'entry' : 'entries'} renamed`;
    }
    if (operation?.status === 'undone') {
      return 'Undo completed';
    }
    if (operation?.status === 'recoveryRequired') {
      return 'Recovery required';
    }
    if (operation?.status === 'failed') {
      return 'Rename failed · completed changes were rolled back';
    }
    if (state.previewPending) {
      return 'Refreshing preview…';
    }
    const preview = state.preview;
    if (!preview) {
      return `${state.context?.entries.length ?? 0} selected`;
    }
    return `${preview.changedCount} ready · ${preview.unchangedCount} unchanged · ${preview.invalidCount} blocked`;
  });
  readonly nameTokens: readonly RenameMaskToken[] = [
    { label: 'Name', value: '[N]' },
    { label: 'Range', value: '[N1-3]' },
    { label: 'Counter', value: '[C]' },
  ];
  readonly extensionTokens: readonly RenameMaskToken[] = [
    { label: 'Extension', value: '[E]' },
    { label: 'Range', value: '[E1-3]' },
    { label: 'Counter', value: '[C]' },
  ];

  setNameMask(nameMask: string): void {
    this.store.updateRules({ nameMask });
  }

  setExtensionMask(extensionMask: string): void {
    this.store.updateRules({ extensionMask });
  }

  setSearchFor(searchFor: string): void {
    this.store.updateRules({ searchFor });
  }

  setReplaceWith(replaceWith: string): void {
    this.store.updateRules({ replaceWith });
  }

  setUseRegex(useRegex: boolean): void {
    this.store.updateRules({ useRegex });
  }

  setMatchCase(matchCase: boolean): void {
    this.store.updateRules({ matchCase });
  }

  setReplaceInExtension(replaceInExtension: boolean): void {
    this.store.updateRules({ replaceInExtension });
  }

  setCaseMode(caseMode: BatchRenameCaseMode): void {
    this.store.updateRules({ caseMode });
  }

  setCounterStart(counterStart: number): void {
    if (Number.isFinite(counterStart)) {
      this.store.updateRules({ counterStart });
    }
  }

  setCounterStep(counterStep: number): void {
    if (Number.isFinite(counterStep)) {
      this.store.updateRules({ counterStep });
    }
  }

  setCounterDigits(counterDigits: number): void {
    if (Number.isFinite(counterDigits)) {
      this.store.updateRules({ counterDigits });
    }
  }

  async start(): Promise<void> {
    const side = this.context()?.panelSide;
    if (
      side &&
      (await this.store.execute()) &&
      this.store.state().operation?.status === 'completed'
    ) {
      this.filesystemChanged.emit(side);
    }
  }

  async undo(): Promise<void> {
    const side = this.context()?.panelSide;
    if (side && (await this.store.undo()) && this.store.state().operation?.status === 'undone') {
      this.filesystemChanged.emit(side);
    }
  }

  requestClose(): void {
    if (this.canClose()) {
      this.closeRequested.emit();
    }
  }

  acknowledgeRecovery(reviewed: boolean): void {
    this.recoveryReviewed.set(reviewed);
  }

  resultLabel(result: string): string {
    switch (result) {
      case 'completed':
        return 'Completed';
      case 'unchanged':
        return 'Unchanged';
      case 'failed':
        return 'Failed';
      case 'rolledBack':
        return 'Rolled back';
      case 'recoveryRequired':
        return 'Recovery required';
      default:
        return 'Unknown';
    }
  }

  @HostListener('keydown', ['$event'])
  handleDialogKeydown(event: KeyboardEvent): void {
    if (event.key === 'Escape') {
      event.preventDefault();
      event.stopPropagation();
      this.requestClose();
      return;
    }

    if (event.key === 'Enter' && event.ctrlKey && !event.altKey && !event.metaKey) {
      event.preventDefault();
      event.stopPropagation();
      if (this.store.canExecute()) {
        void this.start();
      }
    }
  }

  errorMessage(code: string): string {
    return errorMessages[code] ?? errorMessages['request_failed']!;
  }
}
