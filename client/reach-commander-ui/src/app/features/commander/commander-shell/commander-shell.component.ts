import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  OnInit,
  ViewChild,
  computed,
  inject,
  signal,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { CommanderCommand, CommanderFunctionKey } from '../../../core/keyboard/commander-command';
import { CommanderKeyboardService } from '../../../core/keyboard/commander-keyboard.service';
import { CommanderStore } from '../../../core/state/commander-store';
import { SystemMetricsStore } from '../../../core/state/system-metrics-store';
import { UploadStore } from '../../../core/state/upload-store';
import { buildVisibleRows } from '../../../core/state/file-table.viewmodel';
import {
  locationDisplayPath,
  locationSourceId,
  PanelSide,
} from '../../../core/state/commander.models';
import { CommanderPanelComponent } from '../commander-panel/commander-panel.component';
import { CommandBarComponent } from '../command-bar/command-bar.component';
import { SystemMetricsWidgetComponent } from '../../system-metrics/system-metrics-widget.component';
import { SystemMetricsDetailsComponent } from '../../system-metrics/system-metrics-details.component';
import { UploadDialogComponent } from '../../uploads/upload-dialog.component';
import { MultiRenameStore } from '../../../core/state/multi-rename-store';
import { MultiRenameDialogComponent } from '../../multi-rename/multi-rename-dialog.component';
import { ArchiveExtractionStore } from '../../../core/state/archive-extraction-store';
import { captureArchiveExtractionContext } from '../../../core/state/archive-extraction.models';
import { ArchiveExtractionDialogComponent } from '../../archive-extraction/archive-extraction-dialog.component';
import {
  ActivePanelToolbarComponent,
  ActivePanelToolbarContext,
} from '../active-panel-toolbar/active-panel-toolbar.component';
import { PwaService } from '../../../core/pwa/pwa.service';
import { PwaStatusComponent } from '../../pwa/pwa-status.component';

@Component({
  selector: 'app-commander-shell',
  imports: [
    CommanderPanelComponent,
    CommandBarComponent,
    SystemMetricsWidgetComponent,
    SystemMetricsDetailsComponent,
    UploadDialogComponent,
    MultiRenameDialogComponent,
    ActivePanelToolbarComponent,
    ArchiveExtractionDialogComponent,
    PwaStatusComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './commander-shell.component.html',
  styleUrl: './commander-shell.component.scss',
})
export class CommanderShellComponent implements OnInit {
  readonly store = inject(CommanderStore);
  readonly metricsStore = inject(SystemMetricsStore);
  readonly uploadStore = inject(UploadStore);
  readonly multiRename = inject(MultiRenameStore);
  readonly archiveExtraction = inject(ArchiveExtractionStore);
  readonly pwa = inject(PwaService);
  readonly commandStatus = signal<string | null>(null);
  readonly initializationError = signal<string | null>(null);
  readonly menuOpen = signal(false);
  readonly metricsOpen = signal(false);
  readonly uploadOpener = signal<HTMLElement | null>(null);
  readonly extractionOpener = signal<HTMLElement | null>(null);
  readonly activeState = computed(() =>
    this.store.activePanel() === 'left' ? this.store.leftPanel() : this.store.rightPanel(),
  );
  readonly activeTab = computed(() =>
    this.activeState().tabs.find((tab) => tab.id === this.activeState().activeTabId),
  );
  readonly activeSource = computed(() =>
    this.store.sources().find((source) => {
      const tab = this.activeTab();
      return tab && source.id === locationSourceId(tab.location);
    }),
  );
  readonly extractionContext = computed(() => {
    const side = this.store.activePanel();
    const active = side === 'left' ? this.store.leftPanel() : this.store.rightPanel();
    const opposite = side === 'left' ? this.store.rightPanel() : this.store.leftPanel();
    return captureArchiveExtractionContext(side, active, opposite, this.store.sources());
  });
  readonly toolbarContext = computed<ActivePanelToolbarContext>(() => ({
    side: this.store.activePanel(),
    sourceName: this.activeSource()?.name ?? 'Source',
    logicalPath: this.activeTab()
      ? locationDisplayPath(this.activeTab()!.location, this.activeSource()?.name ?? 'Source')
      : 'Source:/',
    available: this.activeSource()?.isAvailable ?? false,
    readOnly: this.activeSource()?.isReadOnly ?? true,
    archive: this.activeTab()?.location.kind === 'archive',
    hasRenameTargets: this.store.createMultiRenameContext(this.store.activePanel()) !== null,
    uploadPending: this.uploadStore.isPending(),
    extractAvailable: this.extractionContext().context !== null,
    extractDisabledReason: this.extractionContext().error,
  }));

  @ViewChild('leftPanel') private leftPanel?: CommanderPanelComponent;
  @ViewChild('rightPanel') private rightPanel?: CommanderPanelComponent;
  @ViewChild(SystemMetricsWidgetComponent) metricsWidget?: SystemMetricsWidgetComponent;
  @ViewChild(ActivePanelToolbarComponent)
  private activeToolbar?: ActivePanelToolbarComponent;

  private readonly keyboard = inject(CommanderKeyboardService);
  private readonly destroyRef = inject(DestroyRef);

  constructor() {
    this.keyboard.commands.pipe(takeUntilDestroyed()).subscribe((command) => this.execute(command));
    this.destroyRef.onDestroy(() => {
      this.keyboard.stop();
      this.metricsStore.stop();
    });
    this.archiveExtraction.setCompletionHandler((source, destination) => {
      void Promise.all([this.store.refresh(source), this.store.refresh(destination)]);
    });
  }

  ngOnInit(): void {
    this.keyboard.start();
    this.metricsStore.start();
    void this.retryInitialization();
  }

  async retryInitialization(): Promise<void> {
    this.initializationError.set(null);
    try {
      await this.store.initialize();
    } catch {
      this.initializationError.set('The ReachCommander server is unavailable.');
    }
  }

  execute(command: CommanderCommand): void {
    if (this.archiveExtraction.state().phase !== 'closed') {
      if (command.type === 'escape') {
        this.handleExtractionEscape();
      }
      return;
    }

    if (this.multiRename.state().open) {
      if (
        command.type === 'escape' &&
        !this.multiRename.state().previewPending &&
        !this.multiRename.state().actionPending
      ) {
        this.closeMultiRename();
      }
      return;
    }

    if (command.type === 'escape' && this.metricsOpen()) {
      this.closeMetrics();
      return;
    }

    if (command.type === 'escape' && this.uploadStore.state().phase !== 'closed') {
      if (!this.uploadStore.isPending()) {
        this.closeUpload();
      }
      return;
    }

    const side = this.store.activePanel();
    switch (command.type) {
      case 'move-cursor':
        this.store.moveCursor(side, command.amount);
        break;
      case 'move-page':
        this.store.moveCursorPage(side, command.direction);
        break;
      case 'move-boundary':
        this.store.moveCursorBoundary(side, command.boundary);
        break;
      case 'open-cursor':
        this.openCursor(side);
        break;
      case 'backspace':
        this.backspace(side);
        break;
      case 'switch-panel':
        this.store.activatePanel(side === 'left' ? 'right' : 'left');
        break;
      case 'toggle-selection':
        this.store.toggleCursorSelection(side);
        break;
      case 'select-all':
        this.store.selectAllVisible(side);
        break;
      case 'multi-rename':
        this.openMultiRename(side);
        break;
      case 'escape':
        this.escape(side);
        break;
      case 'focus-path':
        this.focusPath(side);
        break;
      case 'focus-search':
        this.focusSearch();
        break;
      case 'refresh':
        void this.store.refresh(side);
        break;
      case 'new-tab':
        void this.store.createTab(side);
        break;
      case 'close-tab':
        void this.store.closeActiveTab(side);
        break;
      case 'filter-text':
        this.store.setFilter(side, this.activeState().filter + command.text);
        break;
      case 'function-key':
        this.handleFunctionKey(command.key);
        break;
    }
  }

  openMetrics(): void {
    this.metricsOpen.set(true);
  }

  closeMetrics(): void {
    if (!this.metricsOpen()) {
      return;
    }
    this.metricsOpen.set(false);
    queueMicrotask(() => this.metricsWidget?.focusTrigger());
  }

  reviewUpload(files: readonly File[]): void {
    if (files.length === 0) {
      return;
    }

    const side = this.store.activePanel();
    const panel = side === 'left' ? this.store.leftPanel() : this.store.rightPanel();
    const activeTab = panel.tabs.find((tab) => tab.id === panel.activeTabId);
    const source = this.store.sources().find(
      (candidate) => candidate.id === activeTab?.location.sourceId,
    );
    if (!activeTab || !source) {
      this.commandStatus.set('The active destination is unavailable.');
      return;
    }

    if (activeTab.location.kind === 'archive') {
      this.commandStatus.set('Files cannot be added inside a read-only archive.');
      return;
    }

    if (!source.isAvailable) {
      this.commandStatus.set(`${source.name} is unavailable.`);
      return;
    }

    if (source.isReadOnly) {
      this.commandStatus.set(`${source.name} is read-only.`);
      return;
    }

    this.commandStatus.set(null);
    this.menuOpen.set(false);
    this.uploadOpener.set(
      document.activeElement instanceof HTMLElement ? document.activeElement : null,
    );
    this.uploadStore.open(
      {
        side,
        sourceId: source.id,
        sourceName: source.name,
        directoryPath: activeTab.location.path,
      },
      files,
      () => {
        void this.store.refresh(side);
      },
    );
  }

  startUpload(): void {
    this.uploadStore.start();
  }

  closeUpload(): void {
    if (!this.uploadStore.close()) {
      return;
    }

    const opener = this.uploadOpener();
    this.uploadOpener.set(null);
    queueMicrotask(() => opener?.focus());
  }

  openMultiRename(side: PanelSide = this.store.activePanel()): void {
    const context = this.store.createMultiRenameContext(side);
    if (!context) {
      this.commandStatus.set('Select or focus an item before opening Multi-Rename.');
      return;
    }

    if (!context.isAvailable) {
      this.commandStatus.set(`${context.sourceName} is unavailable.`);
      return;
    }
    if (context.isReadOnly) {
      this.commandStatus.set(`${context.sourceName} is read-only.`);
      return;
    }

    this.menuOpen.set(false);
    this.commandStatus.set(null);
    this.multiRename.open(context);
  }

  closeMultiRename(): void {
    const side = this.multiRename.state().context?.panelSide ?? this.store.activePanel();
    this.multiRename.close();
    queueMicrotask(() => (side === 'left' ? this.leftPanel : this.rightPanel)?.focusPanel());
  }

  openArchiveExtraction(): void {
    const result = this.extractionContext();
    if (!result.context) {
      this.commandStatus.set(result.error ?? 'Select a supported archive to extract.');
      return;
    }

    this.menuOpen.set(false);
    this.commandStatus.set(null);
    this.extractionOpener.set(
      document.activeElement instanceof HTMLElement ? document.activeElement : null,
    );
    void this.archiveExtraction.open(result.context);
  }

  closeArchiveExtraction(): void {
    const state = this.archiveExtraction.state();
    if (state.phase === 'running' || state.phase === 'starting' ||
        state.phase === 'previewing' || state.phase === 'cancelling') {
      return;
    }

    const opener = this.extractionOpener();
    const side = state.context?.sourcePanelSide ?? this.store.activePanel();
    this.archiveExtraction.close();
    this.extractionOpener.set(null);
    queueMicrotask(() => opener?.isConnected
      ? opener.focus()
      : (side === 'left' ? this.leftPanel : this.rightPanel)?.focusPanel());
  }

  async handleRenameFilesystemChanged(side: PanelSide): Promise<void> {
    this.store.clearSelection(side);
    await this.store.refresh(side);
  }

  setActiveFilter(value: string): void {
    this.store.setFilter(this.store.activePanel(), value);
  }

  focusSearch(): void {
    queueMicrotask(() => this.activeToolbar?.focusSearch());
  }

  handleFunctionKey(key: CommanderFunctionKey): void {
    if (key === 'F9') {
      this.menuOpen.update((open) => !open);
      this.commandStatus.set(null);
      return;
    }

    if (key === 'F5') {
      if (this.extractionContext().context || this.hasArchiveExtractionIntent()) {
        this.openArchiveExtraction();
      } else {
        this.commandStatus.set('F5 is reserved for a future copy operation.');
      }
      return;
    }

    this.commandStatus.set(`${key} is reserved for Milestone 2.`);
  }

  private handleExtractionEscape(): void {
    const phase = this.archiveExtraction.state().phase;
    if (phase === 'review' || phase === 'completed' || phase === 'cancelled' ||
        phase === 'failed' || phase === 'recoveryRequired') {
      this.closeArchiveExtraction();
    } else if (
      phase === 'running' &&
      this.archiveExtraction.canCancel() &&
      window.confirm('Cancel the archive extraction?')
    ) {
      void this.archiveExtraction.cancel();
    }
  }

  private hasArchiveExtractionIntent(): boolean {
    const panel = this.activeState();
    const tab = panel.tabs.find((candidate) => candidate.id === panel.activeTabId);
    if (tab?.location.kind === 'archive') {
      return true;
    }

    const rows = buildVisibleRows(panel);
    const candidates = panel.selectedItems.size > 0
      ? rows.filter((row) => !row.isParent && panel.selectedItems.has(row.relativePath))
      : rows[panel.cursorIndex] && !rows[panel.cursorIndex]!.isParent
        ? [rows[panel.cursorIndex]!]
        : [];
    return candidates.some((candidate) =>
      candidate.archiveFormatHint !== null || candidate.archiveRole !== null,
    );
  }

  private openCursor(side: PanelSide): void {
    const state = this.activeState();
    const row = buildVisibleRows(state)[state.cursorIndex];
    if (!row) {
      return;
    }

    const tab = state.tabs.find((candidate) => candidate.id === state.activeTabId);
    const canOpen = row.isParent || row.type === 'directory' ||
      (tab?.location.kind === 'filesystem' && row.archiveFormatHint !== null);
    if (canOpen) {
      void this.store.openEntry(side, row);
    } else {
      this.commandStatus.set('File preview arrives in a later milestone.');
    }
  }

  private backspace(side: PanelSide): void {
    const filter = this.activeState().filter;
    if (filter) {
      this.store.setFilter(side, filter.slice(0, -1));
    } else {
      void this.store.navigateParent(side);
    }
  }

  private escape(side: PanelSide): void {
    if (this.menuOpen()) {
      this.menuOpen.set(false);
    } else if (this.activeState().filter) {
      this.store.setFilter(side, '');
    } else if (this.activeState().selectedItems.size > 0) {
      this.store.clearSelection(side);
    } else {
      this.commandStatus.set(null);
    }
  }

  private focusPath(side: PanelSide): void {
    (side === 'left' ? this.leftPanel : this.rightPanel)?.focusPath();
  }
}
