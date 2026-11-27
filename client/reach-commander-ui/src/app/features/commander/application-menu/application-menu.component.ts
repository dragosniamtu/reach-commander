import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  HostListener,
  ViewChild,
  inject,
  signal,
} from '@angular/core';

@Component({
  selector: 'app-application-menu',
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './application-menu.component.html',
  styleUrl: './application-menu.component.scss',
})
export class ApplicationMenuComponent {
  readonly opened = signal(false);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  @ViewChild('trigger') private trigger?: ElementRef<HTMLButtonElement>;
  @ViewChild('panel') private panel?: ElementRef<HTMLElement>;

  get triggerElement(): HTMLButtonElement | null {
    return this.trigger?.nativeElement ?? null;
  }

  toggle(): void {
    if (this.opened()) {
      this.close();
    } else {
      this.opened.set(true);
      queueMicrotask(() => this.opened() && this.controls()[0]?.focus());
    }
  }

  close(restoreFocus = true): void {
    this.opened.set(false);
    if (restoreFocus) this.triggerElement?.focus();
  }

  triggerKeydown(event: KeyboardEvent): void {
    // Keep native button navigation separate while retaining Commander shortcuts.
    if (['Enter', ' ', 'ArrowDown', 'ArrowUp', 'Tab'].includes(event.key) ||
      (event.key === 'Escape' && this.opened())) {
      event.stopPropagation();
    }
    if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
      event.preventDefault();
      this.opened.set(true);
      queueMicrotask(() => {
        if (!this.opened()) return;
        const controls = this.controls();
        controls[event.key === 'ArrowUp' ? controls.length - 1 : 0]?.focus();
      });
    } else if (event.key === 'Escape' && this.opened()) {
      this.close();
    }
  }

  panelKeydown(event: KeyboardEvent): void {
    event.stopPropagation();
    if (event.key === 'Escape') {
      event.preventDefault();
      this.close();
      return;
    }
    // Preserve native select interaction, including its arrow keys.
    if (event.target instanceof HTMLSelectElement) return;
    if (!['ArrowDown', 'ArrowUp', 'Home', 'End'].includes(event.key)) return;
    event.preventDefault();
    const controls = this.controls();
    const index = controls.indexOf(document.activeElement as HTMLElement);
    const next =
      event.key === 'Home'
        ? 0
        : event.key === 'End'
          ? controls.length - 1
          : (index + (event.key === 'ArrowDown' ? 1 : -1) + controls.length) % controls.length;
    controls[next]?.focus();
  }

  selectAction(event: MouseEvent): void {
    const button = (event.target as Element).closest<HTMLButtonElement>('button');
    if (button && !button.disabled && !button.hasAttribute('data-menu-disclosure') &&
      this.panel?.nativeElement.contains(button)) this.close();
  }

  @HostListener('document:pointerdown', ['$event'])
  @HostListener('document:focusin', ['$event'])
  dismissOutside(event: Event): void {
    if (
      this.opened() &&
      event.target instanceof Node &&
      !this.host.nativeElement.contains(event.target)
    ) {
      this.close(false);
    }
  }

  private controls(): HTMLElement[] {
    return Array.from(
      this.panel?.nativeElement.querySelectorAll<HTMLElement>(
        'button:not(:disabled), select:not(:disabled)',
      ) ?? [],
    ).filter((control) => !control.closest('[hidden]'));
  }
}
