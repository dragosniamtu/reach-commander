import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { SystemUpdateStatusDto } from '../../core/api/api.models';
import { SystemUpdateClientError } from '../../core/state/system-update.store';

@Component({
  selector: 'app-system-update-overlay',
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './system-update-overlay.component.html',
  styleUrl: './system-update-overlay.component.scss',
})
export class SystemUpdateOverlayComponent {
  readonly status = input.required<SystemUpdateStatusDto>();
  readonly reconnecting = input(false);
  readonly error = input<SystemUpdateClientError | null>(null);
  readonly dismissed = output<void>();

  readonly terminal = computed(() =>
    this.status().phase === 'rolledBack' || this.status().phase === 'failed',
  );
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
    return this.reconnecting() ? 'Reconnecting to ReachCommander' : 'Updating ReachCommander';
  });
}
