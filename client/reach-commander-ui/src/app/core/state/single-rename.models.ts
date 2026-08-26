import {
  BatchRenameOperationDto,
  BatchRenamePreviewDto,
  FileEntryDto,
} from '../api/api.models';
import { PanelSide } from './commander.models';

export interface SingleRenameContext {
  readonly panelSide: PanelSide;
  readonly sourceId: string;
  readonly sourceName: string;
  readonly directoryPath: string;
  readonly entry: FileEntryDto;
  readonly isAvailable: boolean;
  readonly isReadOnly: boolean;
}

export interface SingleRenameCompletion {
  readonly context: SingleRenameContext;
  readonly newLogicalPath: string;
}

export interface SingleRenameState {
  readonly open: boolean;
  readonly context: SingleRenameContext | null;
  readonly newName: string;
  readonly preview: BatchRenamePreviewDto | null;
  readonly operation: BatchRenameOperationDto | null;
  readonly previewPending: boolean;
  readonly actionPending: boolean;
  readonly errorCode: string | null;
  readonly requestToken: number;
}
