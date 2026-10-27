# Cricket Auction

A Windows desktop auction desk built with .NET 10, WPF/MVVM, SQLite/EF Core, ClosedXML, and Open XML SDK. No login, accounts, or cloud services. Auction data, teams, history, and cached photos stay on the computer.

## Run

Install the .NET 10 SDK on Windows, then run from this directory:

```powershell
dotnet restore CricketAuction.slnx
dotnet run --project CricketAuction.App
```

The first launch opens the dashboard and a small Create Auction dialog. Subsequent launches open the last auction automatically. Cancel the dialog to use Open or Restore instead. Only one application instance is allowed per Windows session.

## Live auction workflow

1. Create an auction: enter your tournament and auction names, choose how many teams (1–64), and enter each team name. Teams are saved together with the auction and immediately appear in **Sold to**. Names start blank so you can configure your own event. Open **Teams & squads** later to edit teams, add more, or set budgets. New teams have unlimited budgets until you set one.
2. **Import players** accepts `.xlsx` or UTF-8 comma-separated `.csv`. Column names belong in the first row; Excel uses the first worksheet. A sample registration file is in `samples/players.csv`.
3. Review valid/invalid rows and duplicate decisions. Different names can share a phone number and import normally. A matching name AND normalized phone number is flagged, including repeats within the same file; Indian +91 prefixes are normalized for matching. Duplicates default to **KeepExisting**. **UpdateExisting** changes registration details but preserves original import data and auction results. **ImportAsNew** deliberately creates another player, including distinct players with the same name and phone. Clear jersey mistakes (O instead of zero, or a numeric jersey name swapped with the player's name) are corrected with a note in the preview; original source values are retained. Other invalid rows are skipped with a reason.
4. Confirm import. Public photos download automatically, four at a time. **Cancel download** lets you continue offline; **Cache photos** retries missing photos later. Failed/private/deleted images use a placeholder.
5. Search a name, phone, jersey name or number. Click a result or press Enter. The player profile and auction buttons stay beside the list.
6. Press **S**, enter a rupee amount, choose a team, review, then confirm. Spending and statistics update immediately. Insufficient budgets prevent the sale; disable enforcement in Settings if your tournament does not use budgets.
7. Press **U** and confirm to mark a player unsold. **Unsold players** shows the current unsold list.
8. Generate the unsold PowerPoint before beginning the next round. Select all unsold players, current-round unsold players, or individual players. Each slide contains one player with an embedded photo or placeholder.
9. Finish all currently available players, then **Start re-auction**. Unsold players become available in the next round; prior results remain in history. Repeat for further rounds.
10. **Export Excel → Complete auction report** writes Players, Auction History, Teams and Summary sheets. Other choices export only sold, unsold, all, or currently filtered players.
11. **Backup** saves a portable `.db` including cached images. **Restore** verifies the backup and opens a new copy; it never overwrites the existing auction.

Click grid headers to sort by name, amount, team, auction time, or another column. Horizontal scrolling exposes additional columns at smaller window sizes. Player details scroll while Sold/Unsold remain fixed. Filters cover role, batting, bowling, city, round, and team; the navigation buttons filter status. Team filtering also provides a sortable squad list.

## Keyboard

| Shortcut | Action |
|---|---|
| Ctrl+F | Focus and select search text |
| Enter in search/results | Open selected or first matching player |
| S / U | Sold dialog / unsold confirmation |
| Esc | Close dialog or clear player selection |
| Ctrl+E | Excel export |
| Ctrl+P | PowerPoint generation |
| F5 | Reload saved data and statistics |

Single-letter actions are disabled while typing in text inputs. Dialogs always require explicit confirmation; Enter in a text field does not silently sell a player.

## History, undo, and money

- Amounts use integer paise in SQLite, avoiding floating-point rounding errors. Display uses Indian grouping (`₹1,00,000`). Enter `2500`, `2,500`, or `₹2,500`; up to two decimal places are supported. A sale must be greater than zero.
- Sales, unsold decisions, round transitions, and undo operations are transactional. A failure cannot leave a half-saved sale or partially changed round.
- History is append-only and protected by SQLite triggers. Player editing cannot change auction state. There is no player deletion control.
- **Undo last action** restores the previous state and adds an audit event. Undoing a re-auction restores the entire group and session round together. Undo must proceed in reverse action order. A round cannot be undone after new players have been imported into it.
- To correct an older sale, undo later actions in order, then enter the corrected results. There is no unaudited history editor.

## Photos and offline use

Public HTTPS photo URLs and Google Drive `open?id=`, `/file/d/.../view`, and `uc?id=` formats are supported, including links with resource keys. Credentials are never requested or stored. Internet access is used only for missing photos. Downloads stream to a temporary file (deleted automatically), with a two-minute timeout per attempt and a 512 MiB download limit. If the original Drive download or conversion fails, the app tries a 1,600-pixel Drive preview. Run **Cache photos** again to retry missing photos after updating the app. Google Drive may restrict both downloads and previews; use **Change photo** for inaccessible images.

Local and downloaded photos use the bundled Magick.NET converter, including JPEG, PNG, HEIC/HEIF, AVIF, WebP, GIF, BMP, TIFF, ICO, JPEG XL and PSD. Files are detected by content, automatically oriented, resized to fit within 1,600 × 1,600 pixels without enlarging smaller images, and stored as PNG blobs inside the database. Animated and multipage images use their first frame. Local files no longer have a 10 MB limit and are read from a stream. Extremely large images (over 250 megapixels or 100,000 pixels on either axis), unavailable codecs, damaged files, and insufficient temporary disk space are reported with a clear error; support is not unlimited. Conversion uses bounded memory with disk spillover, and player photo conversion runs off the UI thread.

Database backups contain converted photos. **Change photo** overrides the remote photo. An optional Settings cache folder receives extra image copies; the database remains authoritative. PPT generation uses only cached photos and never needs internet access. Photos retain their aspect ratio. Distribute the entire published app folder, including native codec DLLs.

## Sessions, files, settings

Default data location:

```text
%LOCALAPPDATA%\CricketAuction\
  last-auction.txt
  Auction_*.db
  logs\yyyy-MM-dd.log
```

Each database represents one auction session and contains Players, Teams, Sessions (including settings), and History. No external Excel file is needed after import. Use **Open** for another auction database. For a custom data location:

```powershell
dotnet run --project CricketAuction.App -- --data-dir D:\AuctionData
```

Settings include tournament/auction names, starting round (before players are imported), budget enforcement, default team budget, optional photo cache folder, default Excel/PPT folders, and Light/Dark/System theme. Currency is intentionally fixed to INR for this cricket auction workflow. System theme is read at startup/refresh. No database passwords or access accounts are configured; protect backups like other local registration files.

Logs record operation types, record counts/IDs, exception types and stack traces, without player names, phone numbers, photo URLs, or registration contents. Use the built-in Backup command for a consistent copy while the application is running.

## Additional live-event tools

- **Pick next player** randomly selects an available player.
- **Big screen** opens a separate live player window; move it to a projector. F11 toggles fullscreen; Esc closes it. It updates with selection and auction results.
- **Teams & squads** shows roster, spending, remaining budget, owner/captain, and allows storing a local team logo.
- **Generate PPT → Selected team squad** exports purchased players for a team.

Queue ordering, an auction countdown, and automatic presentation generation after every round are not included. Presentations are generated explicitly so the operator chooses the roster and destination.

## Build a distributable Windows application

```powershell
dotnet publish CricketAuction.App -c Release -r win-x64 --self-contained true -o artifacts/win-x64
```

Or run the release build-and-publish script (does not run tests):

```powershell
.\scripts\publish.ps1
```

The script builds only the application and its dependencies. Optional existing runtime verification can be requested with `-SmokeTest`; it is off by default and does not cover every PDF/PNG scenario below.

## Release and manual smoke checklist

Use a separate sample auction and an empty output folder. Do not use live event data. These are manual checks to perform before distributing a release; a successful build alone does not verify startup or exports.

- [ ] Run `dotnet build CricketAuction.App/CricketAuction.App.csproj -c Release`. Review errors and dependency warnings. The PDF dependency currently brings ImageSharp 1.0.4 with NuGet security warnings; review an upgrade before distributing.
- [ ] Run `scripts/publish.ps1` and launch `artifacts/win-x64/CricketAuction.App.exe` with a separate `--data-dir` folder. Confirm startup, dialogs and Light/Dark themes render correctly without XAML errors.
- [ ] Create teams and import sample players. Switch Registered, Sold, Unsold and Available; exactly one navigation item should be highlighted, its count legible, and its heading/list correct.
- [ ] Before the first sale, check the readiness summary: teams configured, players imported and linked photos cached. Missing teams/players must prevent bidding; missing photos remain optional.
- [ ] Cache public images alongside a private Drive link, invalid link and AVIF file. Watch completed/saved counts, cancel using the loader button or Escape, and confirm saved photos remain after reopening. Retry the remaining downloads.
- [ ] Open Photo failures and verify every failed player has an ID, name, reason and next step. Convert AVIF to JPG/PNG externally, then use Photo to select it; automatic AVIF conversion is not included. Confirm import completion does not hide the failure summary.
- [ ] Rotate a photo several times, close/reopen the auction, then Reset photo. Confirm the original orientation returns. Replacing a photo should clear its previous rotation backup.
- [ ] In Teams & squads compare budgets, spent and remaining amounts. Check NEAR LIMIT at 80%, LIMIT REACHED at 100% (including zero budget), OVER BUDGET when enforcement is disabled, and Unlimited for a blank budget.
- [ ] Generate a team poster PNG. Confirm the team, player count and available photo count before saving; cancel once and confirm no file is produced. Open the generated PNG and check all players, long names, photos/placeholders and the last row.
- [ ] Generate a team purchase PDF with multiple teams and more than seven players in one team. Confirm listed teams/counts match the output; open the PDF and inspect pagination, text, photos and totals. Teams without purchases are excluded.
- [ ] Export Excel and PowerPoint, confirm selection/counts, then open the files and check their contents. Verify an unwritable destination gives a useful error without leaving a broken final file.
- [ ] Back up and reopen the sample auction. Confirm sales, photos and rotation reset remain available. Record build date, command, output folder and manual results before distributing the entire published folder.

Photo failures are available for the latest download attempt in the current session. Fix links using Edit or replace images using Photo, then Cache photos to retry. Reset photo restores the image from before the first rotation, rather than undoing just the latest quarter-turn.

Launch `artifacts\win-x64\CricketAuction.App.exe`. Copy the **entire folder**, including native SQLite dependencies, to another Windows x64 computer. The self-contained publish does not require installing .NET. Excel and PowerPoint are not required for generating files. To view slides, use PowerPoint or a compatible viewer. This is a portable unsigned distribution, not an MSI installer.

## Architecture and verification

```text
CricketAuction.App             WPF views, view model, dialogs, DI composition
CricketAuction.Domain          Entities, validation, currency, contracts, statistics
CricketAuction.Infrastructure  EF/SQLite, auction operations, import/export, photos, backups
CricketAuction.Tests           Business and file-format integration tests against real SQLite
```

EF Core provides the unit-of-work/repository boundary; there is no redundant generic repository layer. Short-lived contexts isolate each operation. Money/budget checks and state changes occur inside the same transaction. Schema version 1 is validated on open/restore; unsupported versions are refused instead of being modified blindly.

```powershell
dotnet build CricketAuction.slnx
dotnet test CricketAuction.Tests
```

Tests exercise state transitions, multiple rounds, audited undo, exact/insufficient budgets, duplicate policies, validation, searches, totals, CSV quoting, Excel round trips, backup/reopen including photos, and PowerPoint schema/slide/image validation.

Opt-in WPF runtime verification (uses isolated sample data and exits automatically):

```powershell
.\CricketAuction.App\bin\Debug\net10.0-windows\CricketAuction.App.exe --smoke-test --data-dir D:\Tournament\artifacts\smoke
```

It creates an isolated auction on every run, imports an Excel fixture, renders the dashboard and core dialogs to PNG files, and verifies two sales across two rounds, search, statistics, unsold PPT generation, complete Excel export, backup, restore, and reopen. It writes `smoke-ok.txt` on success; a runtime failure writes `smoke-error.txt`. Generated PPTs are also validated with the Open XML validator before replacing the destination file. Public Drive availability depends on the supplied file's permissions and cannot be guaranteed by offline tests.
