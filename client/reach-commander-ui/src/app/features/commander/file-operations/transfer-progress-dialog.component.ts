import { A11yModule } from '@angular/cdk/a11y';
import { ChangeDetectionStrategy, Component, HostListener, computed, effect, inject, signal } from '@angular/core';
import { FileOperationKind, FileOperationStatusDto } from '../../../core/api/api.models';
import { FileSizePipe } from '../../../shared/pipes/file-size.pipe';
import { FileOperationStore } from './file-operation.store';

@Component({
  selector: 'app-transfer-progress-dialog',
  imports: [A11yModule, FileSizePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './transfer-progress-dialog.component.html',
  styleUrl: './transfer-progress-dialog.component.scss',
})
export class TransferProgressDialogComponent {
  readonly store = inject(FileOperationStore);
  readonly terminal = computed(() => {
    const phase = this.store.activeTask()?.phase;
    return phase === 'completed' || phase === 'completedWithErrors' || phase === 'cancelled' ||
      phase === 'failed' || phase === 'interrupted';
  });
  readonly cancellable = computed(() => {
    const phase = this.store.activeTask()?.phase;
    return phase === 'queued' || phase === 'validating' || phase === 'running';
  });
  readonly announcement = signal('');
  private lastAnnouncementKey = '';

  constructor() {
    effect(() => {
      const task = this.store.activeTask();
      if (!task) {
        return;
      }
      const bucket = task.progress.percentage === null
        ? 'unknown'
        : Math.floor(task.progress.percentage / 10) * 10;
      const key = `${task.operationId}:${task.phase}:${bucket}`;
      if (key !== this.lastAnnouncementKey) {
        this.lastAnnouncementKey = key;
        this.announcement.set(`${this.kindLabel(task.kind)} ${this.phaseLabel(task)}${task.progress.percentage === null ? '' : ` ${task.progress.percentage}%`}`);
      }
    });
  }

  kindLabel(kind: FileOperationKind): string {
    switch (kind) {
      case 'copy': return 'Copy';
      case 'move': return 'Move';
      case 'trash': return 'Move to Trash';
      case 'restore': return 'Restore';
      case 'emptyTrash': return 'Empty Trash';
      case 'permanentDelete': return 'Permanent delete';
    }
  }

  phaseLabel(task: FileOperationStatusDto): string {
    switch (task.phase) {
      case 'queued': return task.queuePosition > 0 ? `queued at position ${task.queuePosition}` : 'queued';
      case 'validating': return 'validating';
      case 'running': return 'in progress';
      case 'cancelling': return 'cancelling';
      case 'completed': return 'completed';
      case 'completedWithErrors': return 'completed with errors';
      case 'cancelled': return 'cancelled';
      case 'failed': return 'failed';
      case 'interrupted': return 'interrupted';
    }
  }

  background(): void {
    this.store.background();
  }

  cancel(): void {
    const operationId = this.store.activeTask()?.operationId;
    if (operationId) {
      void this.store.cancel(operationId);
    }
  }

  close(): void {
    const operationId = this.store.activeTask()?.operationId;
    if (operationId && this.terminal()) {
      void this.store.acknowledge(operationId);
    }
  }

  @HostListener('keydown', ['$event'])
  handleKeydown(event: KeyboardEvent): void {
    event.stopPropagation();
    if (event.key === 'Escape' && !this.terminal()) {
      event.preventDefault();
      this.background();
    }
  }
}
