# hud - the Panorama weapon menu

Everything needed to change how the weapon grid looks, without touching the plugin.

```
panorama/layout/custom_game/weapon_hud.xml    the grid
panorama/styles/custom_game/weapon_hud.css    its look
panorama/styles/custom_game/hud_shared.css    the card, header, footer, reveal - shared chrome
previews/weapon_hud.preview.html              open in a browser
lib/panoramamanager.json                      engine signatures; goes in counterstrikesharp/gamedata/
```

Tooling and the Panorama reference are **not** duplicated here - they live in
[`../skills/cs2-panorama-hud`](../skills/cs2-panorama-hud):

```
../skills/cs2-panorama-hud/scripts/validate.py     gate a build
../skills/cs2-panorama-hud/scripts/preview.py      render to HTML
../skills/cs2-panorama-hud/scripts/new_layout.py   scaffold a new layout
../skills/cs2-panorama-hud/scripts/build-hud.cmd   compile without Workshop Tools
../skills/cs2-panorama-hud/references/             the CSS vocabulary and a 19-layout kit
```

## Editing it

Panorama is not the web. Before you change anything, know these:

- **No `display`, no flex, no grid, no `float`, no `calc()`, no `var()`, no `rgb()`, no `@media`.**
  Layout is `flow-children` plus `width`/`height` taking `fit-children | fill-parent-flow(w)`.
- **`box-shadow` takes the colour FIRST** - `#colour x y blur spread`, not last.
- **`background-size: contains`**, not the web's `contain`. The wrong spelling silently falls back to
  `auto`, which means the image's original size, which overflows.
- **Transitions use the split form** - `transition-property` / `-duration` / `-timing-function`.
- **A child at `width: 100%` inside a parent with no width renders as nothing.** The parent sizes to
  its children, the child sizes to its parent, and the circle resolves to zero.
- **An unregistered property is dropped silently.** Nothing is reported. Check the vocabulary in `../skills/cs2-panorama-hud/references/`.

### Building without Workshop Tools

`../skills/cs2-panorama-hud/scripts/build-hud.ps1` calls `resourcecompiler.exe` directly - it ships with the game
in `game\bin\win64`, and Workshop Tools is a GUI over them.

```
..\skills\cs2-panorama-hud\scripts\build-hud.cmd
..\skills\cs2-panorama-hud\scripts\build-hud.cmd -Watch
```

Use the `.cmd`, not the `.ps1` directly. PowerShell's policy is `AllSigned` on many machines and
refuses unsigned scripts - local ones included, so `Unblock-File` does not help. The wrapper passes
`-ExecutionPolicy Bypass` for that one process and changes nothing system-wide.

Pass `-Cs2Root` if the game is not on `X:`. It writes **loose files**, not a VPK, so `gameinfo.gi`
needs a directory search path:

```
Game                csgo/overrides
Game                csgo
```

A directory is not a sealed archive, so a replaced file may be picked up without a full restart -
worth trying before you quit. The search path itself still only mounts at startup.

Then, every time:

```bash
python3 ../skills/cs2-panorama-hud/scripts/validate.py .     # XML parses, tags/attrs on the whitelist, CSS names and values real
python3 ../skills/cs2-panorama-hud/scripts/preview.py panorama/layout/custom_game/weapon_hud.xml 5
```

The validator exits non-zero, so it can gate a build. The preview is an approximation - flexbox
stands in for `flow-children`, `s2r://` images are placeholders, game fonts are substituted. Judge
spacing and hierarchy there; judge anything else in game.

## What the plugin drives

Ids are a contract. Rename one here and it stops being written to.

| Id | Driven by | Effect |
|---|---|---|
| `grp{r}` | `hidden` | empty category collapses out |
| `grp{r}_r{k}` | `hidden` | unused row of five collapses |
| `grp{r}_label` | `{s:}` | category name |
| `w{r}_{c}` | `hidden` | unused tile collapses |
| `w{r}_{c}` | `selected` | the toggle state |
| `w{r}_{c}` | `icon-*` | which weapon picture |
| `w{r}_{c}` | `{s:}` | weapon name |
| `wsel_save` / `wsel_close` | clicks | footer |

The grid is 10 categories x 10 options laid out 5 per line. **That size is baked in** - a layout
ships to every client in a VPK, so running out of columns later is a re-release, not an edit.

Icon classes live at the bottom of `weapon_hud.css`; `IconSlugs` in `Menus/WeaponHudMenu.cs` owns the
`CsItem` -> class mapping. Both sides have to agree.

## Building and shipping

Compile the two panorama files in CS2 Workshop Tools, pack the VPK, and mount it. The compiled paths
(`weapon_hud.vxml_c`, `weapon_hud.vcss_c`) are what the plugin asks for - note the `_c`.

Addon-supplied layouts are still refused by the client, so today this needs a local mount through
`gameinfo.gi`. That is a development harness, not a shipping method.

## Updating the library

The library comes from nuget.org:

```xml
<PackageReference Include="PanoramaManager" Version="0.1.3" />
```

Bump the version in `RetakesAllocator/RetakesAllocator.csproj` and rebuild. This folder ships as a
standalone fork, so a package is the only reference that survives a clone - there is no
PanoramaManager source tree beside it.

`lib/panoramamanager.json` goes in `counterstrikesharp/gamedata/`. It carries the engine signatures,
so a CS2 update that shifts them is a text edit on the server rather than a plugin rebuild. The
NuGet package ships the same file under `contentFiles/any/any/gamedata/`.

## Turning it off

`EnableHUDMenu: false` in the allocator config restores the SharpModMenu chat screen exactly as
before. The HUD is a different front end to the same preferences, not a different feature.
