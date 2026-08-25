import { DOCUMENT } from '@angular/common';
import {
  DestroyRef,
  Injectable,
  InjectionToken,
  computed,
  inject,
  signal,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { SwUpdate } from '@angular/service-worker';

export interface BeforeInstallPromptEvent extends Event {
  prompt(): Promise<void>;
  readonly userChoice: Promise<{
    outcome: 'accepted' | 'dismissed';
    platform: string;
  }>;
}

export const PWA_RELOAD = new InjectionToken<() => void>('PWA_RELOAD', {
  providedIn: 'root',
  factory: () => () => globalThis.location.reload(),
});

@Injectable({ providedIn: 'root' })
export class PwaService {
  private readonly document = inject(DOCUMENT);
  private readonly updates = inject(SwUpdate);
  private readonly reload = inject(PWA_RELOAD);
  private readonly destroyRef = inject(DestroyRef);
  private readonly installPrompt = signal<BeforeInstallPromptEvent | null>(null);
  private systemUpdateRefresh: Promise<void> | null = null;

  readonly online = signal(true);
  readonly updateReady = signal(false);
  readonly installing = signal(false);
  readonly error = signal<string | null>(null);
  readonly canInstall = computed(() => this.installPrompt() !== null && !this.installing());

  private readonly captureInstallPrompt = (event: Event): void => {
    event.preventDefault();
    this.installPrompt.set(event as BeforeInstallPromptEvent);
    this.error.set(null);
  };

  private readonly clearInstallPrompt = (): void => {
    this.installPrompt.set(null);
  };

  private readonly markOnline = (): void => {
    this.online.set(true);
  };

  private readonly markOffline = (): void => {
    this.online.set(false);
  };

  constructor() {
    const browserWindow = this.document.defaultView;
    if (browserWindow) {
      this.online.set(browserWindow.navigator.onLine);
      browserWindow.addEventListener('beforeinstallprompt', this.captureInstallPrompt);
      browserWindow.addEventListener('appinstalled', this.clearInstallPrompt);
      browserWindow.addEventListener('online', this.markOnline);
      browserWindow.addEventListener('offline', this.markOffline);
      this.destroyRef.onDestroy(() => {
        browserWindow.removeEventListener('beforeinstallprompt', this.captureInstallPrompt);
        browserWindow.removeEventListener('appinstalled', this.clearInstallPrompt);
        browserWindow.removeEventListener('online', this.markOnline);
        browserWindow.removeEventListener('offline', this.markOffline);
      });
    }

    if (this.updates.isEnabled) {
      this.updates.versionUpdates
        .pipe(takeUntilDestroyed(this.destroyRef))
        .subscribe((event) => {
          if (event.type === 'VERSION_READY') {
            this.error.set(null);
            this.updateReady.set(true);
          } else if (event.type === 'VERSION_INSTALLATION_FAILED') {
            this.updateReady.set(false);
            this.error.set('The ReachCommander update could not be downloaded.');
          }
        });
    }
  }

  async install(): Promise<void> {
    const prompt = this.installPrompt();
    if (!prompt || this.installing()) {
      return;
    }

    this.installPrompt.set(null);
    this.installing.set(true);
    this.error.set(null);
    try {
      await prompt.prompt();
      await prompt.userChoice;
    } catch {
      this.error.set('ReachCommander installation could not be started.');
    } finally {
      this.installing.set(false);
    }
  }

  reloadForUpdate(): void {
    if (!this.updateReady()) {
      return;
    }

    this.updateReady.set(false);
    this.reload();
  }

  dismissUpdate(): void {
    this.updateReady.set(false);
  }

  refreshAfterSystemUpdate(): Promise<void> {
    if (this.systemUpdateRefresh) {
      return this.systemUpdateRefresh;
    }

    this.systemUpdateRefresh = this.activateLatestShellAndReload();
    return this.systemUpdateRefresh;
  }

  private async activateLatestShellAndReload(): Promise<void> {
    this.updateReady.set(false);
    this.error.set(null);
    try {
      if (this.updates.isEnabled) {
        const updateAvailable = await this.updates.checkForUpdate();
        if (updateAvailable) {
          await this.updates.activateUpdate();
        }
      }
    } catch {
      this.error.set('The new application shell could not be activated before reload.');
    } finally {
      this.reload();
    }
  }
}
