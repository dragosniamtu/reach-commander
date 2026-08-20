import { A11yModule } from '@angular/cdk/a11y';
import {
  ChangeDetectionStrategy,
  Component,
  AfterViewInit,
  ElementRef,
  HostListener,
  ViewChild,
  computed,
  inject,
  input,
} from '@angular/core';
import { UploadStore } from '../../core/state/upload-store';
import { FileSizePipe } from '../../shared/pipes/file-size.pipe';

@Component({
  selector: 'app-upload-dialog',
  imports: [A11yModule, FileSizePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './upload-dialog.component.html',
  styleUrl: './upload-dialog.component.scss',
})
export class UploadDialogComponent implements AfterViewInit {
  readonly store = inject(UploadStore);
  readonly opener = input.required<HTMLElement | null>();
  readonly state = this.store.state;
  readonly canClose = computed(() => !this.store.isPending());
  readonly canStart = computed(() => {
    const state = this.state();
    return (
      ['review', 'failed', 'cancelled'].includes(state.phase) &&
      !state.limitsPending &&
      state.limits !== null &&
      state.preflightIssues.length === 0
    );
  });

  @ViewChild('primaryButton', { read: ElementRef })
  private primaryButton?: ElementRef<HTMLButtonElement>;
  @ViewChild('closeButton', { read: ElementRef })
  private closeButton?: ElementRef<HTMLButtonElement>;

  ngAfterViewInit(): void {
    const primary = this.primaryButton?.nativeElement;
    (primary && !primary.disabled ? primary : this.closeButton?.nativeElement)?.focus();
  }

  @HostListener('document:keydown.escape', ['$event'])
  closeFromEscape(event: Event): void {
    event.preventDefault();
    event.stopPropagation();
    this.close();
  }

  start(): void {
    this.store.start();
  }

  removeFile(index: number): void {
    this.store.removeFile(index);
  }

  cancelUpload(): void {
    this.store.cancel();
  }

  close(): void {
    if (!this.canClose() || !this.store.close()) {
      return;
    }

    this.opener()?.focus();
  }

  backdropClick(event: MouseEvent): void {
    if (event.target === event.currentTarget) {
      this.close();
    }
  }
}
