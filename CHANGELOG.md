# Change Log:

## 2.0.0

- Figma plugin "Export to Unity" pushes pages or selected frames into the open Editor, with pairing, a version check, preflight checks, a layer panel, deletion confirmation and an offline export file
- Import report window instead of dialogs; one broken layer is skipped instead of stopping the import
- Developer additions to generated prefabs are carried over on re-import, with a backup of the edited file
- Component sets become stateful buttons, toggles or state switchers from their variants
- Toggle, toggle group, slider, input field and scroll view roles; 9-slice; localization keys; screen refs and generated keys
- Strokes inside, centre and outside; letter spacing, line height, rich text, truncation and auto-size; clip content; Scale constraint; auto layout on by default
- Unsupported effects are rendered by Figma as one picture automatically
- Flow map and design tokens assets; import hooks for code
- Tools for unused image cleanup, image optimising, sprite atlases and screen keys
- Texture settings up to 8192 px, no mipmaps, compressed when possible
- Script binding only uses this project's scripts
- Internal ids derived from Figma node ids: repeat exports are byte-identical and outside references survive
- Screens fit any canvas or panel through FigmaScreenFitter; smaller frames keep their design scale
- Fonts and image fills that already exist in the project are used instead of imported copies, with font overrides in the settings and a tool to switch already imported UI over

## 1.0.8

Better support for Linear Color Space (@naoya-maeda). Please note that from this release
all imported textures will have sRGB set to true. The FigmaImage component
now supports sRGB textures in both gamma and linear color spaces/

## 1.0.7

- Enhancement - Added support for "Fit" Image mode (@laura-copop)
- Enhancement - Added support for flipped nodes (@laura-copop)
- Bug fix - JSON Deserialisation no longer throws errors for missing items (@laura-copop)
- Bug fix - Correctly uses project colorspace (@laura-copop)
- Bug fix - Non-visible fills no longer default to visible (@laura-copop)
- Bug fix - Server side images now batch where required to prevent Figma API errors

## 1.0.6

- Enhancement - Added page sync selection (thanks @SatoruUeda)
- Enhancement - Implemented correct alignment for auto-layout
- Enhancement - Use Server-side rendering for boolean shapes
- Bug Fix - Fix for crash import for deeply nested components
- Change - Auto layout components are disabled by default
- Bug fix - Masked objects no longer render the masks themselves

## 1.0.5

Small release adding a few new Figma features and bugs. Thanks @SatoruUeda for the contributions and for all the reports.

- Enhancement -Added support for "Fill" Image mode
- Enhancement -Added text auto sizing
- Enhancement -Added image fill opacity support
- Enhancement -Added layer opacity support
- Bug fix - Fixed issues with components being overwritten
- Bug fix - Numerous other small bugfixes