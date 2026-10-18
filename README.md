# TreeLODGen


> Checked in game with vanilla trees, Nordskog, Happy Little Trees, Ulvenwald and Wigfrid's Tree Replacer, with and without ENB and with PGPatcher. Not tested yet: Vortex, mods installed straight into Data. Please report problems with your tree mod's name and the `TreeLODGen_report.csv` from the output folder.

Generates 3D tree LOD models from the trees of your own load order, in the format DynDOLOD
defined for them.

Tree mods normally need their author to ship hand-made LOD files for DynDOLOD's ultra tree
LOD. Without them, distant trees fall back to flat billboards. TreeLODGen reads the trees
your game will actually show (loose files of your mods, or the vanilla archives) and builds
those LOD models itself, so any tree mod gets 3D LOD, including mods that ship none.

Run it once **before your LOD generation**: TexGen and DynDOLOD, or another generator that
reads the same 3D tree LOD files (see "Other LOD generators"). If you use PGPatcher, run it
**after** PGPatcher.

## What it makes

For each tree, three models of decreasing weight, one per LOD level (4, 8, 16):

- the crown keeps a share of its leaf cards, spread evenly and enlarged to keep the tree's
  density and outline;
- the trunk and branches stay real 3D geometry, reduced to a triangle budget.

Typical pine: 1740 + 400 triangles at LOD 4, 644 + 150 at LOD 8, 262 + 60 at LOD 16.

The output is a normal mod folder:

- `meshes\DynDOLOD\lod\trees\` : the LOD models, named with the CRC32 of the full
  model as DynDOLOD expects, the one from before PGPatcher if it patched the tree (`tree_XXXXXXXXpassthru_lod.nif`, `..._lod_1.nif`, `..._lod_2.nif`);
- `DynDOLOD\DynDOLOD_SSE_skyrimesm_[high|medium|low].ini` : only when some trees need a rule of
  their own (see "Choosing trees"). Every other tree follows DynDOLOD's own "tree" rule, so the
  LOD levels (3D at LOD 4 only, or 3D at LOD 8 and 16 too) are set in DynDOLOD as usual;
- `TreeLODGen_report.csv` : what was done for every tree record, and why.
- `meshes\DynDOLOD\lod\trees\TreeLODGen_models.txt` : the list of the models above, so that
  the next run knows they are its own and replaces them.

Trees that already have a 3D LOD in your load order (shipped by their mod) are left alone
unless you pass `--all`. Bushes, ferns and ground cover are skipped with the same size and
path test TexGen uses.

## Requirements

- Skyrim Special Edition / Anniversary Edition.
- DynDOLOD 3 with DynDOLOD Resources SE, used with ultra tree LOD.

## How to use

**Mod Organizer 2**

1. Add `TreeLODGen.exe` as an executable in MO2 and run it from there. Its window opens with
   the game's Data folder and the output folder already filled in.
2. Click **Generate**. It writes a mod named `TreeLODGen Output` straight into your MO2 mods
   folder, showing each LOD model as a wireframe while it works. Refresh MO2 (F5) and enable
   that mod.
   **Cancel** stops after the current tree and leaves the output folder as it was.
   The folders and options are remembered for the next time, separately for each Mod
   Organizer 2 instance (in `%APPDATA%\TreeLODGen\settings.json`).
3. Run TexGen, then DynDOLOD in advanced mode with **Ultra** ticked, clicking a preset
   (Low / Medium / High) so that the rules are loaded.

You can also run it without starting it through MO2:

```
TreeLODGen.exe --mo2 "D:\MO2\MyInstance" --profile "Default" --output "D:\MO2\MyInstance\mods\TreeLODGen Output"
```

**Vortex or mods installed in Data**

Run `TreeLODGen.exe` after deploying. It finds the game through the registry; otherwise
pick the Data folder in the window. The output folder is created next to the tool unless
you choose another one.

Run it again, and regenerate TexGen and DynDOLOD output, whenever you add, remove or update a
mod that changes tree meshes: the LOD models are tied to the exact full model files.

## Command line

Started with any option, the tool works in a console instead of opening its window, for
scripts or batch use.

```
--output <folder>     where to write the mod (default: "TreeLODGen Output" in the mods folder of the
                      Mod Organizer 2 instance in use, otherwise next to the tool)
--data <folder>       the game's Data folder (default: found in the registry)
--mo2 <folder>        read a Mod Organizer 2 instance directly; combine with --profile <name>
--plugins <file>      plugins.txt to use with --data
--all                 also generate for trees that already have a 3D LOD in the load order
--levels <list>       per LOD level: leaf cards kept : leaf area restored : bark triangles
                      : leaf triangles a crown is never pruned below
                      default 0.4:0.8:400:2500,0.15:0.7:150:1000,0.06:0.6:60:400
--tree-choices <file> JSON object of model path -> "lod4", "all" or "billboard", per tree
--no-rules            write no DynDOLOD rule at all, not even for the tree choices
--dyndolod <folder>   DynDOLOD folder, see "Trees made of animated shapes only"
--no-pause            start at once and do not wait for a key at the end
```

Everything printed is also written to `TreeLODGen.log` next to the exe.

## Good to know

- **Weight.** DynDOLOD's tree rule decides where the 3D models are used. Its high preset uses
  them at LOD 4 and billboards beyond. Using them at LOD 8 and 16 too costs a lot: those levels
  cover most of the map, and in tests the object LOD meshes of Tamriel went from about 5 GB to
  over 8 GB with a dense tree mod and grass LOD, and the game no longer got past its loading
  screen. Use it only for light tree mods, and check that the game still loads.
- **Far levels.** To stay light, the LOD 8 and LOD 16 crowns keep fewer leaf cards. The kept
  cards are enlarged to keep the crown's coverage, up to twice their size, so far crowns stay
  as airy as the real tree instead of turning into a few big solid cards.
- **Choosing trees.** After a generation, **Choose trees...** lists the generated trees with how many
  copies the last DynDOLOD run placed and the size of their LOD 4 model. Each can use its 3D model at
  LOD 4, 3D everywhere, billboards only, or follow DynDOLOD's tree rule (the default); "Vanilla trees
  only" leaves the base game's trees and the trees that replace them on DynDOLOD's rule and puts
  the others on billboards. Saving rewrites the DynDOLOD rules only: run DynDOLOD again, there
  is no need to generate or run TexGen again. The choices are remembered. On the command line, pass
  `--tree-choices <file>`, a JSON object of model path to `"lod4"`, `"all"` or `"billboard"`.
- **Sparse crowns.** A crown made of few large cards is not pruned below a number of
  triangles per level (the fourth value of `--levels`), so such trees keep their full crown
  at LOD 4.
- **Do not reuse one model at every level.** The rules written here only cover trees that
  have all three models. Pointing DynDOLOD's general tree rule at LOD 8 and 16 for trees with
  a single model makes the output several times larger and can stop the game from loading.
- **Texture sharpness.** Crowns keep their full texture, limited by DynDOLOD's "Max tile
  size full". Its default (256 for 1080p/1440p) gives crowns about the same texel density as
  TexGen's billboards at the matching "units per pixel".
- **Trees made of animated shapes only.** Some mods ship trees with no static copy of the
  geometry. TexGen cannot render a billboard for those. Without help they keep
  their 3D models at every level, which is heavy. Pass `--dyndolod "<DynDOLOD folder>"` (the
  DynDOLOD field in the window): the tool then puts static stand-ins in
  `Edit Scripts\DynDOLOD\Render\Skyrim\Replace` and a billboard list in the output so that
  TexGen can make them. TexGen only reads such stand-ins from its own folder, which is why
  the tool needs to know where DynDOLOD is. The window fills the field in by itself when
  TexGen or DynDOLOD is one of your Mod Organizer 2 executables; clear it to skip this. The
  stand-ins of the previous run are removed at each run.
- **Bark that repeats along the trunk.** Such bark cannot sit on DynDOLOD's texture atlas (it showed as dark and grey bands on Ulvenwald), so its shape is named `noatlas` and uses its own texture. That costs one more draw call per bark texture and LOD block.
- **Trees without foliage** (dead trunks) get a LOD made of their reduced bark alone.
- **PGPatcher.** PGPatcher rewrites tree meshes, which changes their CRC32. DynDOLOD still looks
  LOD models up by the CRC32 from before patching (recorded in `ParallaxGen_Diff.json`), so the
  tool reads that file and names the models the same way. AnvilLOD looks them up by the CRC32
  of the mesh as installed, so patched trees also get a copy of their models under that name;
  each generator ignores the other's file. Run TreeLODGen after PGPatcher, and again whenever
  PGPatcher's output changes. Before 1.0.3, most patched trees silently fell back to billboards
  in DynDOLOD, and in 1.0.3 in AnvilLOD.
- **Scaled models.** Many tree mods (Ulvenwald especially) scale or move the tree on a node of
  the NIF. The game applies that on every node except the root, whose transform it replaces
  with the placed reference's; the LOD models follow the same rule, with the result written
  straight into their vertices, so distant trees match the size of the full ones.

## How it works

1. Reads the TREE records of the active plugins and resolves each winning model through
   loose files and BSA archives.
2. Separates bark from foliage, by shape when the tree is built that way, otherwise by
   connected piece: bark only samples opaque parts of the texture, leaf cards always cover
   cut-out texels.
3. Crown: keeps a share of the leaf cards, chosen evenly through the volume, and enlarges
   them inside the tree's original outline.
4. Bark: quadric error edge collapse down to the triangle budget, keeping UVs.
5. Writes plain static NIFs (old `NiTriShape` geometry of vanilla trees is rebuilt as
   `BSTriShape`).

## Other LOD generators

The models follow DynDOLOD's conventions (folder, `passthru_lod` names with the full model's
CRC32, `Crown` and `Trunk` shape names). The tool was developed and tested with DynDOLOD 3.

[AnvilLOD](https://www.nexusmods.com/skyrimspecialedition/mods/194477) reads the same files.
Tried with AnvilLOD 0.2.56 and its "3D tree LOD" option on Tamriel, with PGPatcher: 43,148 of
46,706 trees got a 3D model (DynDOLOD: 42,755 on the same load order), without errors. What to
know:

- It uses the 3D model at LOD 4 and billboard cards farther out, so the lighter LOD 8 and
  LOD 16 models and the rule files written here are not used by it.
- It can draw the billboards TexGen did not make itself ("Draw missing tree billboards");
  TexGen is still recommended.
- On that load order its LOD 4 blocks came to about 6.5 GB, close to DynDOLOD's.

## Licence

Source code: https://github.com/Cl3mus33/TreeLODGen

Copyright (C) 2026 Clemus.

TreeLODGen is free software: you can redistribute it and/or modify it under the terms of the
GNU General Public License version 3 as published by the Free Software Foundation. It is
distributed WITHOUT ANY WARRANTY; see the `LICENSE` file for the full text. The source code
is provided alongside the release.

## Credits

- [nifly](https://github.com/ousnius/nifly) by ousnius, through niflysharp, for reading and
  writing NIF files (GPL-3.0).
- [BCnEncoder.NET](https://github.com/Nominom/BCnEncoder.NET) for decoding DDS textures.
- [K4os.Compression.LZ4](https://github.com/MiloszKrajewski/K4os.Compression.LZ4) for BSA archives.
- DynDOLOD, TexGen and LODGen are by Sheson; this tool only produces input files for them.
- Mechanicalpanda and Tomato for their reports and in-game tests.

The generated LOD models are derived from the meshes of the mods in your load order. They
are for your own use; ask the mod's author before sharing them.
