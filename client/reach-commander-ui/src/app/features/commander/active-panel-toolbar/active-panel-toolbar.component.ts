import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  ViewChild,
  input,
  output,
  signal,
} from '@angular/core';
import { PanelSide } from '../../../core/state/commander.models';
import { ApplicationMenuComponent } from '../application-menu/application-menu.component';

export interface ActivePanelToolbarContext {
  readonly side: PanelSide;
  readonly sourceName: string;
  readonly logicalPath: string;
  readonly available: boolean;
  readonly readOnly: boolean;
  readonly archive: boolean;
  readonly hasRenameTargets: boolean;
  readonly uploadPending: boolean;
  readonly extractAvailable: boolean;
  readonly extractDisabledReason: string | null;
  readonly encodingDisabledReason: string | null;
}

@Component({
  selector: 'app-active-panel-toolbar',
  imports: [ApplicationMenuComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './active-panel-toolbar.component.html',
  styleUrl: './active-panel-toolbar.component.scss',
})
export class ActivePanelToolbarComponent {
  readonly subtitlesExpanded = signal(false);
  readonly context = input.required<ActivePanelToolbarContext>();
  readonly filter = input.required<string>();
  readonly sourceManagementSupported = input(false);
  readonly sourceManagementPending = input(false);
  readonly sourceManagementDisabledReason = input<string | null>(null);
  readonly sourceManagementRetryAvailable = input(false);
  readonly subtitleDisabledReason = input<string | null>('Select or focus one video or folder.');
  readonly canRepairSubtitles = input(false);
  readonly subtitleReadinessMessage = input<string | null>(null);
  readonly activeSubtitleJobs = input(0);
  readonly subtitlesRequested = output<HTMLElement>();
  readonly subtitleRepairRequested = output<HTMLElement>();
  readonly subtitleHistoryRequested = output<HTMLElement>();
  readonly renameRequested = output<void>();
  readonly encodingRequested = output<HTMLElement>();
  readonly filesSelected = output<readonly File[]>();
  readonly extractRequested = output<void>();
  readonly trashRequested = output<void>();
  readonly filterChanged = output<string>();
  readonly sourceRequested = output<HTMLElement>();
  readonly sourceCapabilityRetryRequested = output<void>();

  @ViewChild('searchInput', { read: ElementRef })
  private searchInput?: ElementRef<HTMLInputElement>;
  @ViewChild('fileInput', { read: ElementRef })
  private fileInput?: ElementRef<HTMLInputElement>;
  @ViewChild('encodingButton', { read: ElementRef })
  private encodingButton?: ElementRef<HTMLButtonElement>;
  @ViewChild('addSourceButton', { read: ElementRef })
  private addSourceButton?: ElementRef<HTMLButtonElement>;
  @ViewChild(ApplicationMenuComponent) private menu?: ApplicationMenuComponent;

  get menuTriggerElement(): HTMLElement | null {
    return this.menu?.triggerElement ?? null;
  }

  focusMenu(): void {
    this.menuTriggerElement?.focus();
  }

  requestSubtitles(history = false): void {
    const opener = this.menuTriggerElement;
    if (opener && (history || this.subtitleDisabledReason() === null)) {
      (history ? this.subtitleHistoryRequested : this.subtitlesRequested).emit(opener);
    }
  }

  requestSubtitleRepair(): void {
    const opener = this.menuTriggerElement;
    if (opener && this.canRepairSubtitles()) {
      this.subtitleRepairRequested.emit(opener);
    }
  }

  sourceManagementReason(): string | null {
    if (this.sourceManagementPending()) {
      return 'Checking whether managed host sources are supported.';
    }
    if (!this.sourceManagementSupported()) {
      return this.sourceManagementDisabledReason() ?? 'Source management is unavailable.';
    }
    return null;
  }

  requestSource(): void {
    if (this.sourceManagementRetryAvailable()) {
      this.sourceCapabilityRetryRequested.emit();
      return;
    }
    if (this.sourceManagementReason() === null && this.addSourceButton) {
      this.sourceRequested.emit(this.menuTriggerElement ?? this.addSourceButton.nativeElement);
    }
  }

  renameDisabledReason(): string | null {
    const context = this.context();
    if (!context.available) {
      return 'The active source is unavailable.';
    }
    if (context.archive) {
      return 'Multi-Rename is unavailable inside a read-only archive.';
    }
    if (context.readOnly) {
      return 'The active source is read-only.';
    }
    if (!context.hasRenameTargets) {
      return 'Select or focus an item to use Multi-Rename.';
    }
    return null;
  }

  uploadDisabledReason(): string | null {
    const context = this.context();
    if (!context.available) {
      return 'The active source is unavailable.';
    }
    if (context.archive) {
      return 'Files cannot be added inside a read-only archive.';
    }
    if (context.readOnly) {
      return 'The active source is read-only.';
    }
    if (context.uploadPending) {
      return 'Another upload is currently in progress.';
    }
    return null;
  }

  requestExtraction(): void {
    if (this.context().extractAvailable) {
      this.extractRequested.emit();
    }
  }

  requestRename(): void {
    if (this.renameDisabledReason() === null) {
      this.renameRequested.emit();
    }
  }

  requestEncoding(): void {
    if (this.context().encodingDisabledReason === null && this.encodingButton) {
      this.encodingRequested.emit(this.menuTriggerElement ?? this.encodingButton.nativeElement);
    }
  }

  chooseFiles(): void {
    if (this.uploadDisabledReason() === null) {
      this.fileInput?.nativeElement.click();
    }
  }

  handleFiles(event: Event): void {
    const input = event.target as HTMLInputElement;
    const files = Object.freeze(Array.from(input.files ?? []));
    input.value = '';
    if (files.length > 0) {
      this.filesSelected.emit(files);
    }
    if (files.length === 0) queueMicrotask(() => this.focusAddFiles());
  }

  emitFilter(event: Event): void {
    this.filterChanged.emit((event.target as HTMLInputElement).value);
  }

  clearFilter(): void {
    this.filterChanged.emit('');
    queueMicrotask(() => this.focusSearch());
  }

  focusSearch(): void {
    this.searchInput?.nativeElement.focus();
  }

  focusAddFiles(): void {
    this.focusMenu();
  }
}
