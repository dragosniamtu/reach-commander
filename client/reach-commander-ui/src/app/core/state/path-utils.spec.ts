import { normalizeLogicalPath, parentLogicalPath } from './path-utils';

describe('logical path utilities', () => {
  it('normalizes repeated separators and dot segments', () => {
    expect(normalizeLogicalPath('/Movies//./Sci-Fi/')).toBe('/Movies/Sci-Fi');
    expect(normalizeLogicalPath('Movies\\Kids')).toBe('/Movies/Kids');
  });

  it('rejects parent traversal and physical rooted paths', () => {
    expect(normalizeLogicalPath('/Movies/../Secret')).toBeNull();
    expect(normalizeLogicalPath('C:\\Windows')).toBeNull();
    expect(normalizeLogicalPath('\\\\server\\share')).toBeNull();
  });

  it('returns the logical parent without leaving root', () => {
    expect(parentLogicalPath('/Movies/Sci-Fi')).toBe('/Movies');
    expect(parentLogicalPath('/Movies')).toBe('/');
    expect(parentLogicalPath('/')).toBe('/');
  });
});
