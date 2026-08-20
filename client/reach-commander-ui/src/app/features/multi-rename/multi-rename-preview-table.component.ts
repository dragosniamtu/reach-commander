import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { BatchRenamePreviewRowDto } from '../../core/api/api.models';
import { NameDiffComponent } from '../../shared/components/name-diff/name-diff.component';
import { FileSizePipe } from '../../shared/pipes/file-size.pipe';

@Component({
  selector: 'app-multi-rename-preview-table',
  imports: [DatePipe, FileSizePipe, NameDiffComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './multi-rename-preview-table.component.html',
  styleUrl: './multi-rename-preview-table.component.scss',
})
export class MultiRenamePreviewTableComponent {
  readonly rows = input.required<readonly BatchRenamePreviewRowDto[]>();

  statusLabel(row: BatchRenamePreviewRowDto): string {
    switch (row.status) {
      case 'ready':
        return 'Ready';
      case 'unchanged':
        return 'Unchanged';
      case 'invalid':
        return 'Invalid';
      case 'conflict':
        return 'Conflict';
      case 'stale':
        return 'Stale';
    }
  }
}
