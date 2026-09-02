import { FileEntryDto, SourceDto, TextEncodingKind } from '../api/api.models';
import { buildVisibleRows } from './file-table.viewmodel';
import { PanelSide, PanelState } from './commander.models';

const supportedExtensions = new Set(['srt', 'sub', 'txt', 'csv', 'nfo', 'md', 'json']);

export type TextEncodingPhase =
  | 'closed'
  | 'previewing'
  | 'review'
  | 'starting'
  | 'running'
  | 'cancelling'
  | 'completed'
  | 'completedWithErrors'
  | 'cancelled'
  | 'failed';

export interface TextEncodingContext {
  readonly panelSide: PanelSide;
  readonly sourceId: string;
  readonly sourceName: string;
  readonly directoryPath: string;
  readonly entries: readonly FileEntryDto[];
}

export interface TextEncodingContextResult {
  readonly context: TextEncodingContext | null;
  readonly error: string | null;
}

export interface TextEncodingSafeError {
  readonly code: string;
  readonly detail: string;
}

export interface TextEncodingSettings {
  readonly sourceEncoding: TextEncodingKind;
  readonly outputEncoding: TextEncodingKind;
}

export function captureTextEncodingContext(
  panelSide: PanelSide,
  panel: PanelState,
  sources: readonly SourceDto[],
): TextEncodingContextResult {
  const tab = panel.tabs.find((candidate) => candidate.id === panel.activeTabId);
  if (!tab || tab.location.kind !== 'filesystem') {
    return failure('Text encoding is available only in filesystem folders.');
  }

  const source = sources.find((candidate) => candidate.id === tab.location.sourceId);
  if (!source || !source.isAvailable) {
    return failure(source ? `${source.name} is unavailable.` : 'The active source is unavailable.');
  }
  if (source.isReadOnly) {
    return failure(`${source.name} is read-only.`);
  }

  const rows = buildVisibleRows(panel);
  const candidates = panel.selectedItems.size > 0
    ? rows.filter((row) => !row.isParent && panel.selectedItems.has(row.relativePath))
    : rows[panel.cursorIndex] && !rows[panel.cursorIndex]!.isParent
      ? [rows[panel.cursorIndex]!]
      : [];
  const hasRecognizedFile = candidates.some(
    (entry) => entry.type === 'file' && isSupportedTextFile(entry.name),
  );
  if (!hasRecognizedFile) {
    return failure('Select at least one supported text file.');
  }

  return success({
    panelSide,
    sourceId: source.id,
    sourceName: source.name,
    directoryPath: tab.location.path,
    entries: candidates,
  });
}

export function isSupportedTextFile(name: string): boolean {
  const separator = name.lastIndexOf('.');
  return separator >= 0 && supportedExtensions.has(name.slice(separator + 1).toLowerCase());
}

function success(context: TextEncodingContext): TextEncodingContextResult {
  return {
    context: Object.freeze({
      ...context,
      entries: Object.freeze(context.entries.map((entry) => Object.freeze({ ...entry }))),
    }),
    error: null,
  };
}

function failure(error: string): TextEncodingContextResult {
  return { context: null, error };
}
