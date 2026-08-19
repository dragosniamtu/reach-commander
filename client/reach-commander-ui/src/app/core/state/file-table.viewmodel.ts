import { FileEntryDto } from '../api/api.models';
import { PanelState } from './commander.models';
import { parentLogicalPath } from './path-utils';

export interface FileTableRow extends FileEntryDto {
  readonly isParent: boolean;
}

const nameCollator = new Intl.Collator(undefined, {
  numeric: true,
  sensitivity: 'base',
});

export function buildVisibleRows(panel: PanelState): readonly FileTableRow[] {
  const tab = panel.tabs.find((candidate) => candidate.id === panel.activeTabId);
  if (!tab) {
    return [];
  }

  const filter = panel.filter.trim().toLocaleLowerCase();
  const entries = panel.entries
    .filter((entry) => !filter ||
      entry.name.toLocaleLowerCase().includes(filter) ||
      entry.extension?.toLocaleLowerCase().includes(filter))
    .map<FileTableRow>((entry) => ({ ...entry, isParent: false }));
  const directories = entries.filter((entry) => entry.type === 'directory');
  const files = entries.filter((entry) => entry.type !== 'directory');
  directories.sort(comparator(panel));
  files.sort(comparator(panel));

  const parent = tab.path === '/' ? [] : [parentRow(tab.path)];
  return [...parent, ...directories, ...files];
}

function comparator(panel: PanelState): (left: FileTableRow, right: FileTableRow) => number {
  const direction = panel.sortDirection === 'ascending' ? 1 : -1;
  return (left, right) => {
    const primary = compareColumn(left, right, panel.sortColumn);
    return primary === 0
      ? nameCollator.compare(left.name, right.name)
      : primary * direction;
  };
}

function compareColumn(
  left: FileTableRow,
  right: FileTableRow,
  column: PanelState['sortColumn'],
): number {
  switch (column) {
    case 'name':
      return nameCollator.compare(left.name, right.name);
    case 'extension':
      return nameCollator.compare(left.extension ?? '', right.extension ?? '');
    case 'size':
      return (left.size ?? 0) - (right.size ?? 0);
    case 'modifiedAt':
      return Date.parse(left.modifiedAt) - Date.parse(right.modifiedAt);
    case 'attributes':
      return nameCollator.compare(left.attributes, right.attributes);
  }
}

function parentRow(path: string): FileTableRow {
  return {
    name: '..',
    relativePath: parentLogicalPath(path),
    type: 'directory',
    size: null,
    modifiedAt: '',
    extension: null,
    isReadOnly: true,
    isSymbolicLink: false,
    attributes: '',
    isParent: true,
  };
}
