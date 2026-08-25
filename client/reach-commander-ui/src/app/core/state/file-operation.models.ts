export type TransferOperationKind = 'copy' | 'move';

export interface CapturedFileOperationContext {
  readonly kind: TransferOperationKind;
  readonly sourceId: string;
  readonly logicalPaths: readonly string[];
  readonly destinationSourceId: string;
  readonly destinationLogicalDirectory: string;
  readonly selectedNames: readonly string[];
  readonly knownTotalBytes: number | null;
}

export function freezeFileOperationContext(
  context: CapturedFileOperationContext,
): CapturedFileOperationContext {
  return Object.freeze({
    ...context,
    logicalPaths: Object.freeze([...context.logicalPaths]),
    selectedNames: Object.freeze([...context.selectedNames]),
  });
}
