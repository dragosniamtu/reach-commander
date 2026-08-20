import { FileEntryDto } from '../api/api.models';
import { PanelState } from './commander.models';
import { parentLogicalPath } from './path-utils';
import { matchesFileFilter } from './wildcard-filter';

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

  const entries = panel.entries
    .filter((entry) => matchesFileFilter(entry.name, entry.extension, panel.filter))
    .map<FileTableRow>((entry) => ({ ...entry, isParent: false }));
  const directories = entries.filter((entry) => entry.type === 'directory');
  const files = entries.filter((entry) => entry.type !== 'directory');
  directories.sort(comparator(panel));
  files.sort(comparator(panel));

  const parentPath = tab.location.kind === 'filesystem'
    ? tab.location.path
    : tab.location.internalPath;
  const parent = tab.location.kind === 'filesystem' && parentPath === '/'
    ? []
    : [parentRow(parentPath)];
  return [...parent, ...directories, ...files];
}

export function fileTableRowExplanation(panel: PanelState, row: FileTableRow): string | null {
  if (row.archiveRole === 'secondary') {
    return 'Archive volume part. Open the primary volume instead.';
  }
  if (row.archiveFormatHint) {
    return 'Supported archive. Open as a read-only folder.';
  }

  const location = panel.tabs.find((tab) => tab.id === panel.activeTabId)?.location;
  if (location?.kind === 'archive' && looksLikeArchive(row.name)) {
    return 'Archive file inside an archive. Nested archive browsing is unavailable.';
  }
  return null;
}

function comparator(panel: PanelState): (left: FileTableRow, right: FileTableRow) => number {
  const direction = panel.sortDirection === 'ascending' ? 1 : -1;
  return (left, right) => {
    const primary = compareColumn(left, right, panel.sortColumn);
    return primary === 0 ? nameCollator.compare(left.name, right.name) : primary * direction;
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
      return compareModifiedAt(left.modifiedAt, right.modifiedAt);
    case 'attributes':
      return nameCollator.compare(left.attributes, right.attributes);
  }
}

function compareModifiedAt(left: string | null, right: string | null): number {
  const leftTime = parseDate(left);
  const rightTime = parseDate(right);
  if (leftTime === null && rightTime === null) return 0;
  if (leftTime === null) return 1;
  if (rightTime === null) return -1;
  return leftTime - rightTime;
}

function parseDate(value: string | null): number | null {
  if (!value) return null;
  const parsed = Date.parse(value);
  return Number.isFinite(parsed) ? parsed : null;
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
    archiveFormatHint: null,
    archiveRole: null,
    isParent: true,
  };
}

function looksLikeArchive(name: string): boolean {
  return /(?:\.zip|\.rar|\.7z)(?:\.\d{3})?$|\.(?:r|z)\d{2}$/i.test(name);
}
