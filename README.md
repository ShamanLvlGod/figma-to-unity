# Figma to Unity

Build Unity UI from Figma with one click. A Figma plugin sends your frames straight into the open Unity Editor, and Unity builds real uGUI prefabs: TextMesh Pro text, buttons that switch between their Figma variants, toggles, sliders, input fields, scroll views, anchors from your constraints, and screens that fit any resolution.

- **No Figma API.** No access token, no paid seat, no rate limits. The plugin talks to Unity on your own computer.
- **Re-import safe.** Prefab GUIDs and internal ids are derived from Figma node ids, so an unchanged design writes byte-identical files and links into your screens survive every export. Scripts and objects you add in Unity are carried over.
- **Checks before export.** The plugin lists problems (missing fonts, default layer names, mismatched variants and more) and jumps to the layer on click.
- **Uses what your project already has.** Matching fonts and images in your project are used instead of importing copies.

Based on [UnityFigmaBridge](https://github.com/simonoliver/UnityFigmaBridge) by Simon Oliver (MIT, see `LICENSE`).

## Requirements

- Unity 6.3 or newer, uGUI (com.unity.ugui 2.0 with TextMesh Pro built in)
- The Figma desktop app (plugins under development don't run in the browser)
- Tested on macOS. Windows should work but has not been tested yet

## Installing

**In Unity:** open **Window > Package Manager**, press **+**, choose **Install package from git URL...** and enter:

```
https://github.com/ShamanLvlGod/figma-to-unity.git
```

Or clone this repository into your project's `Packages` folder.

**In Figma**, once per computer:

1. In Unity open **Tools > Figma Bridge > Set Up Figma Plugin**. It puts the plugin in your project's `UserSettings/FigmaToUnityPlugin` folder, shows it in Finder or Explorer and copies the path. Unity keeps that copy up to date when the package updates.
2. In the Figma desktop app open a design file, then use the Figma menu (the logo, top left) or right-click the canvas: **Plugins > Development > Import plugin from manifest...** and pick that `manifest.json`.
3. Run **Plugins > Development > Export to Unity**. It asks for a pairing code.
4. In Unity open **Tools > Figma Bridge > Pair Figma Plugin** and type the code into the plugin.

## Exporting

1. Keep Unity open and out of play mode. Any scene is fine.
2. In Figma run **Export to Unity** and pick the page.
3. To export only some screens, select those top-level frames first. Nothing else in Unity is touched.
4. Fix everything under **Errors** in the Checks list. Click a row to jump to the layer. Warnings can be ignored per layer.
5. Press **Export**. If a screen was renamed or deleted in Figma, the plugin asks before removing it from Unity.
6. The result list shows what was built. Click a row to jump to the layer.

Without Unity: **Save export file** writes a `.figmabundle.json`. A developer imports it with **Tools > Figma Bridge > Import Export File...**.

## Preparing your Figma file

1. **One page per set of screens.** Each screen is one top-level frame at 3840x2160. Drafts go on another page.
2. **Real layer names** (`Title`, `Photo`, `PlayButton`), never "Rectangle 12". Names must be unique among siblings.
3. **Anything clickable is a component.** Name variants `State=Default`, `State=Hover`, `State=Pressed`, `State=Disabled`, plus custom ones like `State=Active`. Layer names must match across variants; only visibility, size and colour change.
4. **Toggles** use a `Checked=On` / `Checked=Off` axis (or `Value`, `IsOn`, `Toggle`).
5. **Set constraints on every layer.** They become the anchoring in Unity. Scale works too.
6. **Live text in Google Fonts**, or a font your Unity project already has. The Checks list flags anything else.
7. **No prototype links inside components.** Links on screens are fine and become the flow map.
8. **Effects Unity can't draw** (blend modes, blurs, inner shadows, multiple fills, odd shapes) are turned into one picture automatically and listed under Info. Keep layers that blend together inside one frame. Text inside a picture can't be changed in Unity.
9. **Never detach instances.** Shared components live on their own page.

## Layer panel and name tags

Select a layer while the plugin is open to set what it becomes. Settings are saved in the Figma file. Name tags in square brackets do the same thing and are removed from the name in Unity.

| Role | Name tag | In Unity |
|---|---|---|
| Auto | | Decided from the layer (default) |
| Button | `[button]` | Button. With variants it switches state visuals |
| Toggle | `[toggle]` | Toggle. With variants it shows the On or Off visuals |
| Toggle group | `[togglegroup]` | Only one toggle inside can be on |
| Slider | `[slider]` | Slider. Children with Fill and Handle (or Knob, Thumb) in their names are used |
| Input field | `[input]` | TMP input field. A text layer with Placeholder or Hint in its name is the placeholder, another text layer is the typed text. "password" in the name hides input |
| Scroll view | `[scroll]` | Scroll view. Figma overflow scrolling does the same |
| Picture | `[image]` or `[flatten]` | Rendered by Figma as one image |
| Ignore | `[ignore]` | Not exported |

Other fields:
- **Binding name:** the name code uses to find this layer. Defaults to the layer name.
- **Localization key:** text layers only. Fills the localization table.
- **9-slice:** left, top, right, bottom insets in pixels. The layer becomes a sliced image that stretches cleanly.
- **Auto-size text:** shrinks text to fit its box.

The word "render" in a layer name also turns it into a picture.

## For developers

### Output

Everything goes to `Assets/Figma/` (change it with **Output Folder** in the settings asset):

| Folder or file | What |
|---|---|
| `Screens/` | One prefab per top-level frame |
| `Components/` | One prefab per component, plus one per component set with all its states |
| `Pages/` | One prefab per page |
| `ImageFills/`, `ServerRenderedImages/` | Image fills and Figma-rendered pictures, reused by content hash |
| `Fonts/`, `FontMaterialPresets/` | TMP font assets and outline or shadow presets |
| `Localization/strings.csv` | `key,text,screen` for every text layer with a localization key. Keys from earlier imports are kept |
| `FlowMap.asset` | Prototype links between screens (`FigmaFlowMap.LinksFrom(screen)`) |
| `DesignTokens.asset` | Figma variables and text styles (`FigmaDesignTokens.GetColor(name, mode)`) |
| `.figmabridge-registry.json` | Which page owns which prefab, plus content hashes. Commit it together with the output |

Prefab GUIDs stay the same across imports, so scene and prefab references survive.

### Putting a screen into the game

- Place the screen prefab itself (nest it or `Instantiate` it); never copy it. Its GUID survives every export.
- Every screen root has a `FigmaScreenFitter`, so a screen fills whatever canvas or panel it is put under, at any resolution or aspect ratio. A 3840x2160 design under a 1920x1080 canvas comes out at exactly the right size; extra width or height (21:9, 4:3, 16:10) goes where the Figma constraints say.
- Frames smaller than a screen (dialogs, panels, HUD pieces) only get the right scale, so they keep their Figma size relative to the screen and you place them yourself.
- Fit mode and design resolution are in the settings asset (**Fit Screens To Parent**, **Screen Fit Mode**, **Design Resolution**). Expand behaves like a CanvasScaler set to Expand.
- To inspect an import, open the prefab (Prefab Mode shows the design at its native size) or drop it into any scene with a canvas.
- Every object in a generated prefab gets an internal id derived from its Figma node id, so re-exporting an unchanged design writes byte-identical files (no noise in git), and links from scenes or other prefabs into a screen (including a button inside a component, several levels deep) and overrides on them survive every export. Renaming or moving a layer in Figma keeps its id; deleting it in Figma removes it.
- Give game logic its own script on the screen prefab (carried over on re-import), reference objects directly, or use `FigmaScreenRefs` lookups.

### Fonts and images your project already has

Imports use your project's own assets instead of bringing in copies (setting **Reuse Project Assets**, on by default):
- **Fonts:** a TMP font anywhere outside `Assets/Figma` with the same family and weight is used, as long as it has every character the Figma text needs. Otherwise the font comes from Google Fonts as before. The report says which one was picked. If two project fonts match, or you want a different font, add a **Font Override** in the settings asset (family, weight or 0 for all weights, font).
- **Images:** an incoming image fill that is pixel-identical to a project sprite (Single sprite, not in a sprite atlas) is not imported; the UI points at the project sprite and the plugin stops sending it. Flattened pictures are always imported.
- If a reused project asset is deleted later, the next import brings the Figma copy back with a warning.
- **Already imported UI:** **Tools > Figma Bridge > Use Project Fonts and Images** finds Figma copies that match project assets, re-points every generated prefab at the originals (rebuilding text effect materials for the new font) and offers to remove the copies nothing uses any more.

### Editing generated prefabs

Figma owns the visuals: every import rebuilds layout, images, text and the importer's own components. What you add in Unity is carried over:
- **Kept:** your own scripts and any component the importer doesn't create (audio, animators, your MonoBehaviours with their field values), and objects you add under a Figma layer. References between them are re-pointed to the rebuilt objects.
- **Not kept:** edits to anything Figma owns, such as moving a layer, changing an Image colour or editing text. Change those in Figma.
- **Lost:** additions on a layer that was deleted in Figma.

Before overwriting a hand-edited prefab the importer copies it to `Library/FigmaBridge/Backups/<time>/` and warns in the report.

Wrapping a generated screen in your own prefab, or adding scripts at runtime, avoids the question entirely.

### Finding objects from code

Every screen root has a `FigmaScreenRefs` component listing its layers by binding name:

```csharp
var refs = screen.GetComponent<FigmaScreenRefs>();
refs.Get<Button>("PlayButton").onClick.AddListener(StartGame);
refs.Get<TMP_Text>("Title").text = playerName;
```

**Tools > Figma Bridge > Generate Screen Keys Script** writes `Assets/Figma/Generated/FigmaScreenKeys.cs` with a constant per key, so a renamed layer breaks the build instead of failing at runtime.

### Binding scripts by name

- A MonoBehaviour from this project whose class name matches a screen or component name is added to its root on import. Set **Screen Binding Namespace** in the settings asset to limit the search to one namespace.
- Serialized fields on that script are filled with matching child objects by name, only while they are empty.
- `[BindFigmaButtonPress("PlayButton")]` on a method adds it as an onClick listener of that button.
- Unity's own types (Image, Button, Text) never match, so a layer called "Image" is safe.

### Import hooks

```csharp
FigmaBridgeImportEvents.ImportStarted += source => { };
FigmaBridgeImportEvents.PrefabBuilt += (prefab, assetPath, isScreen) => { };
FigmaBridgeImportEvents.ImportCompleted += report => { if (report.HasErrors) { } };
```

### State components

- `FigmaStatefulButton`: a Button that shows the child named after the current state (Normal, Highlighted, Pressed, Selected, Disabled, or a custom state). Set `BaseState` to switch a custom state such as `Active`.
- `FigmaStatefulToggle`: a Toggle that shows `On...` or `Off...` visuals.
- `FigmaStateSwitcher`: for sets that aren't interactive. Set `State` to switch.

### Menus

| Menu | What |
|---|---|
| Tools > Figma Bridge > Set Up Figma Plugin | Puts the Figma plugin in a stable folder and shows it |
| Tools > Figma Bridge > Pair Figma Plugin | Shows the pairing code |
| Tools > Figma Bridge > Reset Pairing Code | New code; every plugin has to pair again |
| Tools > Figma Bridge > Re-import Last Export | Rebuilds from the last export without Figma |
| Tools > Figma Bridge > Import Export File... | Imports a `.figmabundle.json` |
| Tools > Figma Bridge > Import Report | Shows the last import's errors, warnings and info. Opens on its own when there are errors |
| Tools > Figma Bridge > Plugin Receiver > Enable / Disable | Turns the local listener on or off |
| Tools > Figma Bridge > Clean Up Unused Images | Moves imported images nothing uses to the trash, after a confirmation |
| Tools > Figma Bridge > Optimise Imported Images | Re-applies texture settings and pads images to a multiple of 4 so they compress |
| Tools > Figma Bridge > Use Project Fonts and Images | Points imported UI at fonts and images the project already has, see above |
| Tools > Figma Bridge > Build Sprite Atlases | One atlas per screen plus a shared one, in `Assets/Figma/Atlases`, for flattened pictures. Image fills drawn by `FigmaImage` and images over 2048 px stay separate |
| Tools > Figma Bridge > Generate Screen Keys Script | See above |

### Troubleshooting

| Problem | Fix |
|---|---|
| Can't reach Unity | Open your project in Unity. If it is open, check Tools > Figma Bridge > Plugin Receiver > Enable |
| Pairing code needed | Tools > Figma Bridge > Pair Figma Plugin and type the code |
| Update the plugin | Update the package in Unity, then close and reopen the plugin |
| Unity needs a newer Figma Bridge | Update the package in Unity |
| Plugin missing (Figma menu) | Run Set Up Figma Plugin again and import the manifest it shows |
| Unity is busy | Wait for the import or compile to finish; the plugin retries |

- The listener only accepts the Figma plugin on localhost with the pairing code, and never starts in batch mode or CI. It uses port 37264 by default. With several Unity projects open at once, give each its own **Receiver Port** (37264, 37265 or 37266) in the settings asset; the plugin finds all of them and lets you pick the project.
- Texture settings: max size is the next power of two up to 8192, no mipmaps, compressed when the size allows it, sRGB.
- Auto layout wrap isn't supported yet; wrapped rows come in as one line with a warning.
- The protocol between the plugin and Unity is in `Documentation~/Protocol.md`.

### REST import (legacy)

The original import through the Figma REST API still works through the settings asset and a personal access token (**Tools > Figma Bridge > Sync Document**). Figma's rate limits on free seats can lock it out for days, so use the plugin.

## Credits

- [UnityFigmaBridge](https://github.com/simonoliver/UnityFigmaBridge) by Simon Oliver
- [Inigo Quilez' 2D SDF functions](https://iquilezles.org/articles/distfunctions2d/)
- [krzys-h UnityWebRequestAwaiter](https://gist.github.com/krzys-h/9062552e33dd7bd7fe4a6c12db109a1a)
- [Jonathan Neal's google-fonts-complete](https://github.com/jonathantneal/google-fonts-complete)
