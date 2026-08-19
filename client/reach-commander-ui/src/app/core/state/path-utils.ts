export function normalizeLogicalPath(input: string): string | null {
  const trimmed = input.trim();
  if (/^(?:[a-zA-Z]:[\\/]|[\\/]{2})/.test(trimmed)) {
    return null;
  }

  const segments = trimmed
    .replaceAll('\\', '/')
    .split('/')
    .filter((segment) => segment.length > 0 && segment !== '.');
  if (segments.some((segment) => segment === '..' || segment.includes('\0'))) {
    return null;
  }

  return segments.length === 0 ? '/' : `/${segments.join('/')}`;
}

export function parentLogicalPath(path: string): string {
  const normalized = normalizeLogicalPath(path);
  if (!normalized || normalized === '/') {
    return '/';
  }

  const separator = normalized.lastIndexOf('/');
  return separator <= 0 ? '/' : normalized.slice(0, separator);
}
