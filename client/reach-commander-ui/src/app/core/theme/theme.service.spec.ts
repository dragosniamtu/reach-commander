import { DOCUMENT } from '@angular/common';
import { TestBed } from '@angular/core/testing';
import { THEME_STORAGE, ThemeService } from './theme.service';

describe('ThemeService', () => {
  let document: Document;
  let storage: Storage;

  beforeEach(() => {
    storage = memoryStorage();
    TestBed.configureTestingModule({
      providers: [{ provide: THEME_STORAGE, useValue: storage }],
    });
    document = TestBed.inject(DOCUMENT);
    document.documentElement.removeAttribute('data-theme');
  });

  afterEach(() => document.documentElement.removeAttribute('data-theme'));

  it('defaults safely without a stored override', () => {
    const service = TestBed.inject(ThemeService);

    expect(service.theme()).toBe('default');
    expect(service.isNorton()).toBe(false);
    expect(document.documentElement.getAttribute('data-theme')).toBeNull();
  });

  it('restores the stored Norton value', () => {
    storage.setItem(ThemeService.storageKey, 'norton');

    const service = TestBed.inject(ThemeService);

    expect(service.theme()).toBe('norton');
    expect(service.isNorton()).toBe(true);
    expect(service.isWindows95()).toBe(false);
    expect(document.documentElement.dataset['theme']).toBe('norton');
  });

  it('restores the stored Windows 95 value', () => {
    storage.setItem(ThemeService.storageKey, 'windows95');

    const service = TestBed.inject(ThemeService);

    expect(service.theme()).toBe('windows95');
    expect(service.isWindows95()).toBe(true);
    expect(document.documentElement.dataset['theme']).toBe('windows95');
  });

  it('ignores an unrecognized stored value', () => {
    storage.setItem(ThemeService.storageKey, 'solarized');

    const service = TestBed.inject(ThemeService);

    expect(service.theme()).toBe('default');
    expect(document.documentElement.getAttribute('data-theme')).toBeNull();
  });

  it('persists both alternate themes and removes the default override', () => {
    const service = TestBed.inject(ThemeService);

    service.setTheme('norton');
    expect(service.theme()).toBe('norton');
    expect(storage.getItem(ThemeService.storageKey)).toBe('norton');
    expect(document.documentElement.dataset['theme']).toBe('norton');

    service.setTheme('windows95');
    expect(service.theme()).toBe('windows95');
    expect(storage.getItem(ThemeService.storageKey)).toBe('windows95');
    expect(document.documentElement.dataset['theme']).toBe('windows95');

    service.setTheme('default');
    expect(service.theme()).toBe('default');
    expect(storage.getItem(ThemeService.storageKey)).toBeNull();
    expect(document.documentElement.getAttribute('data-theme')).toBeNull();
  });

  it('still applies in-memory state when storage access fails', () => {
    const unavailableStorage = memoryStorage();
    vi.spyOn(unavailableStorage, 'getItem').mockImplementation(() => {
      throw new DOMException('Blocked');
    });
    vi.spyOn(unavailableStorage, 'setItem').mockImplementation(() => {
      throw new DOMException('Blocked');
    });
    vi.spyOn(unavailableStorage, 'removeItem').mockImplementation(() => {
      throw new DOMException('Blocked');
    });
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [{ provide: THEME_STORAGE, useValue: unavailableStorage }],
    });
    document = TestBed.inject(DOCUMENT);

    const service = TestBed.inject(ThemeService);
    service.setTheme('windows95');
    expect(service.isWindows95()).toBe(true);
    expect(document.documentElement.dataset['theme']).toBe('windows95');

    service.setTheme('default');
    expect(service.isNorton()).toBe(false);
    expect(document.documentElement.getAttribute('data-theme')).toBeNull();
  });
});

function memoryStorage(): Storage {
  const values = new Map<string, string>();
  return {
    get length() {
      return values.size;
    },
    clear: () => values.clear(),
    getItem: (key) => values.get(key) ?? null,
    key: (index) => [...values.keys()][index] ?? null,
    removeItem: (key) => {
      values.delete(key);
    },
    setItem: (key, value) => {
      values.set(key, value);
    },
  };
}
