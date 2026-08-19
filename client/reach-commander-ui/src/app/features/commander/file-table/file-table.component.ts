import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { PanelState } from '../../../core/state/commander.models';
import { FileSizePipe } from '../../../shared/pipes/file-size.pipe';
import { buildVisibleRows, FileTableRow } from './file-table.viewmodel';

export interface PointerSelection {
  readonly rowIndex: number;
  readonly mode: 'replace' | 'toggle' | 'range';
}

@Component({
  selector: 'app-file-table',
  imports: [DatePipe, FileSizePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './file-table.component.html',
  styleUrl: './file-table.component.scss',
})
export class FileTableComponent {
  readonly panel = input.required<PanelState>();
  readonly sortRequested = output<PanelState['sortColumn']>();
  readonly rowSelected = output<PointerSelection>();
  readonly rowOpened = output<FileTableRow>();
  readonly rows = computed(() => buildVisibleRows(this.panel()));

  ariaSort(column: PanelState['sortColumn']): 'ascending' | 'descending' | 'none' {
    return this.panel().sortColumn === column ? this.panel().sortDirection : 'none';
  }

  select(rowIndex: number, event: MouseEvent): void {
    const mode = event.shiftKey ? 'range' : event.ctrlKey || event.metaKey ? 'toggle' : 'replace';
    this.rowSelected.emit({ rowIndex, mode });
  }

  isSelected(row: FileTableRow): boolean {
    return !row.isParent && this.panel().selectedItems.has(row.relativePath);
  }
}
