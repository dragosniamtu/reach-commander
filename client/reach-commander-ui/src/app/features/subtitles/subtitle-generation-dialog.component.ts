import { A11yModule } from '@angular/cdk/a11y';
import { DatePipe } from '@angular/common';
import {
  AfterViewInit,
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  HostListener,
  ViewChild,
  computed,
  inject,
  output,
} from '@angular/core';
import { SubtitleFilePhase } from './subtitle-generation.models';
import { SubtitleGenerationStore } from './subtitle-generation.store';

@Component({
  selector: 'app-subtitle-generation-dialog',
  imports: [A11yModule, DatePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './subtitle-generation-dialog.component.html',
  styleUrl: './subtitle-generation-dialog.component.scss',
})
export class SubtitleGenerationDialogComponent implements AfterViewInit {
  readonly store = inject(SubtitleGenerationStore);
  readonly closeRequested = output<void>();
  readonly summary = computed(() => {
    const files = this.store.selectedJob()?.files ?? [];
    const count = (phase: SubtitleFilePhase) => files.filter((file) => file.phase === phase).length;
    const completed = count('completed');
    const skipped = count('skipped');
    const failed = count('failed');
    const cancelled = count('cancelled');
    return {
      total: files.length,
      finished: completed + skipped + failed + cancelled,
      completed,
      skipped,
      failed,
      cancelled,
    };
  });

  @ViewChild('dialog', { read: ElementRef, static: true })
  private dialog!: ElementRef<HTMLDialogElement>;

  ngAfterViewInit(): void {
    const dialog = this.dialog.nativeElement;
    if (typeof dialog.showModal === 'function') dialog.showModal();
    else dialog.setAttribute('open', '');
    dialog.focus();
  }

  label(value: string): string {
    return value.charAt(0).toUpperCase() + value.slice(1).replaceAll('_', ' ');
  }

  setLanguage(language: 'english' | 'romanian', event: Event): void {
    this.store[language].set((event.target as HTMLInputElement).checked);
  }

  setSkipExisting(event: Event): void {
    this.store.skipExisting.set((event.target as HTMLInputElement).checked);
  }

  nativeCancel(event: Event): void {
    event.preventDefault();
    this.closeRequested.emit();
  }

  @HostListener('keydown', ['$event'])
  handleKeydown(event: KeyboardEvent): void {
    event.stopPropagation();
    if (event.key === 'Escape') {
      event.preventDefault();
      this.closeRequested.emit();
    }
  }
}
