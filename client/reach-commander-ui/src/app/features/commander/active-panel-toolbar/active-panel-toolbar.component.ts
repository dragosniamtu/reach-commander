import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  ViewChild,
  input,
  output,
} from '@angular/core';
import { PanelSide } from '../../../core/state/commander.models';

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
}

@Component({
  selector: 'app-active-panel-toolbar',
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './active-panel-toolbar.component.html',
  styleUrl: './active-panel-toolbar.component.scss',
})
export class ActivePanelToolbarComponent {
  readonly context = input.required<ActivePanelToolbarContext>();
  readonly filter = input.required<string>();
  readonly sourceManagementSupported = input(false);
  readonly sourceManagementPending = input(false);
  readonly sourceManagementDisabledReason = input<string | null>(null);
  readonly sourceManagementRetryAvailable = input(false);
  readonly renameRequested = output<void>();
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
  @ViewChild('addFilesButton', { read: ElementRef })
  private addFilesButton?: ElementRef<HTMLButtonElement>;
  @ViewChild('addSourceButton', { read: ElementRef })
  private addSourceButton?: ElementRef<HTMLButtonElement>;

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
      this.sourceRequested.emit(this.addSourceButton.nativeElement);
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
    queueMicrotask(() => this.focusAddFiles());
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
    this.addFilesButton?.nativeElement.focus();
  }
}
