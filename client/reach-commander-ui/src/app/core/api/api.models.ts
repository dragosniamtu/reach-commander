export type FileEntryType = 'file' | 'directory' | 'other';

export interface SourceDto {
  readonly id: string;
  readonly name: string;
  readonly isAvailable: boolean;
  readonly isReadOnly: boolean;
  readonly totalBytes: number | null;
  readonly usedBytes: number | null;
  readonly freeBytes: number | null;
  readonly defaultLeft: boolean;
  readonly defaultRight: boolean;
}

export interface FileEntryDto {
  readonly name: string;
  readonly relativePath: string;
  readonly type: FileEntryType;
  readonly size: number | null;
  readonly modifiedAt: string;
  readonly extension: string | null;
  readonly isReadOnly: boolean;
  readonly isSymbolicLink: boolean;
  readonly attributes: string;
}

export interface ApiProblemDetails {
  readonly type: string;
  readonly title: string;
  readonly status: number;
  readonly detail: string;
  readonly instance?: string;
  readonly code: string;
}

export abstract class CommanderApiPort {
  abstract getSources(): Promise<readonly SourceDto[]>;

  abstract listFiles(sourceId: string, path: string): Promise<readonly FileEntryDto[]>;

  abstract getInfo(sourceId: string, path: string): Promise<FileEntryDto>;
}
