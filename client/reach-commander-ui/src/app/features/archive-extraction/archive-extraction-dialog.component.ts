import { A11yModule } from '@angular/cdk/a11y';
import {
  AfterViewInit,
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  HostListener,
  ViewChild,
  computed,
  effect,
  inject,
  output,
} from '@angular/core';
import { ArchiveFormat } from '../../core/api/api.models';
import { ArchiveExtractionStore } from '../../core/state/archive-extraction-store';
import { FileSizePipe } from '../../shared/pipes/file-size.pipe';

@Component({
  selector: 'app-archive-extraction-dialog',
  imports: [A11yModule, FileSizePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './archive-extraction-dialog.component.html',
  styleUrl: './archive-extraction-dialog.component.scss',
})
export class ArchiveExtractionDialogComponent implements AfterViewInit {
  readonly store = inject(ArchiveExtractionStore);
  readonly closeRequested = output<void>();
  readonly issues = computed(() => {
    const preview = this.store.state().preview;
    return preview ? [...preview.violations, ...preview.conflicts] : [];
  });
  readonly terminal = computed(() => [
    'completed', 'cancelled', 'failed', 'recoveryRequired',
  ].includes(this.store.state().phase));
  readonly statusLabel = computed(() => {
    const state = this.store.state();
    switch (state.phase) {
      case 'previewing': return 'Inspecting archive';
      case 'review': return 'Ready to extract';
      case 'starting': return 'Starting extraction';
      case 'cancelling': return 'Cancelling extraction';
      default: return state.operation
        ? this.operationLabel(state.operation.state)
        : state.phase === 'failed' ? 'Failed' : 'Extract archive';
    }
  });

  @ViewChild('dialog', { read: ElementRef, static: true })
  private dialog!: ElementRef<HTMLDialogElement>;

  @ViewChild('firstIssue', { read: ElementRef })
  private firstIssue?: ElementRef<HTMLElement>;

  constructor() {
    inject(DestroyRef).onDestroy(() => this.store.close());
    effect(() => {
      const state = this.store.state();
      if (state.phase === 'review' && state.preview?.canExecute === false) {
        queueMicrotask(() => this.firstIssue?.nativeElement.focus());
      }
    });
  }

  ngAfterViewInit(): void {
    const dialog = this.dialog.nativeElement;
    if (typeof dialog.showModal === 'function') {
      dialog.showModal();
    } else {
      dialog.setAttribute('open', '');
    }
    dialog.focus();
  }

  formatLabel(format: ArchiveFormat): string {
    switch (format) {
      case 'zip': return 'ZIP';
      case 'rar': return 'RAR';
      case 'sevenZip': return '7-Zip';
    }
  }

  operationLabel(state: string): string {
    switch (state) {
      case 'queued': return 'Queued';
      case 'extracting': return 'Extracting';
      case 'finalizing': return 'Finalizing';
      case 'completed': return 'Completed';
      case 'cancelled': return 'Cancelled';
      case 'recoveryRequired': return 'Recovery required';
      default: return 'Failed';
    }
  }

  start(): void {
    void this.store.execute();
  }

  cancel(): void {
    void this.store.cancel();
  }

  reviewAgain(): void {
    void this.store.reviewAgain();
  }

  requestClose(): void {
    const phase = this.store.state().phase;
    if (phase === 'review' || this.terminal()) {
      this.closeRequested.emit();
    }
  }

  handleNativeCancel(event: Event): void {
    event.preventDefault();
    this.handleEscape();
  }

  @HostListener('keydown', ['$event'])
  handleDialogKeydown(event: KeyboardEvent): void {
    event.stopPropagation();
    if (event.key !== 'Escape') {
      return;
    }
    event.preventDefault();
    this.handleEscape();
  }

  private handleEscape(): void {
    const phase = this.store.state().phase;
    if (phase === 'review' || this.terminal()) {
      this.closeRequested.emit();
      return;
    }
    if (
      phase === 'running' &&
      this.store.canCancel() &&
      window.confirm('Cancel the archive extraction?')
    ) {
      this.cancel();
    }
  }
}
