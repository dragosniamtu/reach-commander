import { Pipe, PipeTransform } from '@angular/core';

@Pipe({ name: 'fileSize', standalone: true })
export class FileSizePipe implements PipeTransform {
  transform(value: number | null): string {
    if (value === null) {
      return '—';
    }

    if (value < 1024) {
      return `${value} B`;
    }

    const units = ['KiB', 'MiB', 'GiB', 'TiB', 'PiB'];
    const exponent = Math.min(Math.floor(Math.log(value) / Math.log(1024)), units.length);
    const amount = value / 1024 ** exponent;
    return `${amount.toFixed(1)} ${units[exponent - 1]}`;
  }
}
