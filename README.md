# Manager Agent — FM26 / FM27

An agent for **you, the manager**, the way players have agents. It floats in the game as a small **Agent** tab. You can drag the tab anywhere; click it to open the panel.

Version 0.3 has two duties:

- **Job hunting:** switch it on when you want a new job. The agent applies for every vacancy that matches your filters, and it can open the jobs page by itself every few in-game days.
- **Contract negotiation:** you set your wage range, contract length and the clauses you want. The agent checks the club's maximum salary, negotiates with the chairperson, and asks you before signing.

---

## 1. Install (one click)

1. Download **ManagerAgent-Setup.exe** from the [latest release](../../releases/latest) and run it.
   - Windows may say *"Windows protected your PC"* because the program isn't signed. Click **More info → Run anyway**.
2. Setup finds Football Manager by itself (Steam, Epic or Xbox app). If it doesn't, click **Browse…** and pick the game folder.
3. Click **Install**. Setup will:
   - download and add the **BepInEx** mod loader, if you don't have it yet;
   - add **Manager Agent**;
   - **start Football Manager once and check the agent works with your game version**. The very first start can take 5–10 minutes while BepInEx prepares the game. Leave the game open until Setup says *All set!*
4. If the check says your game version needs a matching build, close the game and click **Repair**. Setup builds Manager Agent for your exact version automatically. This needs internet and is a one-time ~200 MB download.

Keep `ManagerAgent-Setup.exe`. Run it again to update, repair (for example after a big FM patch) or **Uninstall**. Uninstall can keep or delete your saved preferences, and only removes BepInEx if Setup installed it.

## 2. Job hunting

Open the Agent panel. In the **Job hunting** card, press **Turn on**. While it's on:

- **The agent applies by itself.** Every day, or every 3, 7 or 14 in-game days (your choice in **Filters**), once you've left the game alone for a few seconds, it:
  1. opens Career → Head Coach Vacancies;
  2. presses the game's **Apply for all jobs** and confirms the popup;
  3. goes back to where you were.

  It also does this whenever you open that page yourself. If *Apply for all jobs* is greyed out, there's nothing new and it does nothing.
- **Only some jobs?** In **Filters**, set **Apply for** to **Only jobs matching my filters** and choose:
  - minimum club rating;
  - national teams yes or no;
  - which nations (the list fills in once the agent has seen the jobs page).

  In this mode it reads the list and applies to the matching jobs one by one (it still uses *Apply for all jobs* when every open job matches). Jobs you've already applied for are skipped.
- **If something stops it,** the panel and the log say why, and it tries again the next in-game day (or press **Check jobs now**).
- **If it can't read the in-game date,** it checks every 15 minutes of play instead and says so in the panel.
- Press **Turn off** when you're happy where you are. Nothing is applied for while it's off.

## 3. Your terms

In the **Contract negotiation** card, press **Your terms**:

- **Wage:** a minimum (never go below) and a target (what to open with). Use − / + for small steps and ≪ / ≫ for big ones. Once you've seen an offer, **Start from the last offer** sets a sensible range in one click.
- **Contract length:** how many years you want, and the shortest you'd accept.
- **Clauses:** tap each one to cycle through:
  - **Ask for:** requested one at a time, top first, after the wage is agreed.
  - **Ignore.**
  - **Remove:** taken out if the club includes it.

  The clause list fills in automatically from the game's *Add Clause* list. Some clauses (like *Yearly Salary Raise*) only unlock on longer contracts, so the agent sets the length first.

## 4. Negotiating

When a job offer arrives, open it and press **Negotiate for me** in the panel. On the offer message itself, the button says **Open & negotiate for me**. The agent:

1. **Checks the club's maximum salary** before anything else: it clicks the salary box and reads the highest amount in the list that opens (the list ends at the club's maximum), then closes it without changing the offer.
   - If even your minimum is above it, it stops and asks you. **Go for the maximum with this club** applies to this club only; your saved range doesn't change.
   - If only your target is above it, it offers **Aim for the club's maximum**.
2. **Opens at your target** (or the club's maximum, if lower) with your preferred contract length, and presses **Suggest Terms**.
3. **Works the counter-offers:** it meets the club roughly halfway each round, never goes below your minimum, and settles when the gap is small.
4. **Adds your clauses one at a time.** It keeps the ones the club accepts and drops any that would cut your wage by more than 5%.
5. **Stops at "Deal ready"** and shows you the terms: press **Sign it**, or **I'll finish it myself**.
   - If the club won't reach your minimum (or your minimum length), the agent hands back to you instead of signing.

Options in the card:

- **Auto-start on offers:** the agent starts by itself when an offer appears.
- **Sign automatically:** signs when the deal meets your terms, without asking.

Both are off by default.

You can take over at any moment: click anything in the offer, press **Take back control**, or press `Ctrl+Shift+X`.

## The Agent tab

- **Click** the tab to open or close the agent panel.
- **Drag** it anywhere. It remembers the spot on every screen and at any resolution. Drop it on FM's top bar (left of *Instant Result*, for example) and it snaps to the bar's centre, so it lines up with the game's buttons.
- The panel opens under the tab, or above it if the tab is near the bottom of the screen.
- **Reset tab position** at the bottom of the panel puts it back in the default corner. Set `LockTabPosition = true` in the cfg to stop accidental drags.

## Hotkeys

| Keys | Action |
|---|---|
| `Ctrl+Shift+A` | show / hide the Agent tab and panel |
| `Ctrl+Shift+H` | negotiate the contract offer on screen |
| `Ctrl+Shift+X` | take control back |
| `Ctrl+Shift+U` | dump the UI tree to a file (troubleshooting) |

## Settings worth knowing (`com.manageragent.fm.cfg`)

Your wage range, clauses and job filters are set in the panel and saved in `BepInEx\config\ManagerAgent\agent.json`. The cfg file holds the rest:

- **ClickStrategies** (`submit,invoke,mouse`): how buttons are pressed, tried in order until the game reacts:
  - `submit` is the game's own "confirm" input;
  - `invoke` calls FM's click handler;
  - `mouse` really moves the cursor and clicks, and only fires while the FM window is focused.
- **[Game text]:** the words the agent looks for on screen (*Available Jobs*, *Apply For Job*, *Salary*, *Suggest Terms*, *Finalize Deal*…). Change these only if your game isn't in English.

## Troubleshooting

- **Setup can't download BepInEx:** click the link Setup shows, download the file whose name starts with `BepInEx-Unity.IL2CPP-win-x64`, put it next to `ManagerAgent-Setup.exe`, and click **Try again**.
- **Setup asks for administrator permission:** this happens when the game is in a protected folder. Click Yes.
- **The agent doesn't apply, or can't set the salary/length:** with that screen open, press **Dump UI** in the panel, then **Open dumps folder** to find the file (it's in `BepInEx\config\ManagerAgent\dumps` inside the game folder; the log also shows the full path). Set `VerboseLog = true` in the cfg, try again, and send the dump plus `BepInEx\LogOutput.log`.
- **It presses things too slowly or not at all:** the log says which press method worked. Put that one first in `ClickStrategies`.

## FM27 (out November 10)

Run Setup and choose *Football Manager 27*. It installs and checks the same way, and repairs automatically if needed. To bring your preferences across, copy `BepInEx\config\ManagerAgent` from the FM26 folder to the FM27 one.

## Files

| Path | What |
|---|---|
| `BepInEx\config\com.manageragent.fm.cfg` | settings, hotkeys and game texts |
| `BepInEx\config\ManagerAgent\agent.json` | your wage range, clauses, job filters and applications |
| `BepInEx\config\ManagerAgent\dumps\` | UI dumps |

## How it's put together (for adding duties)

```
src/Core/       Agent (runs every frame), settings, saved preferences, logging, JSON, self-test
src/Platform/   Win32: game window, hotkeys, real mouse/keyboard input
src/GameUi/     snapshots of FM's UI (ScreenModel), the real-game adapter, pressing elements
src/Logic/      pure logic: money/years parsing, jobs table + filters, contract screen reader,
                the negotiation strategy (NegotiationPlanner)
src/Duties/     JobHunter and Negotiator — step-by-step scripts over IGame
src/Panel/      the Agent tab and panel
```

Duties only see the game through `IGame` (snapshots plus press, type and scroll). So `tests/AgentTests` can run them end-to-end against **FakeFm**, a simulated FM laid out like the real jobs, offer and contract screens, with a club that negotiates. It also runs the negotiation strategy against 3,000 random clubs.

## For developers

| Folder | What |
|---|---|
| `src/` | the plugin |
| `installer/` | `ManagerAgent-Setup.exe` (.NET Framework 4.7.2 WinForms, built into Windows 10/11). It embeds the prebuilt plugin and this source, which **Repair** compiles against the player's own game files. |
| `prebuild/` | stand-ins for the BepInEx, Il2CppInterop and Unity assemblies, so the plugin can be compiled without the game (see its README) |
| `tests/` | installer tests; `tests/AgentTests` = duties against a simulated FM |

- **Build a release on Linux** (needs the .NET 8 SDK, Mono reference assemblies and zip): `./build-all.sh`. The output goes to `dist/`.
- **Build just the plugin against an installed game on Windows:** `.\build.ps1 -GameDir "...\Football Manager 26"`. It copies the plugin into `BepInEx\plugins\ManagerAgent`.
- **Self-test:** at startup the plugin JIT-compiles all of its methods against the game's real assemblies, logs the result, and writes `BepInEx\config\ManagerAgent\selftest.txt`. Setup reads that file to decide whether **Repair** is needed.

## License

MIT — see [LICENSE](LICENSE). Manager Agent is a fan-made mod and isn't affiliated with Sports Interactive or SEGA. It doesn't include or redistribute any game files or BepInEx; the installer downloads BepInEx from its official sources.
