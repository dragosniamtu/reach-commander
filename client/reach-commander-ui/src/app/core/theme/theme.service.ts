import { DOCUMENT } from '@angular/common';
import { Injectable, InjectionToken, computed, inject, signal } from '@angular/core';

export type ReachCommanderTheme = 'default' | 'norton' | 'windows95';

const storedThemes = new Set<ReachCommanderTheme>(['norton', 'windows95']);

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
  readonly isWindows95 = computed(() => this.theme() === 'windows95');

  constructor() {
    this.apply(this.readPreference());
  }

  setTheme(theme: ReachCommanderTheme): void {
    this.apply(theme);
    try {
      if (theme === 'default') this.storage.removeItem(ThemeService.storageKey);
      else this.storage.setItem(ThemeService.storageKey, theme);
    } catch {
      // A disabled or full browser store must not prevent a visual preference change.
    }
  }

  private readPreference(): ReachCommanderTheme {
    try {
      const storedTheme = this.storage.getItem(ThemeService.storageKey);
      return storedTheme !== null && storedThemes.has(storedTheme as ReachCommanderTheme)
        ? storedTheme as ReachCommanderTheme
        : 'default';
    } catch {
      return 'default';
    }
  }

  private apply(theme: ReachCommanderTheme): void {
    this.mutableTheme.set(theme);
    if (theme === 'default') {
      this.document.documentElement.removeAttribute('data-theme');
    } else {
      this.document.documentElement.dataset['theme'] = theme;
    }
  }
}
