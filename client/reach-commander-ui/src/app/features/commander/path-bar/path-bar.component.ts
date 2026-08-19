import { ChangeDetectionStrategy, Component, ElementRef, input, output, signal, viewChild } from '@angular/core';

@Component({
  selector: 'app-path-bar',
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './path-bar.component.html',
  styleUrl: './path-bar.component.scss',
})
export class PathBarComponent {
  readonly path = input.required<string>();
  readonly pathCommitted = output<string>();
  readonly editing = signal(false);
  readonly draft = signal('');
  private readonly editor = viewChild<ElementRef<HTMLInputElement>>('editor');

  focusEditor(): void {
    this.draft.set(this.path());
    this.editing.set(true);
    setTimeout(() => {
      const element = this.editor()?.nativeElement;
      element?.focus();
      element?.select();
    });
  }

  updateDraft(event: Event): void {
    this.draft.set((event.target as HTMLInputElement).value);
  }

  handleKey(event: KeyboardEvent): void {
    if (event.key === 'Enter') {
      event.preventDefault();
      this.commit();
    } else if (event.key === 'Escape') {
      event.preventDefault();
      this.editing.set(false);
    }
  }

  commit(): void {
    this.editing.set(false);
    this.pathCommitted.emit(this.draft());
  }
}
