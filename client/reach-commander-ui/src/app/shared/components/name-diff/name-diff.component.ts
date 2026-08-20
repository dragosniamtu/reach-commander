import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';

@Component({
  selector: 'app-name-diff',
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './name-diff.component.html',
  styleUrl: './name-diff.component.scss',
})
export class NameDiffComponent {
  readonly oldName = input.required<string>();
  readonly newName = input.required<string>();
  readonly segments = computed(() => diffName(this.oldName(), this.newName()));
}

interface NameSegments {
  readonly prefix: string;
  readonly changed: string;
  readonly suffix: string;
}

function diffName(oldName: string, newName: string): NameSegments {
  let prefixLength = 0;
  const maximumPrefix = Math.min(oldName.length, newName.length);
  while (prefixLength < maximumPrefix && oldName[prefixLength] === newName[prefixLength]) {
    prefixLength++;
  }

  let suffixLength = 0;
  const maximumSuffix = Math.min(oldName.length - prefixLength, newName.length - prefixLength);
  while (
    suffixLength < maximumSuffix &&
    oldName[oldName.length - suffixLength - 1] === newName[newName.length - suffixLength - 1]
  ) {
    suffixLength++;
  }

  return {
    prefix: newName.slice(0, prefixLength),
    changed: newName.slice(
      prefixLength,
      suffixLength === 0 ? newName.length : newName.length - suffixLength,
    ),
    suffix: suffixLength === 0 ? '' : newName.slice(newName.length - suffixLength),
  };
}
