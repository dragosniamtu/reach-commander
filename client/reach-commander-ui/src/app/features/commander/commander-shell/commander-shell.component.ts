import { ChangeDetectionStrategy, Component, DestroyRef, OnInit, ViewChild, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { CommanderCommand, CommanderFunctionKey } from '../../../core/keyboard/commander-command';
import { CommanderKeyboardService } from '../../../core/keyboard/commander-keyboard.service';
import { CommanderStore } from '../../../core/state/commander-store';
import { SystemMetricsStore } from '../../../core/state/system-metrics-store';
import { buildVisibleRows } from '../../../core/state/file-table.viewmodel';
import { PanelSide } from '../../../core/state/commander.models';
import { CommanderPanelComponent } from '../commander-panel/commander-panel.component';
import { CommandBarComponent } from '../command-bar/command-bar.component';
import { SystemMetricsWidgetComponent } from '../../system-metrics/system-metrics-widget.component';
import { SystemMetricsDetailsComponent } from '../../system-metrics/system-metrics-details.component';

@Component({
  selector: 'app-commander-shell',
  imports: [
    CommanderPanelComponent,
    CommandBarComponent,
    SystemMetricsWidgetComponent,
    SystemMetricsDetailsComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './commander-shell.component.html',
  styleUrl: './commander-shell.component.scss',
})
export class CommanderShellComponent implements OnInit {
  readonly store = inject(CommanderStore);
  readonly metricsStore = inject(SystemMetricsStore);
  readonly commandStatus = signal<string | null>(null);
  readonly initializationError = signal<string | null>(null);
  readonly menuOpen = signal(false);
  readonly metricsOpen = signal(false);
  readonly activeState = computed(() =>
    this.store.activePanel() === 'left' ? this.store.leftPanel() : this.store.rightPanel(),
  );

  @ViewChild('leftPanel') private leftPanel?: CommanderPanelComponent;
  @ViewChild('rightPanel') private rightPanel?: CommanderPanelComponent;
  @ViewChild(SystemMetricsWidgetComponent) metricsWidget?: SystemMetricsWidgetComponent;

  private readonly keyboard = inject(CommanderKeyboardService);
  private readonly destroyRef = inject(DestroyRef);

  constructor() {
    this.keyboard.commands
      .pipe(takeUntilDestroyed())
      .subscribe((command) => this.execute(command));
    this.destroyRef.onDestroy(() => {
      this.keyboard.stop();
      this.metricsStore.stop();
    });
  }

  ngOnInit(): void {
    this.keyboard.start();
    this.metricsStore.start();
    void this.store.initialize().catch(() => {
      this.initializationError.set('ReachCommander could not load its source configuration.');
    });
  }

  execute(command: CommanderCommand): void {
    if (command.type === 'escape' && this.metricsOpen()) {
      this.closeMetrics();
      return;
    }

    const side = this.store.activePanel();
    switch (command.type) {
      case 'move-cursor': this.store.moveCursor(side, command.amount); break;
      case 'move-page': this.store.moveCursorPage(side, command.direction); break;
      case 'move-boundary': this.store.moveCursorBoundary(side, command.boundary); break;
      case 'open-cursor': this.openCursor(side); break;
      case 'backspace': this.backspace(side); break;
      case 'switch-panel': this.store.activatePanel(side === 'left' ? 'right' : 'left'); break;
      case 'toggle-selection': this.store.toggleCursorSelection(side); break;
      case 'select-all': this.store.selectAllVisible(side); break;
      case 'escape': this.escape(side); break;
      case 'focus-path': this.focusPath(side); break;
      case 'refresh': void this.store.refresh(side); break;
      case 'new-tab': void this.store.createTab(side); break;
      case 'close-tab': void this.store.closeActiveTab(side); break;
      case 'filter-text': this.store.setFilter(side, this.activeState().filter + command.text); break;
      case 'function-key': this.handleFunctionKey(command.key); break;
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

  handleFunctionKey(key: CommanderFunctionKey): void {
    if (key === 'F9') {
      this.menuOpen.update((open) => !open);
      this.commandStatus.set(null);
      return;
    }

    this.commandStatus.set(`${key} is reserved for Milestone 2.`);
  }

  private openCursor(side: PanelSide): void {
    const state = this.activeState();
    const row = buildVisibleRows(state)[state.cursorIndex];
    if (!row) {
      return;
    }

    if (row.type === 'directory') {
      void this.store.navigateTo(side, row.relativePath);
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
