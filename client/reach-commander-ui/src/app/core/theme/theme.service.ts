import { DOCUMENT } from '@angular/common';
import { Injectable, InjectionToken, computed, inject, signal } from '@angular/core';

export type ReachCommanderTheme = 'default' | 'norton';

export const THEME_STORAGE = new InjectionToken<Storage>('ReachCommander theme storage', {
  providedIn: 'root',
  factory: () => localStorage,
});

@Injectable({ providedIn: 'root' })
export class ThemeService {
  static readonly storageKey = 'reachcommander.theme.v1';

  private readonly document = inject(DOCUMENT);
  private readonly storage = inject(THEME_STORAGE);
  private readonly mutableTheme = signal<ReachCommanderTheme>('default');

  readonly theme = this.mutableTheme.asReadonly();
  readonly isNorton = computed(() => this.theme() === 'norton');

  constructor() {
    this.apply(this.readPreference());
  }

  toggle(): void {
    this.setTheme(this.isNorton() ? 'default' : 'norton');
  }

  setTheme(theme: ReachCommanderTheme): void {
    this.apply(theme);
    try {
      if (theme === 'norton') {
        this.storage.setItem(ThemeService.storageKey, theme);
      } else {
        this.storage.removeItem(ThemeService.storageKey);
      }
    } catch {
      // A disabled or full browser store must not prevent a visual preference change.
    }
  }

  private readPreference(): ReachCommanderTheme {
    try {
      return this.storage.getItem(ThemeService.storageKey) === 'norton' ? 'norton' : 'default';
    } catch {
      return 'default';
    }
  }

  private apply(theme: ReachCommanderTheme): void {
    this.mutableTheme.set(theme);
    if (theme === 'norton') {
      this.document.documentElement.dataset['theme'] = theme;
    } else {
      this.document.documentElement.removeAttribute('data-theme');
    }
  }
}
