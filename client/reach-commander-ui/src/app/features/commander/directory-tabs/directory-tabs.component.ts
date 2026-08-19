import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import { DirectoryTab } from '../../../core/state/commander.models';

@Component({
  selector: 'app-directory-tabs',
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './directory-tabs.component.html',
  styleUrl: './directory-tabs.component.scss',
})
export class DirectoryTabsComponent {
  readonly tabs = input.required<readonly DirectoryTab[]>();
  readonly activeTabId = input.required<string>();
  readonly tabSelected = output<string>();
  readonly tabClosed = output<string>();
  readonly tabCreated = output<void>();
}
