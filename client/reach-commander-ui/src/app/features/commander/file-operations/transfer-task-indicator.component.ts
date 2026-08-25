import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { FileOperationKind, FileOperationStatusDto } from '../../../core/api/api.models';
import { FileOperationStore } from './file-operation.store';

@Component({
  selector: 'app-transfer-task-indicator',
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './transfer-task-indicator.component.html',
  styleUrl: './transfer-task-indicator.component.scss',
})
export class TransferTaskIndicatorComponent {
  readonly store = inject(FileOperationStore);
  readonly task = computed(() => this.store.activeTask() ??
    this.store.tasks().find((task) => !task.acknowledged) ?? null);

  kindLabel(kind: FileOperationKind): string {
    switch (kind) {
      case 'copy': return 'Copy';
      case 'move': return 'Move';
      case 'trash': return 'Trash';
      case 'restore': return 'Restore';
      case 'emptyTrash': return 'Empty Trash';
      case 'permanentDelete': return 'Delete';
    }
  }

  progressLabel(task: FileOperationStatusDto): string {
    if (task.phase === 'queued') {
      return 'Queued';
    }
    if (task.progress.percentage === null) {
      return task.phase;
    }
    return `${task.progress.percentage}%`;
  }

  accessibleLabel(task: FileOperationStatusDto): string {
    const queued = this.store.queuedCount();
    return `${this.kindLabel(task.kind)} ${this.progressLabel(task)}${queued > 0 ? `, ${queued} queued` : ''}. Open task details.`;
  }

  restore(): void {
    const operationId = this.task()?.operationId;
    if (operationId) {
      this.store.restoreProgress(operationId);
    }
  }
}
