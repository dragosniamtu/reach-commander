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
  readonly sourceSelected = output<string>();

  description(source: SourceDto): string {
    if (!source.isAvailable) {
      return `${source.name}: unavailable, ${source.isReadOnly ? 'read-only' : 'read/write'}`;
    }

    const capacity = source.totalBytes === null || source.freeBytes === null
      ? 'capacity unavailable'
      : `${formatBytes(source.usedBytes ?? 0)} used, ${formatBytes(source.freeBytes)} free, ${formatBytes(source.totalBytes)} total`;
    return `${source.name}: available, ${capacity}, ${source.isReadOnly ? 'read-only' : 'read/write'}`;
  }
}

function formatBytes(value: number): string {
  if (value < 1024) {
    return `${value} B`;
  }

  const units = ['KiB', 'MiB', 'GiB', 'TiB', 'PiB'];
  const exponent = Math.min(Math.floor(Math.log(value) / Math.log(1024)), units.length);
  return `${(value / 1024 ** exponent).toFixed(1)} ${units[exponent - 1]}`;
}
