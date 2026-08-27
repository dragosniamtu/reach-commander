import { DOCUMENT } from '@angular/common';
import { DestroyRef, Inject, Injectable, InjectionToken, inject, signal } from '@angular/core';
import { CommanderApiPort, SystemUpdateSupportBundleDownload } from '../../core/api/api.models';
import { ProtectedStateResetService } from '../../core/auth/protected-state-reset.service';

export interface SystemUpdateSupportBundleSaver {
  save(download: SystemUpdateSupportBundleDownload): void;
}

export const SYSTEM_UPDATE_SUPPORT_BUNDLE_SAVER =
  new InjectionToken<SystemUpdateSupportBundleSaver>('SYSTEM_UPDATE_SUPPORT_BUNDLE_SAVER', {
    providedIn: 'root',
    factory: () => {
      const document = inject(DOCUMENT);
      return {
        save: (download) => {
          const objectUrl = globalThis.URL.createObjectURL(download.blob);
          const anchor = document.createElement('a');
          try {
            anchor.href = objectUrl;
            anchor.download = download.fileName;
            anchor.hidden = true;
            (document.body ?? document.documentElement).append(anchor);
            anchor.click();
          } finally {
            anchor.remove();
            globalThis.URL.revokeObjectURL(objectUrl);
          }
        },
      };
    },
  });

@Injectable()
export class SystemUpdateSupportBundleService {
  private readonly mutablePending = signal(false);
  private readonly mutableError = signal<string | null>(null);

  readonly pending = this.mutablePending.asReadonly();
  readonly error = this.mutableError.asReadonly();
  private generation = 0;

  constructor(
    private readonly api: CommanderApiPort,
    @Inject(SYSTEM_UPDATE_SUPPORT_BUNDLE_SAVER)
    private readonly saver: SystemUpdateSupportBundleSaver,
    protectedState: ProtectedStateResetService,
    destroyRef: DestroyRef,
  ) {
    const unregister = protectedState.register(() => this.reset());
    destroyRef.onDestroy(() => {
      unregister();
      this.reset();
    });
  }

  async download(): Promise<void> {
    if (this.mutablePending()) {
      return;
    }

    this.mutablePending.set(true);
    this.mutableError.set(null);
    const generation = this.generation;
    try {
      const download = await this.api.downloadSystemUpdateSupportBundle();
      if (generation === this.generation) {
        this.saver.save(download);
      }
    } catch {
      if (generation === this.generation) {
        this.mutableError.set(
          'Diagnostics could not be downloaded. On the Ubuntu host, run ' +
            'sudo reachcommander support-bundle > reachcommander-support.zip.',
        );
      }
    } finally {
      if (generation === this.generation) {
        this.mutablePending.set(false);
      }
    }
  }

  private reset(): void {
    this.generation++;
    this.mutablePending.set(false);
    this.mutableError.set(null);
  }
}
