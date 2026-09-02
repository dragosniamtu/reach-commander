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
  inject,
  output,
  signal,
} from '@angular/core';
import { TextEncodingKind } from '../../core/api/api.models';
import { TextEncodingStore } from '../../core/state/text-encoding-store';

@Component({
  selector: 'app-text-encoding-dialog',
  imports: [A11yModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './text-encoding-dialog.component.html',
  styleUrl: './text-encoding-dialog.component.scss',
})
export class TextEncodingDialogComponent implements AfterViewInit {
  readonly store = inject(TextEncodingStore);
  readonly closeRequested = output<void>();
  readonly recoveryAcknowledged = signal(false);
  readonly operationActive = computed(() =>
    ['starting', 'running', 'cancelling'].includes(this.store.state().phase),
  );
  readonly terminal = computed(() =>
    ['completed', 'completedWithErrors', 'cancelled', 'failed'].includes(
      this.store.state().phase,
    ),
  );
  readonly recoveryRequired = computed(() =>
    this.store.state().operation?.rows.some((row) => row.result === 'recoveryRequired') ?? false,
  );
  readonly canClose = computed(() =>
    (this.store.state().phase === 'review' || this.terminal()) &&
    (!this.recoveryRequired() || this.recoveryAcknowledged()),
  );
  readonly statusLabel = computed(() => {
    const state = this.store.state();
    switch (state.phase) {
      case 'previewing': return 'Inspecting selected files';
      case 'review': return 'Ready to convert';
      case 'starting': return 'Starting conversion';
      case 'running': return 'Converting files';
      case 'cancelling': return 'Cancelling conversion';
      case 'completed': return 'Conversion completed';
      case 'completedWithErrors': return 'Conversion completed with errors';
      case 'cancelled': return 'Conversion cancelled';
      case 'failed': return 'Conversion failed';
      default: return 'Change text encoding';
    }
  });

  @ViewChild('dialog', { read: ElementRef, static: true })
  private dialog!: ElementRef<HTMLDialogElement>;

  @ViewChild('sourceEncoding', { read: ElementRef })
  private sourceEncoding?: ElementRef<HTMLSelectElement>;

  constructor() {
    inject(DestroyRef).onDestroy(() => this.store.close());
  }

  ngAfterViewInit(): void {
    const dialog = this.dialog.nativeElement;
    if (typeof dialog.showModal === 'function') {
      dialog.showModal();
    } else {
      dialog.setAttribute('open', '');
    }
    queueMicrotask(() => this.sourceEncoding?.nativeElement.focus());
  }

  encodingLabel(encoding: TextEncodingKind | null): string {
    switch (encoding) {
      case 'auto': return 'Auto detect';
      case 'utf8': return 'UTF-8';
      case 'utf8Bom': return 'UTF-8 with BOM';
      case 'utf16LittleEndian': return 'UTF-16 LE';
      case 'utf16BigEndian': return 'UTF-16 BE';
      case 'windows1250': return 'Windows-1250';
      case 'windows1252': return 'Windows-1252';
      default: return 'Unknown';
    }
  }

  statusText(status: string): string {
    switch (status) {
      case 'ready': return 'Ready';
      case 'warning': return 'Warning';
      default: return 'Invalid';
    }
  }

  resultText(result: string): string {
    switch (result) {
      case 'converted': return 'Converted';
      case 'skipped': return 'Skipped';
      case 'failed': return 'Failed';
      case 'recoveryRequired': return 'Recovery required';
      default: return 'Pending';
    }
  }

  setSourceEncoding(event: Event): void {
    this.store.setSourceEncoding((event.target as HTMLSelectElement).value as TextEncodingKind);
  }

  setOutputEncoding(event: Event): void {
    this.store.setOutputEncoding((event.target as HTMLSelectElement).value as TextEncodingKind);
  }

  start(): void {
    void this.store.execute();
  }

  cancel(): void {
    void this.store.cancel();
  }

  reviewAgain(): void {
    this.recoveryAcknowledged.set(false);
    void this.store.reviewAgain();
  }

  acknowledgeRecovery(event: Event): void {
    this.recoveryAcknowledged.set((event.target as HTMLInputElement).checked);
  }

  requestClose(): void {
    if (this.canClose()) {
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
    if (this.canClose()) {
      this.closeRequested.emit();
      return;
    }
    if (
      this.operationActive() &&
      this.store.canCancel() &&
      window.confirm('Cancel the text encoding operation?')
    ) {
      this.cancel();
    }
  }
}
