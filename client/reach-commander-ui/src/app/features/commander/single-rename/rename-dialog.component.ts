import { A11yModule } from '@angular/cdk/a11y';
import {
  AfterViewInit,
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  HostListener,
  ViewChild,
  computed,
  inject,
  output,
} from '@angular/core';
import { SingleRenameStore } from '../../../core/state/single-rename-store';

const errorMessages: Readonly<Record<string, string>> = {
  invalid_rename_rule: 'The requested name is not valid.',
  source_read_only: 'The selected source is read-only.',
  source_not_found: 'The selected source no longer exists.',
  source_unavailable: 'The selected source is unavailable.',
  invalid_path: 'The selected path is not valid.',
  path_forbidden: 'The selected path is not allowed.',
  entry_not_found: 'The selected entry no longer exists.',
  rename_plan_not_found: 'This preview is no longer available. Enter the name again.',
  rename_plan_expired: 'This preview has expired. Enter the name again.',
  rename_plan_stale: 'The folder changed. Enter the name again for a fresh preview.',
  rename_recovery_required: 'Recovery is required. Review the logical operation result.',
  request_failed: 'The rename request could not be completed.',
};

@Component({
  selector: 'app-rename-dialog',
  imports: [A11yModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './rename-dialog.component.html',
  styleUrl: './rename-dialog.component.scss',
})
export class RenameDialogComponent implements AfterViewInit {
  readonly store = inject(SingleRenameStore);
  readonly closeRequested = output<void>();
  readonly previewMessage = computed(() => {
    const state = this.store.state();
    if (state.newName.length === 0) {
      return 'Enter a new name.';
    }
    if (state.errorCode) {
      return errorMessages[state.errorCode] ?? errorMessages['request_failed']!;
    }
    if (state.previewPending) {
      return null;
    }

    const row = state.preview?.rows[0];
    if (!row) {
      return null;
    }
    if (row.message) {
      return row.message;
    }
    if (row.status === 'unchanged') {
      return 'Enter a different name.';
    }
    if (row.status === 'ready') {
      return `Ready to rename to ${row.newName}.`;
    }
    return 'The requested name cannot be used.';
  });
  readonly previewBlocked = computed(() => {
    const state = this.store.state();
    const status = state.preview?.rows[0]?.status;
    return (
      state.newName.length === 0 ||
      state.errorCode !== null ||
      (status !== undefined && status !== 'ready')
    );
  });

  @ViewChild('nameInput') private nameInput?: ElementRef<HTMLInputElement>;

  ngAfterViewInit(): void {
    this.nameInput?.nativeElement.focus({ preventScroll: true });
    this.nameInput?.nativeElement.select();
  }

  submit(): void {
    if (this.store.canExecute()) {
      void this.store.execute();
    }
  }

  requestClose(): void {
    if (!this.store.state().actionPending) {
      this.closeRequested.emit();
    }
  }

  @HostListener('keydown', ['$event'])
  handleKeydown(event: KeyboardEvent): void {
    event.stopPropagation();
    if (event.key === 'Escape') {
      event.preventDefault();
      this.requestClose();
    } else if (event.key === 'Enter') {
      event.preventDefault();
      this.submit();
    }
  }
}
