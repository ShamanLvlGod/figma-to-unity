# Figma Bridge protocol (version 2)

How the "Export to Unity" Figma plugin talks to the Unity Editor. The plugin lives in `FigmaPlugin~/`; the Unity side is `Editor/Plugin/FigmaPluginReceiver.cs`.

## Transport

- HTTP on `http://localhost:37264`, started by the Unity Editor (never in batchmode).
- Every request from the plugin carries:
  - `X-Figma-Bridge: 2` (protocol version)
  - `X-Figma-Bridge-Plugin-Version: <plugin version, e.g. 2.0.0>`
  - `X-Figma-Bridge-Token: <pairing code>` (every endpoint except `/ping`)
- Requests come from the plugin UI iframe, whose origin is `null`. Unity answers CORS preflights for that origin and sends `Access-Control-Allow-Private-Network: true`.
- Errors are non-2xx responses with a JSON body `{ "error": "message" }`. A missing or wrong pairing code returns 401 `{ "error": "...", "pairingRequired": true }`.

## Pairing

Unity creates a random pairing code per project and machine. The artist copies it once from the Unity menu **Tools > Figma Bridge > Pair Figma Plugin**, pastes it into the plugin, and the plugin keeps it in `figma.clientStorage` under `pairing:<project name>`.

## Endpoints

### `GET /ping`
Always answers 200.
```json
{
  "ok": true,
  "protocol": 2,
  "packageVersion": "2.0.0",
  "minPluginVersion": "2.0.0",
  "project": "MyGame",
  "importing": false,
  "paired": true,
  "designResolution": { "width": 3840, "height": 2160 },
  "fonts": [ { "family": "Roboto", "style": "Bold" } ],
  "googleFonts": true
}
```
`designResolution` and `fonts` are only included when `paired` is true. `fonts` lists the font families and styles Unity already has as TextMeshPro assets. When `googleFonts` is true, Unity also downloads missing Google Fonts by itself.

### `POST /document`
Body: the export document (below) as JSON. Unity parses it, works out what it needs, and answers:
```json
{
  "images": ["<image hash>"],
  "renders": ["<node id>"],
  "scale": 1,
  "plan": {
    "screens": [ { "name": "MainMenu", "status": "new" } ],
    "deletions": [ "Old Screen" ],
    "warnings": [ { "message": "...", "nodeId": "1:2", "nodeName": "Title" } ]
  }
}
```
- `images`: image fill hashes Unity doesn't have yet. Upload each with `/image`.
- `renders`: node ids Unity wants as flattened PNG images. Upload each with `/render`.
- `scale`: export scale for renders.
- `plan.screens[].status`: `new`, `changed` or `unchanged`, compared with the last import.
- `plan.deletions`: screens Unity will remove because they no longer exist on this page. Always empty for a partial (selection) export. The plugin must show these and ask before continuing.

### `POST /image?hash=<image hash>`
Body: the raw image bytes from `figma.getImageByHash(hash).getBytesAsync()`.

### `POST /render?id=<node id>`
Body: PNG bytes from `node.exportAsync({ format: 'PNG', constraint: { type: 'SCALE', value: scale }, colorProfile: 'SRGB' })`. Use the default bounds (not `useAbsoluteBounds`), so shadows and blurs are included; Unity positions the image with the node's `absoluteRenderBounds`.

### `POST /commit`
Empty body (send `Content-Length: 0`). Unity builds the UI and answers when done:
```json
{
  "ok": true,
  "screens": ["MainMenu"],
  "report": {
    "errors":   [ { "message": "...", "nodeId": "1:2", "nodeName": "Title" } ],
    "warnings": [ ],
    "info":     [ ]
  }
}
```
`ok` is false when the import failed; `report.errors` then says why.

## Export document

The body of `/document` is a REST-API-shaped Figma file:
```json
{
  "name": "UI Prototyping",
  "schemaVersion": 0,
  "pluginVersion": "2.0.0",
  "partialExport": false,
  "components": { },
  "componentSets": { },
  "styles": { },
  "designTokens": { },
  "document": {
    "id": "0:0", "type": "DOCUMENT", "name": "Document",
    "children": [
      { "id": "<page id>", "type": "CANVAS", "name": "<page name>", "flowStartingPoints": [], "children": [ "<screen frames>" ] },
      { "id": "figma-bridge-components", "type": "CANVAS", "name": "Components", "flowStartingPoints": [], "children": [ "<component sets and components used>" ] }
    ]
  }
}
```
- Screens are the page's top-level frames, or only the selected ones. With a selection, `partialExport` is true.
- Every node comes from `node.exportAsync({ format: 'JSON_REST_V1' }).document`, plus `relativeTransform` and `size` copied from the live node when missing.
- Local components used by the screens (and by those components) go on the `Components` canvas. For a variant, export its whole component set. Library (remote) components are skipped; Unity builds those instances inline.
- Nodes the artist marked as ignored are removed from the document.

### `figmaBridge` on nodes
Any node may carry a `figmaBridge` object with the artist's settings and the plugin's own decisions:
```json
"figmaBridge": {
  "role": "button",
  "bindingName": "BackButton",
  "localizationKey": "menu/back",
  "nineSlice": { "left": 40, "top": 40, "right": 40, "bottom": 40 },
  "textAutoSize": true,
  "flatten": true,
  "flattenReason": "blend mode OVERLAY"
}
```
- `role`: `auto` (default), `button`, `toggle`, `toggleGroup`, `slider`, `inputField`, `scrollView`, `image` (flatten into one picture), `ignore`.
- `bindingName`: the key game code uses to find this object. Defaults to the layer name.
- `localizationKey`: text nodes only.
- `nineSlice`: insets in Figma pixels. The node is exported as a picture and drawn as a sliced image.
- `textAutoSize`: text nodes only; shrink text to fit its box.
- `flatten`: render this node as one picture. Set by the artist (role `image`) or by the plugin's automatic rules below, with `flattenReason` saying why.

Artist settings are stored per node with `node.setSharedPluginData('figmabridge', 'settings', JSON)`, so they survive a change of plugin id.

Layer-name tags are a shortcut for the same roles and work even without the plugin panel: `[button]`, `[toggle]`, `[togglegroup]`, `[slider]`, `[input]`, `[scroll]`, `[image]`, `[flatten]` (same as `[image]`), `[ignore]`, anywhere in the name, any case. Unity reads both.

### Automatic flattening
The plugin sets `flatten: true` with a reason on nodes Unity can't draw natively:
- A visible effect other than a drop shadow (inner shadow, layer blur, background blur, noise, texture, glass or anything unknown).
- More than one visible fill, or a visible fill that isn't solid, linear gradient, radial gradient or image.
- Node types LINE, REGULAR_POLYGON, STAR, BOOLEAN_OPERATION, VECTOR, or an ELLIPSE with a partial arc.
- Dashed strokes, or different stroke weights per side.
- A layer with a blend mode other than normal or pass-through: its parent is flattened so the blend is kept, unless the parent is a screen or a component root, in which case the layer itself is flattened and a warning says the blend may look different.
- Text is never flattened automatically; a warning is shown instead.
- Instances are never flattened; the rules apply inside the component definition.

### `designTokens`
```json
{
  "variables": [ { "collection": "Colors", "name": "brand/primary", "type": "COLOR", "valuesByMode": { "Light": { "r": 1, "g": 0, "b": 0, "a": 1 } } } ],
  "paintStyles": [ { "name": "Ink", "color": { "r": 0, "g": 0, "b": 0, "a": 1 } } ],
  "textStyles": [ { "name": "Heading", "fontFamily": "Roboto", "fontStyle": "Bold", "fontSize": 64, "letterSpacing": 0, "lineHeightPx": 80 } ]
}
```
Only local variables and styles. Variable types: `COLOR`, `FLOAT`, `STRING`, `BOOLEAN`. Aliases are resolved to their value.

## Offline bundle

When Unity isn't running, the plugin can save one `.figmabundle.json` file:
```json
{ "bundleVersion": 1, "document": { }, "images": { "<hash>": "<base64>" }, "renders": { "<node id>": "<base64 PNG>" }, "renderScale": 1 }
```
Renders cover every node the plugin flattens, every VECTOR and BOOLEAN_OPERATION, and every node with "render" in its name. Unity imports it from **Tools > Figma Bridge > Import Export File**.
