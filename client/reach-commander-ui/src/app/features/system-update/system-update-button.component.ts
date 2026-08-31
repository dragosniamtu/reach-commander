import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  ViewChild,
  computed,
  input,
  output,
} from '@angular/core';
import { SystemUpdateStatusDto } from '../../core/api/api.models';

interface CurrentVersionPresentation {
  readonly label: string;
  readonly accessibleLabel: string;
}

@Component({
  selector: 'app-system-update-button',
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './system-update-button.component.html',
  styleUrl: './system-update-button.component.scss',
})
export class SystemUpdateButtonComponent {
  readonly status = input<SystemUpdateStatusDto | null>(null);
  readonly pending = input(false);
  readonly check = output<void>();
  readonly open = output<void>();

  @ViewChild('trigger', { read: ElementRef })
  private trigger?: ElementRef<HTMLButtonElement>;

  readonly canCheck = computed(() => {
    const status = this.status();
    return !this.pending() &&
      status?.supported === true &&
      status.reasonCode !== 'version_pinned' &&
      (status.phase === 'current' || status.phase === 'unavailable');
  });
  readonly canActivate = computed(() =>
    !this.pending() && (this.status()?.canApply === true || this.canCheck()));
  readonly accessibleSummary = computed(() => {
    const status = this.status();
    if (this.pending()) {
      return 'Checking for updates';
    }

    const summary = updateLabel(status);
    if (!this.canCheck()) {
      return summary;
    }

    return status?.phase === 'unavailable'
      ? `Retry update check. ${summary}`
      : `Check for updates. ${summary}`;
  });
  readonly currentVersion = computed<CurrentVersionPresentation>(() => {
    const status = this.status();
    if (!status) {
      return {
        label: 'v…',
        accessibleLabel: 'Current ReachCommander version is loading',
      };
    }

    const currentVersion = status.currentVersion;
    if (!currentVersion?.trim()) {
      return {
        label: 'Unknown',
        accessibleLabel: 'Current ReachCommander version is unavailable',
      };
    }

    return {
      label: currentVersion,
      accessibleLabel: `Current ReachCommander version ${currentVersion}`,
    };
  });
  readonly tooltip = computed(() => {
    const status = this.status();
    const metadata = [
      status?.channel ? `Channel: ${status.channel}` : null,
      status?.lastCheckedAt ? `Last checked: ${status.lastCheckedAt}` : null,
      status?.phase === 'blocked' ? status.detail : null,
    ].filter((value): value is string => value !== null);
    return [this.accessibleSummary(), ...metadata].join('\n');
  });

  get triggerElement(): HTMLElement | null {
    return this.trigger?.nativeElement ?? null;
  }

  focusTrigger(): void {
    this.trigger?.nativeElement.focus();
  }

  requestOpen(): void {
    if (!this.canActivate()) {
      return;
    }

    if (this.status()?.canApply) {
      this.open.emit();
    } else {
      this.check.emit();
    }
  }
}

export function updateLabel(status: SystemUpdateStatusDto | null): string {
  if (!status || status.phase === 'checking') {
    return 'Checking for updates';
  }

  if (status.reasonCode === 'version_pinned') {
    return 'Updates disabled while version-pinned';
  }

  switch (status.phase) {
    case 'current':
      return 'ReachCommander is up to date';
    case 'available':
      return `Update available: ${status.targetVersion ?? 'new version'}`;
    case 'blocked':
      return 'Update waiting for operations to finish';
    case 'applying':
      return 'Updating ReachCommander';
    case 'completed':
      return 'ReachCommander update completed';
    case 'rolledBack':
      return 'Previous version restored after update failure';
    case 'failed':
      return 'Update requires administrator attention';
    case 'unavailable':
      return `System updates unavailable: ${status.detail ?? 'Unsupported installation.'}`;
    default:
      return 'System updates unavailable';
  }
}
