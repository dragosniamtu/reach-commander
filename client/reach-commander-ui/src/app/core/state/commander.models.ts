import { FileEntryDto } from '../api/api.models';

export type PanelSide = 'left' | 'right';
export type FileSortColumn = 'name' | 'extension' | 'size' | 'modifiedAt' | 'attributes';
export type SortDirection = 'ascending' | 'descending';

export interface DirectoryTab {
  readonly id: string;
  readonly label: string;
  readonly sourceId: string;
  readonly path: string;
}

export interface PanelState {
  readonly sourceId: string;
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
  readonly requestToken: number;
}
