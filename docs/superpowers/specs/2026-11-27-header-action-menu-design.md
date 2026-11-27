# Header action menu

The user selected one Menu button with icons and text labels for the top toolbar.

The header retains the brand, active folder and search field. One Menu button opens
a bounded, scrollable panel containing the existing file actions and application
controls. File actions retain their SVGs, labels, disabled reasons and active-pane
context. Application controls include installation when available, current transfer,
settings (disabled), theme, update status/current version, system metrics, password
change and logout. Keep status and progress visible inside the menu.

The user also requested a **Subtitles** item containing both subtitle features.
Use an expandable group inside Menu for **Generate Subtitles**, **Repair subtitles**
and the existing **Subtitle jobs** history. Keep icons and labels for each action.
Expanding the group keeps Menu open, and collapsed children are excluded from
keyboard navigation. Choosing an action retains its existing eligibility and
returns dialog focus to Menu.

Use a nonmodal dialog popup because the panel contains both commands and a native
theme select. Enter/Space open the button; Up/Down navigate controls; Escape and
outside clicks dismiss it. Tab remains usable and leaving the popup closes it.
Menu key events must not operate the underlying file pane. Selecting a command
closes the menu, and dialogs return focus to the persistent Menu button. Account
dialogs live outside the popup so closing the menu cannot hide them.

Reuse existing stores and action handlers; no server or deployment changes.
Verify menu dismissal, keyboard isolation, disabled actions, file picker, account
dialog and modal focus restoration. Run frontend tests, production build and browser
acceptance, including all three themes and the supported 680px minimum viewport.
