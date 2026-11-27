import { TextEncodingKind } from '../api/api.models';

const encodingLabels: Readonly<Record<TextEncodingKind, string>> = {
  auto: 'Auto detect',
  utf8: 'UTF-8',
  utf8Bom: 'UTF-8 with BOM',
  utf16LittleEndian: 'UTF-16 LE',
  utf16BigEndian: 'UTF-16 BE',
  windows1250: 'Windows-1250',
  windows1251: 'Windows-1251 (Cyrillic)',
  windows1252: 'Windows-1252',
  windows1253: 'Windows-1253 (Greek)',
  windows1254: 'Windows-1254 (Turkish)',
  windows1255: 'Windows-1255 (Hebrew)',
  windows1256: 'Windows-1256 (Arabic)',
  windows1257: 'Windows-1257 (Baltic)',
  windows1258: 'Windows-1258 (Vietnamese)',
  thai874: 'Windows-874 (Thai)',
  shiftJis: 'Shift JIS (Japanese)',
  gbk: 'GBK (Simplified Chinese)',
  big5: 'Big5 (Traditional Chinese)',
  korean949: 'Windows-949 (Korean)',
  latin1: 'ISO-8859-1 (Latin-1)',
  latin2: 'ISO-8859-2 (Latin-2)',
};

export const sourceEncodingOptions = (Object.keys(encodingLabels) as TextEncodingKind[]).map(
  (value) => ({ value, label: encodingLabels[value] }),
);

export function encodingLabel(encoding: TextEncodingKind | null): string {
  return encoding ? encodingLabels[encoding] ?? 'Unknown' : 'Unknown';
}
