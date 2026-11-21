# contact-sheets
Reviewing motion with contact sheets: choosing a time range, frame count and crop, comparing before and after, and GIFs for the user.

A contact sheet lays frames side by side in one image so timing, spacing and arcs can be judged at a
glance, as animation and VFX reviews do. Each tile is labeled with its frame number and game time.

capture returns one: 12 frames across the run by default, or what its `sheet` option asks for. When
you already know the moment to check, pass it there and save a call. contact_sheet makes another sheet
from a capture's saved frames without re-running, for zooming in after seeing the first one. Both
pick `count` saved frames evenly between `from` and `to` (game seconds). Use a short range around the moment of interest (an impact, a launch) with 8 to 16 frames
rather than the whole capture. `crop` ('x,y,width,height' in frame pixels) zooms into an effect; crops
smaller than a tile are enlarged by a whole factor so pixels stay crisp.

## Before and after

To check a change, capture a baseline with a `name` before it (`name: "before"`), make the change, and
capture again with `sheet: { baseline: "before" }`; contact_sheet takes `baseline` too. Each row shows the
same frame from both (A is the baseline, B the new capture) and a third tile with every changed pixel in
red, brighter for larger changes, plus the percentage changed. The same build and arguments draw the same
frames, so any red is a real difference: a faint opacity change still shows. `diff: false` drops the third
tile for larger A and B. The HUD (captured with `hud: true`) is left out of the comparison and shows in
blue; anything else the game draws from wall-clock time or memory (its own FPS counter, say) differs in
every frame, so crop it out. Both captures need the same fps and size; compare 6 frames by default, at
most 12. Compare numbers by reading both summary.json files.

Only saved frames can be used: capture with save `all` (default) or `every:N` first. Sheets are saved in
the capture's `sheets/` folder and stay at most 1568 px wide, the size model clients keep without
downscaling.

## GIFs for the user

You can't watch an animation, but the user can. When they should judge motion (timing, feel, an A/B
choice between two versions), make a GIF with the gif tool and give them its path: it plays on a
double-click in Windows, in browsers and in chat apps. It takes the same `from`, `to`, `crop` and
`baseline` as contact_sheet (baseline plays A beside B; `diff: true` adds the changed pixels as a third
panel, off by default since people judge motion best from A and B), plus `fps` (frames of game time per second, at
most 50; 30 by default, and one that divides the capture's fps keeps spacing even), `speed` (below 1 is
slow motion, useful for fast effects) and `width` per frame (640 by default). GIFs are saved in the
capture's `gifs/` folder. Their 256 colors are dithered with a fixed pattern, so smooth gradients look
slightly grainy: judge fine color in the PNG frames or contact sheets.
