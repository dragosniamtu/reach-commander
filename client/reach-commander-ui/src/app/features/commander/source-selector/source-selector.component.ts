import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import { SourceDto } from '../../../core/api/api.models';

@Component({
  selector: 'app-source-selector',
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './source-selector.component.html',
  styleUrl: './source-selector.component.scss',
})
export class SourceSelectorComponent {
  readonly sources = input.required<readonly SourceDto[]>();
  readonly selectedSourceId = input.required<string>();
  readonly removalEnabled = input(false);
  readonly removalPending = input(false);
  readonly sourceSelected = output<string>();
  readonly sourceRemovalRequested = output<SourceRemovalRequest>();

  description(source: SourceDto): string {
    if (!source.isAvailable) {
      return `${source.name}: unavailable, ${source.isReadOnly ? 'read-only' : 'read/write'}`;
    }

    const capacity = source.totalBytes === null || source.freeBytes === null
      ? 'capacity unavailable'
      : `${formatBytes(source.usedBytes ?? 0)} used, ${formatBytes(source.freeBytes)} free, ${formatBytes(source.totalBytes)} total`;
    return `${source.name}: available, ${capacity}, ${source.isReadOnly ? 'read-only' : 'read/write'}`;
  }

  removeTitle(source: SourceDto): string {
    if (this.sources().length <= 1) {
      return 'ReachCommander must keep at least one source mapping.';
    }
    if (!this.removalEnabled()) {
      return 'Source removal requires an installer-managed deployment with the latest installer.';
    }
    if (this.removalPending()) {
      return 'A source-management operation is already in progress.';
    }
    return `Remove the ${source.name} mapping. The host folder and its files will be preserved.`;
  }

  requestRemoval(source: SourceDto, event: MouseEvent): void {
    if (!this.removalEnabled() || this.removalPending() || this.sources().length <= 1) {
      return;
    }
    this.sourceRemovalRequested.emit({
      source,
      opener: event.currentTarget as HTMLButtonElement,
    });
  }
}

export interface SourceRemovalRequest {
  readonly source: SourceDto;
  readonly opener: HTMLButtonElement;
}

function formatBytes(value: number): string {
  if (value < 1024) {
    return `${value} B`;
  }

  const units = ['KiB', 'MiB', 'GiB', 'TiB', 'PiB'];
  const exponent = Math.min(Math.floor(Math.log(value) / Math.log(1024)), units.length);
  return `${(value / 1024 ** exponent).toFixed(1)} ${units[exponent - 1]}`;
}
