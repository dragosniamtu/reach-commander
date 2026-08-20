const wildcard = /[*?]/;
const regexSyntax = /[\\^$.*+?()[\]{}|]/g;

export function matchesFileFilter(
  name: string,
  extension: string | null,
  rawFilter: string,
): boolean {
  const filter = rawFilter.trim();
  if (!filter) {
    return true;
  }

  if (!wildcard.test(filter)) {
    const needle = filter.toLocaleLowerCase();
    return (
      name.toLocaleLowerCase().includes(needle) ||
      (extension?.toLocaleLowerCase().includes(needle) ?? false)
    );
  }

  const source = [...filter]
    .map((character) =>
      character === '*' ? '.*' : character === '?' ? '.' : character.replace(regexSyntax, '\\$&'),
    )
    .join('');

  return new RegExp(`^${source}$`, 'iu').test(name);
}
