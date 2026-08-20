import { ArchiveFormat, FileEntryDto } from '../api/api.models';
import { parentLogicalPath } from './path-utils';

export type PanelSide = 'left' | 'right';
export type FileSortColumn = 'name' | 'extension' | 'size' | 'modifiedAt' | 'attributes';
export type SortDirection = 'ascending' | 'descending';

export interface FilesystemLocation {
  readonly kind: 'filesystem';
  readonly sourceId: string;
  readonly path: string;
}

export interface ArchiveLocation {
  readonly kind: 'archive';
  readonly sourceId: string;
  readonly archivePath: string;
  readonly internalPath: string;
}

export type PanelLocation = FilesystemLocation | ArchiveLocation;

export interface ArchivePanelMetadata {
  readonly format: ArchiveFormat;
  readonly volumeCount: number;
}

export interface DirectoryTab {
  readonly id: string;
  readonly label: string;
  readonly location: PanelLocation;
}

export interface PanelState {
  readonly tabs: readonly DirectoryTab[];
  readonly activeTabId: string;
  readonly cursorIndex: number;
  readonly selectedItems: ReadonlySet<string>;
  readonly selectionAnchor: number | null;
  readonly sortColumn: FileSortColumn;
  readonly sortDirection: SortDirection;
  readonly filter: string;
  readonly entries: readonly FileEntryDto[];
  readonly loading: boolean;
  readonly errorCode: string | null;
  readonly errorDetail: string | null;
  readonly archiveMetadata: ArchivePanelMetadata | null;
  readonly requestToken: number;
}

export function locationSourceId(location: PanelLocation): string {
  return location.sourceId;
}

export function locationDisplayPath(location: PanelLocation, sourceName: string): string {
  return location.kind === 'filesystem'
    ? `${sourceName}:${location.path}`
    : `${sourceName}:${location.archivePath}!${location.internalPath}`;
}

export function locationParent(location: PanelLocation): PanelLocation {
  if (location.kind === 'filesystem') {
    return { ...location, path: parentLogicalPath(location.path) };
  }

  if (location.internalPath !== '/') {
    return { ...location, internalPath: parentLogicalPath(location.internalPath) };
  }

  return {
    kind: 'filesystem',
    sourceId: location.sourceId,
    path: parentLogicalPath(location.archivePath),
  };
}
