import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  ViewChild,
  computed,
  input,
  output,
} from '@angular/core';
import { SourceDto } from '../../../core/api/api.models';
import { CommanderStore } from '../../../core/state/commander-store';
import {
  locationDisplayPath,
  locationSourceId,
  PanelSide,
  PanelState,
} from '../../../core/state/commander.models';
import { FileTableRow } from '../file-table/file-table.viewmodel';
import {
  buildVisibleRows,
  fileTableRowExplanation,
} from '../../../core/state/file-table.viewmodel';
import {
  SourceRemovalRequest,
  SourceSelectorComponent,
} from '../source-selector/source-selector.component';
import { DirectoryTabsComponent } from '../directory-tabs/directory-tabs.component';
import { PathBarComponent } from '../path-bar/path-bar.component';
import { FileTableComponent, PointerSelection } from '../file-table/file-table.component';

@Component({
  selector: 'app-commander-panel',
  imports: [SourceSelectorComponent, DirectoryTabsComponent, PathBarComponent, FileTableComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './commander-panel.component.html',
  styleUrl: './commander-panel.component.scss',
})
export class CommanderPanelComponent {
  readonly side = input.required<PanelSide>();
  readonly panel = input.required<PanelState>();
  readonly sources = input.required<readonly SourceDto[]>();
  readonly active = input.required<boolean>();
  readonly sourceRemovalEnabled = input(false);
  readonly sourceRemovalPending = input(false);
  readonly sourceRemovalRequested = output<SourceRemovalRequest>();
  readonly activeTab = computed(() =>
    this.panel().tabs.find((tab) => tab.id === this.panel().activeTabId),
  );
  readonly currentSource = computed(() =>
    this.sources().find((source) => {
      const tab = this.activeTab();
      return tab && source.id === locationSourceId(tab.location);
    }),
  );
  readonly isArchive = computed(() => this.activeTab()?.location.kind === 'archive');
  readonly displayPath = computed(() => {
    const tab = this.activeTab();
    return tab
      ? locationDisplayPath(tab.location, this.currentSource()?.name ?? 'Source')
      : 'Source:/';
  });
  readonly pathBarPath = computed(() => {
    const location = this.activeTab()?.location;
    return location?.kind === 'filesystem' ? location.path : this.displayPath();
  });
  readonly statusAnnouncement = computed(() => {
    const panel = this.panel();
    if (panel.loading) return 'Reading location.';
    if (panel.errorCode) return panel.errorDetail ?? this.errorMessage();
    const rows = buildVisibleRows(panel);
    if (rows.length === 0) return 'This location is empty.';
    const activeRow = rows[panel.cursorIndex];
    if (!activeRow) return `${panel.entries.length} items.`;
    const explanation = fileTableRowExplanation(panel, activeRow);
    return `${activeRow.name}. ${explanation ? `${explanation} ` : ''}${panel.entries.length} items.`;
  });

  @ViewChild(PathBarComponent) private pathBar?: PathBarComponent;
  @ViewChild('panelRoot', { read: ElementRef })
  private panelRoot?: ElementRef<HTMLElement>;

  constructor(readonly store: CommanderStore) {}

  focusPath(): void {
    this.pathBar?.focusEditor();
  }

  focusPanel(): void {
    this.panelRoot?.nativeElement.focus();
  }

  selectRow(selection: PointerSelection): void {
    this.store.selectWithPointer(this.side(), selection.rowIndex, selection.mode);
  }

  async openRow(row: FileTableRow): Promise<void> {
    await this.store.openEntry(this.side(), row);
    this.focusPanel();
  }

  async closeTab(tabId: string): Promise<void> {
    if (tabId !== this.panel().activeTabId) {
      await this.store.activateTab(this.side(), tabId);
    }
    await this.store.closeActiveTab(this.side());
  }

  errorMessage(): string {
    switch (this.panel().errorCode) {
      case 'source_unavailable':
        return 'This source is not currently mounted or accessible.';
      case 'invalid_path':
        return 'That logical path is not valid.';
      case 'request_failed':
        return 'The directory could not be loaded.';
      default:
        return this.panel().errorDetail ?? 'The location could not be loaded.';
    }
  }

  errorTitle(): string {
    return this.isArchive() || this.panel().errorCode?.startsWith('archive_')
      ? 'Archive unavailable'
      : 'Source unavailable';
  }
}
