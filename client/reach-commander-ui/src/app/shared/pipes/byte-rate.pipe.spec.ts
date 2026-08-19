import { ByteRatePipe } from './byte-rate.pipe';

describe('ByteRatePipe', () => {
  const pipe = new ByteRatePipe();

  it('formats unavailable, bytes, and IEC rates compactly', () => {
    expect(pipe.transform(null)).toBe('—');
    expect(pipe.transform(512)).toBe('512 B/s');
    expect(pipe.transform(1536)).toBe('1.5 KiB/s');
    expect(pipe.transform(2 * 1024 ** 2)).toBe('2.0 MiB/s');
  });
});
