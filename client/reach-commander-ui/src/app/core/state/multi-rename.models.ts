import {
  BatchRenameOperationDto,
  BatchRenamePreviewDto,
  BatchRenameRulesDto,
  FileEntryDto,
} from '../api/api.models';
import { PanelSide } from './commander.models';

export interface MultiRenameContext {
  readonly panelSide: PanelSide;
  readonly sourceId: string;
  readonly sourceName: string;
  readonly directoryPath: string;
  readonly entries: readonly FileEntryDto[];
  readonly isAvailable: boolean;
  readonly isReadOnly: boolean;
}

export interface MultiRenameState {
  readonly open: boolean;
  readonly context: MultiRenameContext | null;
  readonly rules: BatchRenameRulesDto;
  readonly preview: BatchRenamePreviewDto | null;
  readonly operation: BatchRenameOperationDto | null;
  readonly previewPending: boolean;
  readonly actionPending: boolean;
  readonly disabledReason: string | null;
  readonly errorCode: string | null;
  readonly requestToken: number;
}
