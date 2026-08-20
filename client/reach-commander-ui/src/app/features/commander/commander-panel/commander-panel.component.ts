import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  ViewChild,
  computed,
  input,
} from '@angular/core';
import { SourceDto } from '../../../core/api/api.models';
import { CommanderStore } from '../../../core/state/commander-store';
import { PanelSide, PanelState } from '../../../core/state/commander.models';
import { FileTableRow } from '../file-table/file-table.viewmodel';
import { SourceSelectorComponent } from '../source-selector/source-selector.component';
import { DirectoryTabsComponent } from '../directory-tabs/directory-tabs.component';
import { PathBarComponent } from '../path-bar/path-bar.component';
import { QuickFilterComponent } from '../quick-filter/quick-filter.component';
import { FileTableComponent, PointerSelection } from '../file-table/file-table.component';

@Component({
  selector: 'app-commander-panel',
  imports: [
    SourceSelectorComponent,
    DirectoryTabsComponent,
    PathBarComponent,
    QuickFilterComponent,
    FileTableComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './commander-panel.component.html',
  styleUrl: './commander-panel.component.scss',
})
export class CommanderPanelComponent {
  readonly side = input.required<PanelSide>();
  readonly panel = input.required<PanelState>();
  readonly sources = input.required<readonly SourceDto[]>();
  readonly active = input.required<boolean>();
  readonly activeTab = computed(() =>
    this.panel().tabs.find((tab) => tab.id === this.panel().activeTabId),
  );
  readonly currentSource = computed(() =>
    this.sources().find((source) => source.id === this.panel().sourceId),
  );

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

  openRow(row: FileTableRow): void {
    if (row.type === 'directory') {
      void this.store.navigateTo(this.side(), row.relativePath);
    }
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
        return '';
    }
  }
}
