import { Pipe, PipeTransform } from '@angular/core';

@Pipe({ name: 'byteRate', standalone: true })
export class ByteRatePipe implements PipeTransform {
  transform(value: number | null): string {
    if (value === null || !Number.isFinite(value) || value < 0) {
      return '—';
    }

    if (value < 1024) {
      return `${Math.round(value)} B/s`;
    }

    const units = ['KiB/s', 'MiB/s', 'GiB/s', 'TiB/s'];
    const exponent = Math.min(
      Math.floor(Math.log(value) / Math.log(1024)),
      units.length,
    );
    return `${(value / 1024 ** exponent).toFixed(1)} ${units[exponent - 1]}`;
  }
}
