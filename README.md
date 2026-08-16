# GltfBakeTool

Windows desktop tool (WPF, .NET 10) to clean up glTF/GLB node hierarchies and bake
several meshes + materials into **one mesh, one primitive, one atlased material**.

## Solution layout

| Project | Purpose |
|---|---|
| `GltfBakeTool.Core` | All logic, no UI (net10.0). Load/save, node cleanup, mesh join, atlas baking, structural glTF edits. |
| `GltfBakeTool` | WPF app (net10.0-windows): tree view, HelixToolkit (SharpDX) viewport, properties/texture panel (double-click a texture → full-resolution inspector with wheel zoom / drag pan / pixel readback), log, undo. |
| `GltfBakeTool.Cli` | Headless driver for testing/automation: `info`, `clean`, `join`, `prune`, `roundtrip`, `dump-images`. |

Libraries: **SharpGLTF** (parse/author), **HelixToolkit.Wpf.SharpDX 3.x** (viewer), **SkiaSharp** (image
decode/compose/encode – MIT; ImageSharp 3+/4 was rejected because of its commercial licence),
**CommunityToolkit.Mvvm**.

## How the pieces work

### Two editing layers
* **SharpGLTF `ModelRoot`** is used to *read* everything (nodes, meshes, materials, animations, skins)
  and to *author new content* (the joined mesh, the atlas material/textures).
* **Structural edits** – removing/reparenting nodes, pruning unused resources, rebuilding the binary
  buffer – are done on the raw glTF **JSON + BIN** (`Core/Structure/GlbPackage`, `GltfStructure`).
  SharpGLTF has no node-removal API and its `SceneBuilder` round trip is lossy (skinned mesh nodes
  are re-created unnamed at the root, unnamed empties vanish, accessors are re-split), so the DOM
  route keeps everything we don't touch byte-for-byte. Results are re-parsed through SharpGLTF with
  validation, so structural mistakes surface immediately.
* Undo = GLB snapshot in memory before every operation.

### Clean empty nodes (`Core/Operations/CleanEmptyNodes`)
A node is removed when it has **no** mesh/camera/skin/light, is **not** an animation target, skin
joint or skeleton root, has no `extras` (option), and either
* its local transform is identity, or
* its whole subtree is being removed (transform is then irrelevant), or
* *Fold transforms* is on and no surviving child animates its transform (the transform is composed
  into the children; falls back to a `matrix` when TRS decomposition is impossible).

Children are spliced into the parent at the removed node's position; every node reference (children,
scene roots, skin joints/skeleton, animation targets) is re-indexed. Scope: whole file, or only the
checked subtrees. Candidates are shown grey/italic in the tree beforehand.

### Join (`Core/Operations/JoinMeshes` + `Core/Atlas/*`)
1. Collect triangle primitives under the checked nodes (lines/points and morph-target primitives are
   skipped with a warning). Rigid meshes are baked into the join parent's local space (world matrix,
   inverse-transpose for normals, winding flipped for mirrored transforms). Skinned meshes must all
   share one skin; their geometry stays in bind space and JOINTS_0/WEIGHTS_0 are carried over.
2. Per source material: UVs get the `KHR_texture_transform` applied and every UV *island* (triangles
   connected through shared vertices) is shifted by whole tiles towards [0,1] (invisible with REPEAT
   wrapping, but it stops islands parked in neighbouring tiles from inflating the cell). The cell then
   covers exactly the used UV range (fractional, plus a texel margin) – unused texture area is cropped
   and real wrap-around is baked as repeats. Ranges above *max repeats* tiles are clamped (warning).
   Materials with identical texture content (same images, factors, wrap modes) share one cell.
3. `MaterialAtlasBaker`: one cell per material, same layout for every channel
   (BaseColor, MetallicRoughness, Normal, Occlusion, Emissive). Missing textures are filled from
   factors; factors are multiplied into pixels so the merged material uses factor 1. Cells get
   edge-extended padding (repeat/mirror/clamp according to the source sampler). Skyline packing into
   the smallest power-of-two atlas, with a modest downscale preferred over doubling the atlas.
   Channels that are texture-less and identical across materials become factors instead of images.
   Alpha mode = most permissive of the sources (warned), double-sided = any.
4. Author mesh (POSITION, NORMAL, TEXCOORD_0, optional COLOR_0/TANGENT/JOINTS/WEIGHTS) + material,
   place a new node under the common ancestor, clear the source meshes, remove the emptied nodes,
   prune orphaned meshes/materials/textures/images/accessors/bufferViews and rebuild the buffer.

### Join groups (`Core/Grouping/JoinGrouping`)
One merged material cannot express some properties per part, so primitives are classified by a
**compatibility key**: alpha class (blend vs opaque/mask, optionally mask vs opaque), unlit,
transmission/volume, clearcoat, other KHR material extensions, textures on TEXCOORD_1+, heavy UV
tiling, double-sided, and skin. The "Join groups" panel shows the resulting groups (colour badge,
counts, mixed nodes), lets you toggle which criteria count, tints the viewport by group and checks a
group's nodes on click. **Join per group** produces one mesh + atlas per group in a single undoable
pass (`<Name>_<group>`); **Join checked** still forces everything into one mesh (with the alpha policy).
Nodes whose primitives land in different groups keep a new mesh with the leftover primitives — no
geometry is duplicated. Extensions the merged material cannot carry are reported per material.

## Known limitations / ideas
* Textures on TEXCOORD_1+ cannot be atlased (dropped for that channel, warning).
* Heavily tiled materials (e.g. 48×48) get clamped; an alternative "keep tiling, reduce resolution"
  mode could be added.
* Spec-gloss (`KHR_materials_pbrSpecularGlossiness`) is approximated (diffuse as albedo, metallic 0).
* Normal-map handedness after mirroring: `TANGENT.w` is flipped when all sources carry tangents;
  otherwise tangents are dropped (clients derive them).
* Morph targets are not merged. Different skins cannot be joined into one mesh.
* Clearcoat / specular / sheen / transmission etc. are not baked (reported and, by default, split into their own group).

## CLI examples
```
GltfBakeTool.Cli info  model.glb
GltfBakeTool.Cli clean model.glb out.glb [--fold]
GltfBakeTool.Cli join  model.glb out.glb [--nodes 3,7] [--atlas 2048] [--jpeg] [--alpha auto|opaque|mask|blend] [--per-group [--tiling]]
GltfBakeTool.Cli groups model.glb [--tiling]
GltfBakeTool.Cli materials model.glb
GltfBakeTool.Cli dump-images out.glb ./images
```
