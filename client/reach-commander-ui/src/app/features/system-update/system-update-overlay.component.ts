import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  computed,
  inject,
  input,
  output,
  signal,
} from '@angular/core';
import { SystemUpdateStatusDto } from '../../core/api/api.models';
import { SystemUpdateClientError } from '../../core/state/system-update.store';
import { buildSystemUpdateProgress } from './system-update-progress';
import { buildSystemUpdateTrace } from './system-update-trace';
import { SystemUpdateSupportBundleService } from './system-update-support-bundle.service';

@Component({
  selector: 'app-system-update-overlay',
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './system-update-overlay.component.html',
  styleUrl: './system-update-overlay.component.scss',
  providers: [SystemUpdateSupportBundleService],
})
export class SystemUpdateOverlayComponent {
  private readonly nowMilliseconds = signal(Date.now());
  readonly supportBundle = inject(SystemUpdateSupportBundleService);

  readonly status = input.required<SystemUpdateStatusDto>();
  readonly reconnecting = input(false);
  readonly error = input<SystemUpdateClientError | null>(null);
  readonly dismissed = output<void>();

  readonly terminal = computed(
    () => this.status().phase === 'rolledBack' || this.status().phase === 'failed',
  );
  readonly progress = computed(() =>
    buildSystemUpdateProgress(this.status(), this.reconnecting(), this.nowMilliseconds()),
  );
  readonly trace = computed(() => buildSystemUpdateTrace(this.status(), this.nowMilliseconds()));
  readonly title = computed(() => {
    if (this.status().phase === 'rolledBack') {
      return 'Previous version restored';
    }
    if (this.status().phase === 'failed') {
      return 'Update requires attention';
    }
    if (this.status().phase === 'completed') {
      return 'Activating updated app';
    }
    if (this.progress().recovery.length > 0) {
      return 'Recovering previous version';
    }
    return this.reconnecting() ? 'Reconnecting to ReachCommander' : 'Updating ReachCommander';
  });

  constructor() {
    const interval = globalThis.setInterval(() => this.nowMilliseconds.set(Date.now()), 5_000);
    inject(DestroyRef).onDestroy(() => globalThis.clearInterval(interval));
  }
}
