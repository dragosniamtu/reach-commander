import { A11yModule } from '@angular/cdk/a11y';
import { ChangeDetectionStrategy, Component, HostListener, computed, effect, inject, output, signal } from '@angular/core';
import { FileSizePipe } from '../../../shared/pipes/file-size.pipe';
import { TrashStore } from './trash.store';

export const PERMANENT_DELETE_WARNING =
  'This deletion is permanent, cannot be undone, and is unrecoverable.';

@Component({
  selector: 'app-delete-dialog',
  imports: [A11yModule, FileSizePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './delete-dialog.component.html',
  styleUrl: './delete-dialog.component.scss',
})
export class DeleteDialogComponent {
  readonly store = inject(TrashStore);
  readonly closed = output<void>();
  readonly permanentDelete = signal(false);
  readonly warning = PERMANENT_DELETE_WARNING;
  readonly visibleNames = computed(() =>
    (this.store.deleteRequest()?.logicalPaths ?? []).slice(0, 5).map(basename),
  );
  readonly hiddenNameCount = computed(() =>
    Math.max(0, (this.store.deleteRequest()?.logicalPaths.length ?? 0) - 5),
  );
  readonly confirmationReady = computed(() =>
    this.permanentDelete() && this.store.deletePreview()?.mode === 'permanent' && !this.store.busy(),
  );

  constructor() {
    effect(() => this.ensureAvailableMode());
  }

  ensureAvailableMode(): void {
    const preview = this.store.deletePreview();
    if (preview && !preview.trashAvailable && preview.mode !== 'permanent') {
      this.permanentDelete.set(true);
      void this.store.changeDeleteMode('permanent');
    }
  }

  permanentChanged(event: Event): void {
    const permanent = (event.target as HTMLInputElement).checked;
    this.permanentDelete.set(permanent);
    void this.store.changeDeleteMode(permanent ? 'permanent' : 'trash');
  }

  async confirm(): Promise<void> {
    const preview = this.store.deletePreview();
    if (!preview) {
      return;
    }
    if (preview.mode === 'permanent' && !this.confirmationReady()) {
      return;
    }
    await this.store.submitDelete(preview.mode === 'permanent');
    if (this.store.deleteRequest() === null) {
      this.closed.emit();
    }
  }

  cancel(): void {
    if (!this.store.busy() || this.store.deletePreview() === null) {
      this.store.clearDeletePreview();
      this.closed.emit();
    }
  }

  @HostListener('keydown', ['$event'])
  handleKeydown(event: KeyboardEvent): void {
    event.stopPropagation();
    if (event.key === 'Escape') {
      event.preventDefault();
      this.cancel();
    }
  }
}

function basename(path: string): string {
  return path.split('/').at(-1) || path;
}
