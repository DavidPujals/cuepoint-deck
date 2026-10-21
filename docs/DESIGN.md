# Cuepoint Deck interface design

Cuepoint Deck's interface follows Apple's Human Interface Guidelines (developer.apple.com/design), translated to a
Windows app. The guidelines' three principles shape everything here:

- **Hierarchy**: controls sit in a calm layer above the content, and the content (the song and its cards) leads.
- **Harmony**: one type family, one symbol family, one tint colour, concentric rounded shapes.
- **Consistency**: every question looks and behaves the same, every list works the same way, every action has one
  name everywhere it appears.

The app runs in a dark booth beside Resolume, so it is dark only.

## Foundations

| Guideline | Here |
|---|---|
| Use the system font | Segoe UI Variable (Windows' counterpart to SF Pro), Display for titles and timecodes. Timecodes use tabular figures so digits never jitter. |
| Use system symbols | Segoe Fluent Icons (Windows' counterpart to SF Symbols) for every icon. No emoji, no text arrows. |
| Semantic colour | Label / secondary / tertiary text, separator, one tint (blue) for default buttons, selection, focus and links. Green only ever means live or connected, orange a warning, red something destructive. |
| Typography hierarchy | Large Title for the song, Title for headings, Headline for groups, Body, Secondary, Caption. Section headings in title case and secondary colour, not shouting capitals. |
| Consistent shapes | 7 px controls, 10–12 px groups and cards, capsules for badges and the toast. |
| Selection | Tinted while the list has focus, grey when it doesn't, as on the Mac. |
| Focus | A tint outline on whatever has keyboard focus in sheets and Settings. (Main-window buttons don't take focus, so the number keys always reach the deck.) |

## Window and toolbar

- **One window, two modes**, switched with a **segmented control** (Show | Edit) instead of a button whose label
  flips. A segmented control is the guideline for switching views of the same content.
- **The toolbar** holds the controls that apply everywhere. Leading: connection status. Centre: the setlist (the
  content-specific control). Trailing: issues, role, pin, the mode switch, shortcuts, settings.
- **Show exceptions, not defaults**. The role badge appears only for Follow; the warning button only when there is a
  warning. A healthy toolbar is quiet.
- **Popovers** carry detail on demand, tied to the control that opened them: connection details, things needing
  attention (each with the action that fixes it), keyboard shortcuts. They close when you click away.
- **Feedback** that doesn't need a decision is a toast at the bottom of the window. It never blocks or takes a click.
  The status strip of text it replaces is gone.

## Questions: alerts and sheets

Every modal question is a **sheet**: it drops from under the toolbar of the window that asked, dims that window, and
has no title bar of its own. Return presses the default button and Esc cancels.

Alerts follow the guideline's anatomy: the app icon, a title that states the situation or asks the question, one
sentence of context, and buttons named for what they do. Never Yes/No/OK for a choice.

| Moment | Title | Buttons (leading … trailing) |
|---|---|---|
| Quit | Quit Cuepoint Deck? | Quit · **Cancel** (default, so a stray Return can't close the deck mid-service), with "Don't ask again" |
| Leave unsaved edits | Do you want to save the changes to “X”? | Don't Save · Cancel · **Save** |
| Saved elsewhere meanwhile | “X” was changed on another computer | Use Their Version · Cancel · **Save Mine** |
| Delete a song | Delete “X”? | Delete Song · **Cancel** |
| Delete a setlist | Delete the setlist “X”? | Delete Setlist · **Cancel** |
| Suggestions over existing segments | Replace the existing segments? | Replace · Cancel · **Add as Drafts** |
| No columns to pick from | Resolume isn't connected | New Song from File… · **OK** |

Destructive buttons have a red label, sit apart at the leading edge, and are never the default. Asking for a name
(new, rename, duplicate setlist) is a sheet that explains a clash under the field as you type and keeps Create
disabled until the name works, instead of failing after it closes.

Prefer **undo** to a question. Deleting a segment doesn't ask; Ctrl+Z brings it back, and nothing is written until
Save.

## Menus

- Context menus on everything you can act on: cards, setlist songs, sidebar songs and setlists, segments, running
  order entries. They hold the commands for that one thing, most-used first, grouped with separators.
- Items are title-case verbs ("Jump Now", "Use in Show Mode"). An ellipsis means more input follows ("Rename…").
  The keyboard shortcut sits right-aligned beside the item. Destructive items are red and last.
- **Pull-down buttons** gather related, less frequent commands: the sidebar's **+** (new song or setlist), the
  editor's **⋯**, **Auto** (suggestions), **Set Start / Set End**.
- **Pop-up buttons** (the setlist chooser, Settings choices) show the current choice and mark it with a checkmark.

## Settings

A normal window, not a dialog, with **panes** (General, Resolume, Library, Media, About) in a symbol-over-label tab
bar. **Changes apply as you make them**: switches and pop-ups at once, text fields on Return or when you move on.
There is no Save or Cancel. A value that can't be used is held back and explained under its group. Options are in
grouped forms with a footnote under each group saying what it does. Frequently changed options stay out of Settings
and live where they are used: the pin, the setlist, the card size (Ctrl + / −).

## Edit mode

- A **sidebar** (Songs with search, Setlists) and a **detail** area, the layout of library apps. Setlists moved here
  from their own window, so editing a running order no longer means opening and closing a dialog.
- **Empty states** say what the area is for and offer the action that fills it.
- **One prominent action per view**: Save in the song header (lit only with unsaved changes), Add at Playhead over the
  segments, Use in Show Mode on a setlist.
- **It always fits the window.** Panels share the width instead of overflowing it. The sidebar has a toggle in the
  toolbar and, on narrow windows, slides over the content instead of taking a column (as sidebars do on iPad and in
  narrow Mac windows). As space runs out, secondary controls step aside before primary ones, and inner panels scroll
  inside themselves; the page itself never scrolls.
- Lists have the Mac's **+ − bar** underneath; the segments list's **+** is a menu with every way to add. The inspector replaces rows of nudge buttons with a stepper and a
  **Set** pull-down, a combo box for names, a checkbox that reveals the optional end time, and a ringed swatch for the
  current colour.

## Where to look in the code

- Tokens and control styles: `src/CuepointDeck.App/Theme.xaml`
- Sheets and alerts: `Views/Sheet.cs`, `Views/Alert.xaml`, `Views/PromptSheet.xaml`, `Views/PickClipSheet.xaml`
- Toolbar, popovers and toast: `MainWindow.xaml`, `ViewModels/ShellViewModel.cs`
- Pull-down buttons, placeholders, commit-on-Return: `Ui.cs`
- Screen captures without anyone at the screen: `DebugCapture.cs` (see its header for the modes)
