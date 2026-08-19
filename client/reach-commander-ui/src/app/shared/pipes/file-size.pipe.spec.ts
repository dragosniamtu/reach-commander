import { FileSizePipe } from './file-size.pipe';

describe('FileSizePipe', () => {
  const pipe = new FileSizePipe();

  it('formats null, bytes, and IEC values compactly', () => {
    expect(pipe.transform(null)).toBe('—');
    expect(pipe.transform(0)).toBe('0 B');
    expect(pipe.transform(1536)).toBe('1.5 KiB');
    expect(pipe.transform(2 * 1024 * 1024)).toBe('2.0 MiB');
  });
});
