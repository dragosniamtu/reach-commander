import { matchesFileFilter } from './wildcard-filter';

describe('matchesFileFilter', () => {
  it.each([
    ['notes.txt', 'txt', '', true],
    ['notes.txt', 'txt', 'note', true],
    ['notes.txt', 'txt', 'TXT', true],
    ['archive.tar.gz', 'gz', '*.gz', true],
    ['archive.tar.gz', 'gz', '*.zip', false],
    ['report-01.pdf', 'pdf', 'report-??.pdf', true],
    ['report-1.pdf', 'pdf', 'report-??.pdf', false],
    ['photo', null, 'photo*', true],
    ['photo', null, '*photo', true],
    ['photo-1', null, 'photo', true],
    ['photo-1', null, 'photo.', false],
    ['a+b[1].txt', 'txt', 'a+b[1].*', true],
    ['A+B[1].TXT', 'TXT', 'a+b[1].*', true],
    ['Résumé.md', 'md', 'résumé.?d', true],
  ])('%s with %s filtered by %s is %s', (name, extension, filter, expected) => {
    expect(matchesFileFilter(name, extension, filter)).toBe(expected);
  });
});
