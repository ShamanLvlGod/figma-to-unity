const PLUGIN_VERSION = '2.0.0';
const UI_WIDTH = 340;
const UI_START_HEIGHT = 460;
const UI_MIN_HEIGHT = 160;
const UI_MAX_HEIGHT = 820;
const PLUGIN_DATA_NAMESPACE = 'figmabridge';
const SETTINGS_KEY = 'settings';
const IGNORED_CHECKS_KEY = 'ignoredChecks';
const COMPONENTS_CANVAS_ID = 'figma-bridge-components';
const CANVAS_BACKGROUND = { r: 0.12, g: 0.12, b: 0.12, a: 1 };
const MAX_TEXTURE_SIDE = 4096;
const NAVIGATION_PAGE_WINDOW_MS = 4000;
const FONT_LIST_CACHE_MS = 60000;
const MAX_ALIAS_DEPTH = 16;

const ROLES = ['auto', 'button', 'toggle', 'toggleGroup', 'slider', 'inputField', 'scrollView', 'image', 'ignore'];
const INTERACTIVE_ROLES = ['button', 'toggle', 'toggleGroup', 'slider', 'inputField', 'scrollView'];
const INTERACTIVE_ROLE_NAMES = {
  button: 'a button',
  toggle: 'a toggle',
  toggleGroup: 'a toggle group',
  slider: 'a slider',
  inputField: 'a text input',
  scrollView: 'a scroll area',
  variant: 'an instance of a component with variants',
};
const NAME_TAGS = [
  { tag: '[button]', role: 'button' },
  { tag: '[togglegroup]', role: 'toggleGroup' },
  { tag: '[toggle]', role: 'toggle' },
  { tag: '[slider]', role: 'slider' },
  { tag: '[input]', role: 'inputField' },
  { tag: '[scroll]', role: 'scrollView' },
  { tag: '[image]', role: 'image' },
  { tag: '[flatten]', role: 'image' },
  { tag: '[ignore]', role: 'ignore' },
];
const INSET_SIDES = ['left', 'top', 'right', 'bottom'];
const SUPPORTED_FILL_TYPES = ['SOLID', 'GRADIENT_LINEAR', 'GRADIENT_RADIAL', 'IMAGE'];
const SHAPE_TYPE_REASONS = {
  LINE: 'line',
  REGULAR_POLYGON: 'polygon',
  STAR: 'star',
  BOOLEAN_OPERATION: 'boolean shape',
  VECTOR: 'vector shape',
};
const NEVER_AUTO_FLATTEN_TYPES = ['TEXT', 'INSTANCE', 'COMPONENT', 'COMPONENT_SET'];
const ROOT_LIKE_PARENT_TYPES = ['COMPONENT', 'COMPONENT_SET', 'INSTANCE'];
const FULL_CIRCLE_SWEEP = 6.2822;
const DEFAULT_LAYER_NAME_PATTERN = /^(frame|rectangle|group|ellipse|vector|text|image|line|arrow|polygon|star|union|subtract|intersect|exclude|component|instance|slice|section)\s+\d+$/i;
const VARIANT_NAME_PATTERN = /^\s*[^=,]+=[^=,]+(,\s*[^=,]+=[^=,]+)*\s*$/;
const NAME_TAG_PATTERN = /\[[a-z]+\]/gi;
const RENDER_WORD_PATTERN = /(^|[^a-z])render([^a-z]|$)/i;
const DEFAULT_STATE_PATTERN = /default|normal|idle/i;
const SEVERITY_ORDER = { error: 0, warning: 1, info: 2 };

function errorText(error) {
  if (!error) return 'Unknown error';
  if (typeof error === 'string') return error;
  return error.message || String(error);
}

function quote(text) {
  return '"' + text + '"';
}

function plural(count, singular, pluralForm) {
  return count + ' ' + (count === 1 ? singular : (pluralForm || singular + 's'));
}

function roundNumber(value) {
  return Math.round(value * 1000) / 1000;
}

function formatSize(value) {
  return String(Math.round(value * 100) / 100);
}

function listNames(names, limit) {
  const shown = names.slice(0, limit).map(quote);
  const rest = names.length - shown.length;
  return shown.join(', ') + (rest > 0 ? ' and ' + rest + ' more' : '');
}

function groupBy(items, keyOf) {
  const groups = new Map();
  for (const item of items) {
    const key = keyOf(item);
    if (key === null || key === undefined) continue;
    if (!groups.has(key)) groups.set(key, []);
    groups.get(key).push(item);
  }
  return groups;
}

function cleanLayerName(name) {
  const cleaned = String(name || '').replace(NAME_TAG_PATTERN, '').trim();
  return cleaned.length === 0 ? 'Unnamed' : cleaned;
}

function tagsInName(name) {
  const lower = String(name || '').toLowerCase();
  return NAME_TAGS.filter(entry => lower.indexOf(entry.tag) >= 0);
}

function roleFromTags(name) {
  const tags = tagsInName(name);
  return tags.length > 0 ? tags[0].role : 'auto';
}

function hasRenderWord(name) {
  return RENDER_WORD_PATTERN.test(String(name || ''));
}

function nameMentionsRender(name) {
  return String(name || '').toLowerCase().indexOf('render') >= 0;
}

function isDefaultLayerName(name) {
  return DEFAULT_LAYER_NAME_PATTERN.test(cleanLayerName(name));
}

function isVariantName(name) {
  return VARIANT_NAME_PATTERN.test(String(name || ''));
}

function toInset(value) {
  if (value === null || value === undefined) return null;
  if (typeof value === 'string' && value.trim() === '') return null;
  const number = Number(value);
  return isFinite(number) && number >= 0 ? number : null;
}

function normalizeSettings(raw, isText) {
  const source = raw && typeof raw === 'object' ? raw : {};
  const settings = {
    role: ROLES.indexOf(source.role) >= 0 ? source.role : 'auto',
    bindingName: typeof source.bindingName === 'string' ? source.bindingName.trim() : '',
    localizationKey: '',
    nineSlice: null,
    textAutoSize: false,
  };
  if (isText) {
    settings.localizationKey = typeof source.localizationKey === 'string' ? source.localizationKey.trim() : '';
    settings.textAutoSize = source.textAutoSize === true;
  }
  if (source.nineSlice && typeof source.nineSlice === 'object') {
    const insets = {};
    let hasAnySide = false;
    for (const side of INSET_SIDES) {
      const inset = toInset(source.nineSlice[side]);
      if (inset === null) continue;
      insets[side] = inset;
      hasAnySide = true;
    }
    if (hasAnySide) settings.nineSlice = insets;
  }
  return settings;
}

function settingsForStorage(settings) {
  const stored = {};
  if (settings.role !== 'auto') stored.role = settings.role;
  if (settings.bindingName) stored.bindingName = settings.bindingName;
  if (settings.localizationKey) stored.localizationKey = settings.localizationKey;
  if (settings.nineSlice) stored.nineSlice = Object.assign({}, settings.nineSlice);
  if (settings.textAutoSize) stored.textAutoSize = true;
  return Object.keys(stored).length > 0 ? JSON.stringify(stored) : '';
}

function hasNineSlice(settings) {
  const insets = settings && settings.nineSlice;
  if (!insets) return false;
  return INSET_SIDES.some(side => (Number(insets[side]) || 0) > 0);
}

function bridgeObjectFor(settings, flatten) {
  const bridge = {};
  if (settings.role !== 'auto') bridge.role = settings.role;
  if (settings.bindingName) bridge.bindingName = settings.bindingName;
  if (settings.localizationKey) bridge.localizationKey = settings.localizationKey;
  if (hasNineSlice(settings)) {
    bridge.nineSlice = {
      left: settings.nineSlice.left || 0,
      top: settings.nineSlice.top || 0,
      right: settings.nineSlice.right || 0,
      bottom: settings.nineSlice.bottom || 0,
    };
  }
  if (settings.textAutoSize) bridge.textAutoSize = true;
  if (flatten) {
    bridge.flatten = true;
    bridge.flattenReason = flatten.reasons.join(', ');
  }
  return Object.keys(bridge).length > 0 ? bridge : null;
}

function readableKind(value) {
  return String(value || 'unknown').toLowerCase().replace(/_/g, ' ');
}

function isVisiblePaint(paint) {
  return !!paint && paint.visible !== false;
}

function addUnique(list, value) {
  if (list.indexOf(value) < 0) list.push(value);
}

function effectProblems(effects) {
  const reasons = [];
  if (!Array.isArray(effects)) return reasons;
  for (const effect of effects) {
    if (!effect || effect.visible === false || effect.type === 'DROP_SHADOW') continue;
    addUnique(reasons, readableKind(effect.type));
  }
  return reasons;
}

function fillProblems(fills) {
  const reasons = [];
  if (!Array.isArray(fills)) return reasons;
  const visibleFills = fills.filter(isVisiblePaint);
  if (visibleFills.length > 1) reasons.push(visibleFills.length + ' fills');
  for (const fill of visibleFills) {
    if (SUPPORTED_FILL_TYPES.indexOf(fill.type) < 0) addUnique(reasons, readableKind(fill.type) + ' fill');
  }
  return reasons;
}

function hasUnevenStrokeWeights(style) {
  if (style.strokeWeight === 'MIXED') return true;
  const sides = [style.strokeTopWeight, style.strokeRightWeight, style.strokeBottomWeight, style.strokeLeftWeight]
    .filter(value => typeof value === 'number');
  return sides.length === 4 && sides.some(value => Math.abs(value - sides[0]) > 0.001);
}

function strokeProblems(style) {
  const strokes = Array.isArray(style.strokes) ? style.strokes.filter(isVisiblePaint) : [];
  if (strokes.length === 0) return [];
  const reasons = [];
  if (Array.isArray(style.dashPattern) && style.dashPattern.some(length => length > 0)) reasons.push('dashed stroke');
  if (hasUnevenStrokeWeights(style)) reasons.push('different stroke widths per side');
  return reasons;
}

function shapeProblems(style) {
  if (SHAPE_TYPE_REASONS[style.type]) return [SHAPE_TYPE_REASONS[style.type]];
  if (style.type === 'ELLIPSE' && style.arcData && typeof style.arcData === 'object') {
    const arc = style.arcData;
    if ((arc.innerRadius || 0) > 0.0001) return ['ring shape'];
    if (Math.abs((arc.endingAngle || 0) - (arc.startingAngle || 0)) < FULL_CIRCLE_SWEEP) return ['ellipse arc'];
  }
  return [];
}

function flattenReasonsFor(style) {
  if (!style) return [];
  return shapeProblems(style).concat(effectProblems(style.effects), fillProblems(style.fills), strokeProblems(style));
}

function textStyleProblems(style) {
  if (!style) return [];
  return effectProblems(style.effects).concat(fillProblems(style.fills));
}

function canAutoFlatten(entry) {
  return !entry.isScreen && NEVER_AUTO_FLATTEN_TYPES.indexOf(entry.type) < 0;
}

function isRootLikeParent(parent) {
  return !parent || parent.isScreen || ROOT_LIKE_PARENT_TYPES.indexOf(parent.type) >= 0;
}

function addFlattenReason(entry, reason) {
  if (!entry.flatten) entry.flatten = { reasons: [], auto: true };
  addUnique(entry.flatten.reasons, reason);
}

function artistPictureReason(entry) {
  if (entry.settings.role === 'image') return 'role image';
  const tag = tagsInName(entry.name).find(item => item.role === 'image');
  return tag ? 'name tag ' + tag.tag : 'role image';
}

function decideFlattening(entries) {
  for (const entry of entries) {
    entry.flatten = null;
    entry.textProblems = [];
    entry.blendSelf = null;
    entry.blendUnsupported = null;
    if (entry.insideLocalInstance || entry.isScreen) continue;
    if (entry.role === 'image') {
      entry.flatten = { reasons: [artistPictureReason(entry)], auto: false };
      continue;
    }
    if (entry.type === 'TEXT') {
      entry.textProblems = textStyleProblems(entry.style);
      continue;
    }
    if (!canAutoFlatten(entry)) continue;
    for (const reason of flattenReasonsFor(entry.style)) addFlattenReason(entry, reason);
  }

  for (const entry of entries) {
    if (entry.insideLocalInstance || entry.isScreen) continue;
    if (entry.type === 'COMPONENT' || entry.type === 'COMPONENT_SET') continue;
    const mode = entry.style ? entry.style.blendMode : null;
    if (!mode || mode === 'NORMAL' || mode === 'PASS_THROUGH' || mode === 'MIXED') continue;
    const reason = 'blend mode ' + mode;
    if (isRootLikeParent(entry.parent)) {
      if (canAutoFlatten(entry)) {
        addFlattenReason(entry, reason);
        entry.blendSelf = mode;
      } else {
        entry.blendUnsupported = mode;
      }
    } else {
      addFlattenReason(entry.parent, reason + ' on ' + quote(entry.cleanName));
    }
  }

  for (const entry of entries) {
    const parent = entry.parent;
    entry.pictureAncestor = parent ? (parent.pictureAncestor || (parent.isPicture ? parent : null)) : null;
    if (entry.pictureAncestor) {
      entry.flatten = null;
      entry.blendSelf = null;
      entry.isPicture = false;
      continue;
    }
    entry.isPicture = !entry.isScreen && (
      !!entry.flatten || entry.role === 'image' || hasNineSlice(entry.settings) || hasRenderWord(entry.name)
    );
  }
}

function compareLayerNames(expected, actual) {
  const counts = new Map();
  for (const name of expected) counts.set(name, (counts.get(name) || 0) + 1);
  const extra = [];
  for (const name of actual) {
    const remaining = counts.get(name) || 0;
    if (remaining > 0) counts.set(name, remaining - 1);
    else extra.push(name);
  }
  const missing = [];
  counts.forEach((remaining, name) => {
    for (let index = 0; index < remaining; index++) missing.push(name);
  });
  return { missing, extra };
}

function letterSpacingPx(letterSpacing, fontSize) {
  if (!letterSpacing || typeof letterSpacing !== 'object') return 0;
  const value = Number(letterSpacing.value) || 0;
  return roundNumber(letterSpacing.unit === 'PERCENT' ? value / 100 * fontSize : value);
}

function lineHeightPx(lineHeight, fontSize) {
  if (!lineHeight || typeof lineHeight !== 'object' || lineHeight.unit === 'AUTO') return null;
  const value = Number(lineHeight.value) || 0;
  return roundNumber(lineHeight.unit === 'PERCENT' ? value / 100 * fontSize : value);
}

function isVariableAlias(value) {
  return !!value && typeof value === 'object' && value.type === 'VARIABLE_ALIAS' && typeof value.id === 'string';
}

function plainTokenValue(value) {
  if (value === null || value === undefined) return null;
  if (typeof value === 'object') {
    if ('r' in value && 'g' in value && 'b' in value) {
      return { r: value.r, g: value.g, b: value.b, a: value.a === undefined ? 1 : value.a };
    }
    return JSON.parse(JSON.stringify(value));
  }
  return value;
}

function walkRest(node, visit) {
  visit(node);
  for (const child of node.children || []) walkRest(child, visit);
}

function collectImageRefs(root) {
  const refs = [];
  const stack = [root];
  while (stack.length > 0) {
    const current = stack.pop();
    if (Array.isArray(current)) {
      for (const item of current) if (item && typeof item === 'object') stack.push(item);
      continue;
    }
    for (const key of Object.keys(current)) {
      const value = current[key];
      if (key === 'imageRef' && typeof value === 'string' && value) addUnique(refs, value);
      else if (value && typeof value === 'object') stack.push(value);
    }
  }
  return refs;
}

function bundleRenderKind(node, depth) {
  if (node.type === 'CANVAS' || node.type === 'COMPONENT_SET') return null;
  if (depth <= 1 && node.type === 'FRAME') return null;
  const bridge = node.figmaBridge || {};
  if (bridge.flatten) return 'picture';
  const role = bridge.role && bridge.role !== 'auto' ? bridge.role : roleFromTags(node.name);
  if (role === 'image') return 'picture';
  if (hasNineSlice({ nineSlice: bridge.nineSlice })) return 'picture';
  if (hasRenderWord(node.name)) return 'picture';
  if (shapeProblems({ type: node.type, arcData: node.arcData }).length > 0) return 'picture';
  if (nameMentionsRender(node.name)) return 'extra';
  return null;
}

function collectBundleRenders(documentNode) {
  const definedComponentIds = new Set();
  walkRest(documentNode, node => {
    if (node.type === 'COMPONENT') definedComponentIds.add(node.id);
  });
  const renderIds = [];
  function visit(node, depth) {
    if (node.visible === false) return;
    if (node.type === 'INSTANCE' && definedComponentIds.has(node.componentId)) return;
    const kind = bundleRenderKind(node, depth);
    if (kind) addUnique(renderIds, node.id);
    if (kind === 'picture') return;
    for (const child of node.children || []) visit(child, depth + 1);
  }
  for (const canvas of documentNode.children || []) {
    for (const child of canvas.children || []) visit(child, 1);
  }
  return renderIds;
}

function fontKey(font) {
  const normalize = value => String(value || '').toLowerCase().replace(/[\s_-]+/g, '');
  return normalize(font.family) + '|' + normalize(font.style);
}

function fontLabel(font) {
  return quote(font.family + ' ' + font.style);
}

const state = {
  exportPageId: null,
  scopeFrameIds: null,
  navigationSelectionKey: null,
  navigationPage: null,
  unityInfo: { fonts: null, googleFonts: false, designResolution: null },
  pictureInfoById: new Map(),
  imageSizeCache: new Map(),
  fontListCache: null,
};

function readMixed(node, key) {
  if (!(key in node)) return undefined;
  const value = node[key];
  return value === figma.mixed ? 'MIXED' : value;
}

function describeNode(node) {
  try {
    return {
      type: node.type,
      blendMode: readMixed(node, 'blendMode'),
      effects: readMixed(node, 'effects'),
      fills: readMixed(node, 'fills'),
      strokes: readMixed(node, 'strokes'),
      dashPattern: readMixed(node, 'dashPattern'),
      strokeWeight: readMixed(node, 'strokeWeight'),
      strokeTopWeight: readMixed(node, 'strokeTopWeight'),
      strokeRightWeight: readMixed(node, 'strokeRightWeight'),
      strokeBottomWeight: readMixed(node, 'strokeBottomWeight'),
      strokeLeftWeight: readMixed(node, 'strokeLeftWeight'),
      arcData: readMixed(node, 'arcData'),
    };
  } catch (error) {
    return { type: node.type };
  }
}

function readJsonPluginData(node, key) {
  try {
    const raw = node.getSharedPluginData(PLUGIN_DATA_NAMESPACE, key);
    return raw ? JSON.parse(raw) : null;
  } catch (error) {
    return null;
  }
}

function readSettings(node) {
  return normalizeSettings(readJsonPluginData(node, SETTINGS_KEY), node.type === 'TEXT');
}

function readIgnoredChecks(node) {
  const list = readJsonPluginData(node, IGNORED_CHECKS_KEY);
  return new Set(Array.isArray(list) ? list.filter(item => typeof item === 'string') : []);
}

function effectiveRole(node) {
  const settings = readSettings(node);
  return settings.role !== 'auto' ? settings.role : roleFromTags(node.name);
}

function pageOf(node) {
  let current = node;
  while (current && current.type !== 'PAGE') current = current.parent;
  return current || null;
}

async function loadPageOf(node) {
  const page = pageOf(node);
  if (page) await page.loadAsync();
  return page;
}

function findPage(pageId) {
  return figma.root.children.find(page => page.id === pageId) || null;
}

function listPages() {
  return figma.root.children.map(page => ({ id: page.id, name: page.name }));
}

function selectionKey(nodes) {
  return nodes.map(node => node.id).sort().join(',');
}

function sameIdList(left, right) {
  if (!left || !right) return left === right;
  return left.slice().sort().join(',') === right.slice().sort().join(',');
}

function selectedScreenIds(page) {
  return page.selection
    .filter(node => node.type === 'FRAME' && node.parent && node.parent.id === page.id)
    .map(node => node.id);
}

function userSelectedScreenIds(page) {
  if (state.navigationSelectionKey !== null && selectionKey(page.selection) === state.navigationSelectionKey) return null;
  const ids = selectedScreenIds(page);
  return ids.length > 0 ? ids : null;
}

function hasInstanceAncestor(node) {
  let current = node.parent;
  while (current && current.type !== 'PAGE') {
    if (current.type === 'INSTANCE') return true;
    current = current.parent;
  }
  return false;
}

function describeLayer(node) {
  return {
    id: node.id,
    name: node.name,
    cleanName: cleanLayerName(node.name),
    type: node.type,
    isText: node.type === 'TEXT',
    settings: readSettings(node),
    tags: tagsInName(node.name).map(item => item.tag),
    tagRole: roleFromTags(node.name),
    insideInstance: hasInstanceAncestor(node),
    picture: state.pictureInfoById.get(node.id) || null,
  };
}

function selectionMessage(recheck) {
  const selection = figma.currentPage.selection;
  return {
    type: 'selection',
    count: selection.length,
    layer: selection.length === 1 ? describeLayer(selection[0]) : null,
    recheck,
  };
}

function postLayerInfo() {
  const selection = figma.currentPage.selection;
  if (selection.length !== 1) return;
  figma.ui.postMessage({ type: 'layerInfo', layer: describeLayer(selection[0]) });
}

function postProgress(text) {
  figma.ui.postMessage({ type: 'progress', text });
}

async function resolveScope() {
  let page = findPage(state.exportPageId);
  if (!page) {
    page = figma.currentPage;
    state.exportPageId = page.id;
    state.scopeFrameIds = null;
  }
  await page.loadAsync();
  const frames = page.children.filter(node => node.type === 'FRAME');
  let screens = frames;
  let partialExport = false;
  if (state.scopeFrameIds) {
    const chosen = frames.filter(frame => state.scopeFrameIds.indexOf(frame.id) >= 0);
    if (chosen.length > 0) {
      screens = chosen;
      partialExport = true;
    }
  }
  const included = [];
  const ignored = [];
  for (const screen of screens) {
    if (effectiveRole(screen) === 'ignore') ignored.push(screen);
    else included.push(screen);
  }
  return { page, screens: included, ignoredScreens: ignored, partialExport };
}

function summarizeScope(scope) {
  return {
    pageId: scope.page.id,
    pageName: scope.page.name,
    partialExport: scope.partialExport,
    screens: scope.screens.map(screen => ({ id: screen.id, name: screen.name })),
    ignoredScreens: scope.ignoredScreens.map(screen => screen.name),
  };
}

function postScope() {
  resolveScope()
    .then(scope => figma.ui.postMessage({ type: 'scope', scope: summarizeScope(scope) }))
    .catch(error => figma.ui.postMessage({ type: 'scope', scope: null, error: errorText(error) }));
}

function setScopeFrames(ids) {
  if (sameIdList(ids, state.scopeFrameIds)) return;
  state.scopeFrameIds = ids;
  postScope();
}

function handleSelectionChange() {
  const page = figma.currentPage;
  const byPlugin = state.navigationSelectionKey !== null && selectionKey(page.selection) === state.navigationSelectionKey;
  if (!byPlugin) {
    state.navigationSelectionKey = null;
    if (page.id === state.exportPageId) setScopeFrames(userSelectedScreenIds(page));
  }
  figma.ui.postMessage(selectionMessage(!byPlugin));
}

function handlePageChange() {
  const navigation = state.navigationPage;
  state.navigationPage = null;
  const byPlugin = !!navigation && navigation.pageId === figma.currentPage.id && Date.now() < navigation.until;
  figma.ui.postMessage({ type: 'pages', pages: listPages(), currentPageId: figma.currentPage.id, byPlugin });
}

function describeMainComponent(main) {
  if (!main) return null;
  const root = main.parent && main.parent.type === 'COMPONENT_SET' ? main.parent : main;
  return {
    main,
    root,
    remote: !!(main.remote || root.remote),
    isVariant: root.id !== main.id,
    name: root.name,
  };
}

async function resolveMainComponents(roots) {
  const infoByInstanceId = new Map();
  const seenRootIds = new Set(roots.map(root => root.id));
  let pending = roots.slice();
  while (pending.length > 0) {
    const batch = new Map();
    for (const root of pending) {
      if (root.type === 'INSTANCE') batch.set(root.id, root);
      if ('findAllWithCriteria' in root) {
        for (const instance of root.findAllWithCriteria({ types: ['INSTANCE'] })) batch.set(instance.id, instance);
      }
    }
    pending = [];
    const unresolved = Array.from(batch.values()).filter(instance => !infoByInstanceId.has(instance.id));
    const mains = await Promise.all(unresolved.map(instance => instance.getMainComponentAsync().catch(() => null)));
    for (let index = 0; index < unresolved.length; index++) {
      const info = describeMainComponent(mains[index]);
      infoByInstanceId.set(unresolved[index].id, info);
      if (!info || info.remote || seenRootIds.has(info.root.id)) continue;
      seenRootIds.add(info.root.id);
      await loadPageOf(info.root);
      pending.push(info.root);
    }
  }
  return infoByInstanceId;
}

const OUTSIDE_CONTEXT = { insideInstance: false, insideLocalInstance: false, definition: null };

function walkTree(node, parentEntry, context, analysis, isScreen) {
  const settings = readSettings(node);
  const tagRole = roleFromTags(node.name);
  const role = settings.role !== 'auto' ? settings.role : tagRole;
  if (role === 'ignore') {
    analysis.ignoredIds.add(node.id);
    return null;
  }
  analysis.walkedIds.add(node.id);
  analysis.liveById.set(node.id, node);
  const entry = {
    node,
    id: node.id,
    type: node.type,
    name: node.name,
    cleanName: cleanLayerName(node.name),
    settings,
    tagRole,
    role,
    parent: parentEntry,
    children: [],
    isScreen: !!isScreen,
    insideInstance: context.insideInstance,
    insideLocalInstance: context.insideLocalInstance,
    definition: context.definition,
    ignoredChecks: readIgnoredChecks(node),
    instanceInfo: null,
    style: null,
  };
  analysis.entries.push(entry);
  if (parentEntry) parentEntry.children.push(entry);

  let childContext = context;
  if (node.type === 'INSTANCE') {
    const info = analysis.mainByInstanceId.get(node.id) || null;
    entry.instanceInfo = info;
    const isLocal = !!info && !info.remote;
    if (isLocal && !analysis.queuedDefinitionIds.has(info.root.id)) {
      analysis.queuedDefinitionIds.add(info.root.id);
      analysis.definitionQueue.push(info.root);
    }
    childContext = {
      insideInstance: true,
      insideLocalInstance: context.insideLocalInstance || isLocal,
      definition: context.definition,
    };
  } else if ((node.type === 'COMPONENT' || node.type === 'COMPONENT_SET') && !context.insideInstance) {
    childContext = { insideInstance: false, insideLocalInstance: false, definition: entry };
  }

  if ('children' in node) {
    for (const child of node.children) walkTree(child, entry, childContext, analysis, false);
  }
  return entry;
}

function createRowCollector() {
  const rows = [];
  const ignoredRows = [];
  function makeRow(severity, check, entries, buildMessage) {
    return {
      key: severity + ':' + check + ':' + entries.map(entry => entry.id).join(','),
      severity,
      check,
      message: buildMessage(entries),
      nodeIds: entries.map(entry => entry.id),
      canIgnore: severity !== 'error',
    };
  }
  function add(severity, check, entries, buildMessage, minimum) {
    const required = minimum || 1;
    const active = [];
    const ignored = [];
    for (const entry of entries) {
      if (severity !== 'error' && entry.ignoredChecks.has(check)) ignored.push(entry);
      else active.push(entry);
    }
    if (active.length >= required) rows.push(makeRow(severity, check, active, buildMessage));
    if (ignored.length > 0) ignoredRows.push(makeRow(severity, check, ignored, buildMessage));
  }
  function result() {
    const bySeverity = (left, right) => SEVERITY_ORDER[left.severity] - SEVERITY_ORDER[right.severity];
    return { rows: rows.slice().sort(bySeverity), ignoredRows: ignoredRows.slice().sort(bySeverity) };
  }
  return { add, result };
}

function describeLayers(entries, noun) {
  const first = quote(entries[0].cleanName);
  if (entries.length === 1) return first;
  return first + ' and ' + plural(entries.length - 1, 'more ' + (noun || 'layer'));
}

function verbFor(entries, singular, pluralForm) {
  return entries.length === 1 ? singular : pluralForm;
}

function bindingNameOf(entry) {
  return entry.settings.bindingName || entry.cleanName;
}

function definitionLabel(definitionEntry) {
  const parent = definitionEntry.parent;
  if (definitionEntry.type === 'COMPONENT' && parent && parent.type === 'COMPONENT_SET') return parent.cleanName;
  return definitionEntry.cleanName;
}

function fontNamesOf(textNode) {
  try {
    if (textNode.fontName !== figma.mixed) return [textNode.fontName];
    return textNode.getRangeAllFontNames(0, textNode.characters.length);
  } catch (error) {
    return [];
  }
}

async function availableFontKeys() {
  const cache = state.fontListCache;
  if (cache && Date.now() - cache.time < FONT_LIST_CACHE_MS) return cache.keys;
  try {
    const fonts = await figma.listAvailableFontsAsync();
    const keys = new Set(fonts.map(font => fontKey(font.fontName)));
    state.fontListCache = { time: Date.now(), keys };
    return keys;
  } catch (error) {
    return null;
  }
}

async function imageSizeOf(hash) {
  if (state.imageSizeCache.has(hash)) return state.imageSizeCache.get(hash);
  let size = null;
  try {
    const image = figma.getImageByHash(hash);
    if (image) size = await image.getSizeAsync();
  } catch (error) {
    size = null;
  }
  state.imageSizeCache.set(hash, size);
  return size;
}

function imageHashesOf(node) {
  const hashes = [];
  for (const key of ['fills', 'strokes']) {
    if (!(key in node)) continue;
    const paints = node[key];
    if (!Array.isArray(paints)) continue;
    for (const paint of paints) {
      if (paint && paint.type === 'IMAGE' && paint.imageHash) addUnique(hashes, paint.imageHash);
    }
  }
  return hashes;
}

function checkDuplicateScreens(screenEntries, collector) {
  const groups = groupBy(screenEntries, entry => entry.cleanName.toLowerCase());
  groups.forEach(group => {
    if (group.length < 2) return;
    collector.add('error', 'duplicateScreenName', group, items =>
      plural(items.length, 'screen') + ' are named ' + quote(items[0].cleanName) +
      '. They would overwrite each other in Unity, so give each one its own name.', 2);
  });
}

async function checkMissingFonts(entries, collector) {
  const texts = entries.filter(entry => entry.type === 'TEXT' && entry.node.hasMissingFont === true);
  if (texts.length === 0) return;
  const available = await availableFontKeys();
  const groups = new Map();
  for (const entry of texts) {
    const fonts = fontNamesOf(entry.node);
    const missing = available ? fonts.filter(font => !available.has(fontKey(font))) : [];
    const label = (missing.length > 0 ? missing : fonts).map(fontLabel).join(', ') || 'a font';
    if (!groups.has(label)) groups.set(label, []);
    groups.get(label).push(entry);
  }
  groups.forEach((group, label) => {
    collector.add('error', 'missingFont', group, items =>
      'Font ' + label + ' is missing on this computer. ' + describeLayers(items, 'text') + ' ' +
      verbFor(items, 'uses', 'use') + ' it. Install the font or pick another one.');
  });
}

function checkUnknownFonts(entries, collector) {
  const unityFonts = state.unityInfo.fonts;
  if (!Array.isArray(unityFonts)) return;
  const known = new Set(unityFonts.map(fontKey));
  const groups = new Map();
  for (const entry of entries) {
    if (entry.type !== 'TEXT' || entry.pictureAncestor || entry.node.hasMissingFont === true) continue;
    for (const font of fontNamesOf(entry.node)) {
      if (known.has(fontKey(font))) continue;
      const label = fontLabel(font);
      if (!groups.has(label)) groups.set(label, []);
      const group = groups.get(label);
      if (group.indexOf(entry) < 0) group.push(entry);
    }
  }
  const googleFonts = !!state.unityInfo.googleFonts;
  groups.forEach((group, label) => {
    collector.add('warning', 'unknownFont', group, items =>
      'Unity doesn\'t have the font ' + label + ' yet' +
      (googleFonts
        ? '. It will try to download it from Google Fonts during the export.'
        : '. Add it to Unity as a TextMeshPro font, or the text will use a fallback font.') +
      ' Used by ' + describeLayers(items, 'text') + '.');
  });
}

function checkDesignResolution(screenEntries, collector) {
  const resolution = state.unityInfo.designResolution;
  if (!resolution || !(resolution.width > 0) || !(resolution.height > 0)) return;
  for (const entry of screenEntries) {
    const width = entry.node.width;
    const height = entry.node.height;
    if (Math.abs(width - resolution.width) <= 0.5 && Math.abs(height - resolution.height) <= 0.5) continue;
    collector.add('warning', 'designResolution', [entry], items =>
      'Screen ' + quote(items[0].cleanName) + ' is ' + formatSize(width) + ' \u00d7 ' + formatSize(height) +
      ', but Unity is set up for ' + resolution.width + ' \u00d7 ' + resolution.height + '. It may look stretched or cut off.');
  }
}

function checkDuplicateSiblings(entries, collector) {
  const groups = groupBy(entries, entry => {
    if (!entry.parent || entry.parent.type === 'COMPONENT_SET') return null;
    return entry.parent.id + '\u0000' + bindingNameOf(entry);
  });
  groups.forEach(group => {
    if (group.length < 2) return;
    collector.add('warning', 'duplicateName', group, items =>
      plural(items.length, 'layer') + ' are named ' + quote(bindingNameOf(items[0])) + ' inside ' +
      quote(items[0].parent.cleanName) + '. Game code that finds layers by name only gets the first one, so rename them.', 2);
  });
}

function interactiveKindOf(entry) {
  if (INTERACTIVE_ROLES.indexOf(entry.role) >= 0) return entry.role;
  if (/button/i.test(entry.name)) return 'button';
  if (entry.type === 'INSTANCE' && entry.instanceInfo && entry.instanceInfo.isVariant) return 'variant';
  return null;
}

function checkDefaultNames(entries, collector) {
  for (const entry of entries) {
    if (!isDefaultLayerName(entry.name)) continue;
    const interactiveKind = interactiveKindOf(entry);
    const componentChild = !!entry.parent && entry.parent.type === 'COMPONENT';
    if (componentChild) {
      collector.add('warning', 'defaultName', [entry], items =>
        quote(items[0].cleanName) + ' is part of the component ' + quote(definitionLabel(items[0].parent)) +
        ' but still has a default name. Give it a clear name so variants and game code can match it.');
    } else if (interactiveKind && !entry.settings.bindingName) {
      collector.add('warning', 'defaultName', [entry], items =>
        quote(items[0].cleanName) + ' is ' + INTERACTIVE_ROLE_NAMES[interactiveKind] +
        ' but still has a default name. Give it a clear name so game code can find it.');
    }
  }
}

async function checkLargeImages(entries, collector) {
  const usersByHash = new Map();
  for (const entry of entries) {
    if (entry.pictureAncestor) continue;
    for (const hash of imageHashesOf(entry.node)) {
      if (!usersByHash.has(hash)) usersByHash.set(hash, []);
      usersByHash.get(hash).push(entry);
    }
  }
  const hashes = Array.from(usersByHash.keys());
  const sizes = await Promise.all(hashes.map(imageSizeOf));
  hashes.forEach((hash, index) => {
    const size = sizes[index];
    if (!size || (size.width <= MAX_TEXTURE_SIDE && size.height <= MAX_TEXTURE_SIDE)) return;
    collector.add('warning', 'largeImage', usersByHash.get(hash), items =>
      describeLayers(items) + ' ' + verbFor(items, 'uses', 'use') + ' an image that is ' + size.width + ' \u00d7 ' + size.height +
      '. Unity textures can be at most ' + MAX_TEXTURE_SIDE + ' on a side, so it will be scaled down and may look blurry.');
  });
}

function checkComponentSets(entries, collector) {
  for (const set of entries) {
    if (set.type !== 'COMPONENT_SET') continue;
    const variants = set.children.filter(child => child.type === 'COMPONENT');
    for (const variant of variants) {
      if (isVariantName(variant.name)) continue;
      collector.add('warning', 'variantName', [variant], items =>
        'Variant ' + quote(items[0].cleanName) + ' in ' + quote(set.cleanName) +
        ' should be named like "State=Hover" so Unity can tell the states apart.');
    }
    if (variants.length < 2) continue;
    const reference = variants.find(variant => DEFAULT_STATE_PATTERN.test(variant.name)) || variants[0];
    const referenceNames = reference.children.map(child => child.cleanName);
    for (const variant of variants) {
      if (variant === reference) continue;
      const difference = compareLayerNames(referenceNames, variant.children.map(child => child.cleanName));
      if (difference.missing.length === 0 && difference.extra.length === 0) continue;
      const details = [];
      if (difference.missing.length > 0) details.push('missing ' + listNames(difference.missing, 3));
      if (difference.extra.length > 0) details.push('extra ' + listNames(difference.extra, 3));
      collector.add('warning', 'variantLayers', [variant], items =>
        'Variant ' + quote(items[0].cleanName) + ' of ' + quote(set.cleanName) + ' has different layers than ' +
        quote(reference.cleanName) + ' (' + details.join('; ') + '). Unity matches variant layers by name, so keep them the same.');
    }
  }
}

function activeReactions(node) {
  try {
    const reactions = node.reactions;
    if (!Array.isArray(reactions)) return [];
    return reactions.filter(reaction => (reaction.actions && reaction.actions.length > 0) || reaction.action);
  } catch (error) {
    return [];
  }
}

function checkComponentReactions(entries, collector) {
  for (const entry of entries) {
    if (!entry.definition || entry.type === 'COMPONENT' || entry.type === 'COMPONENT_SET') continue;
    if (activeReactions(entry.node).length === 0) continue;
    collector.add('warning', 'componentReaction', [entry], items =>
      quote(items[0].cleanName) + ' has prototype interactions inside the component ' + quote(definitionLabel(items[0].definition)) +
      '. Unity can\'t wire those up per instance, so add the interaction to the instance on the screen instead.');
  }
}

function checkUnsupportedLayouts(entries, collector) {
  for (const entry of entries) {
    const node = entry.node;
    if (!('layoutMode' in node)) continue;
    let kind = null;
    if (node.layoutMode === 'GRID') kind = 'grid auto layout';
    else if (node.layoutMode && node.layoutMode !== 'NONE' && node.layoutWrap === 'WRAP') kind = 'wrapping auto layout';
    if (!kind) continue;
    collector.add('warning', 'autoLayoutWrap', [entry], items =>
      quote(items[0].cleanName) + ' uses ' + kind + ', which Unity doesn\'t support yet. Its layers keep their current positions but won\'t rearrange.');
  }
}

function checkTextInPictures(entries, collector) {
  const groups = groupBy(entries.filter(entry => entry.type === 'TEXT' && entry.pictureAncestor), entry => entry.pictureAncestor.id);
  groups.forEach(group => {
    const picture = group[0].pictureAncestor;
    collector.add('warning', 'textInPicture', group, items =>
      describeLayers(items, 'text') + ' ' + verbFor(items, 'is', 'are') + ' inside ' + quote(picture.cleanName) +
      ', which becomes a picture, so ' + verbFor(items, 'it', 'they') + ' won\'t be editable text in Unity.');
  });
}

function checkTextStyles(entries, collector) {
  for (const entry of entries) {
    if (entry.type !== 'TEXT' || !entry.textProblems || entry.textProblems.length === 0) continue;
    collector.add('warning', 'textStyle', [entry], items =>
      'Text ' + quote(items[0].cleanName) + ' has ' + items[0].textProblems.join(', ') +
      '. Text is never turned into a picture, so Unity shows it without that look.');
  }
}

function checkBlendModes(entries, collector) {
  for (const entry of entries) {
    if (entry.blendSelf && entry.isPicture) {
      collector.add('warning', 'blendSelf', [entry], items =>
        quote(items[0].cleanName) + ' uses blend mode ' + items[0].blendSelf + ' directly inside ' +
        quote(items[0].parent ? items[0].parent.cleanName : 'the screen') +
        '. It becomes a picture on its own, so the blend may look different in Unity.');
    } else if (entry.blendUnsupported && !entry.pictureAncestor) {
      collector.add('warning', 'blendUnsupported', [entry], items =>
        quote(items[0].cleanName) + ' uses blend mode ' + items[0].blendUnsupported +
        ', which Unity can\'t show here. Put it inside a group so the group can become a picture.');
    }
  }
}

function checkFlattenedInfo(entries, collector) {
  for (const entry of entries) {
    if (!entry.isPicture || !entry.flatten || !entry.flatten.auto) continue;
    collector.add('info', 'flattened', [entry], items =>
      quote(items[0].cleanName) + ' becomes a picture in Unity: ' + items[0].flatten.reasons.join(', ') + '.');
  }
}

function checkLibraryComponents(entries, collector) {
  const groups = groupBy(entries.filter(entry => entry.type === 'INSTANCE'), entry => {
    const info = entry.instanceInfo;
    if (!info) return 'missing:' + entry.id;
    return info.remote ? 'remote:' + info.root.id : null;
  });
  groups.forEach(group => {
    const info = group[0].instanceInfo;
    if (!info) {
      collector.add('info', 'libraryComponent', group, items =>
        quote(items[0].cleanName) + ' has lost its main component, so Unity builds it inline as plain layers.');
      return;
    }
    collector.add('info', 'libraryComponent', group, items =>
      'Library component ' + quote(cleanLayerName(info.name)) + ' comes from another file, so Unity builds it inline (' +
      plural(items.length, 'use') + ') instead of using a prefab.');
  });
}

async function collectChecks(analysis) {
  const collector = createRowCollector();
  const entries = analysis.entries;
  const outside = entries.filter(entry => !entry.insideInstance);
  const outsidePictures = outside.filter(entry => !entry.pictureAncestor);
  checkDuplicateScreens(analysis.screenEntries, collector);
  await checkMissingFonts(entries, collector);
  checkUnknownFonts(entries, collector);
  checkDesignResolution(analysis.screenEntries, collector);
  checkDuplicateSiblings(outsidePictures, collector);
  checkDefaultNames(outsidePictures, collector);
  await checkLargeImages(entries, collector);
  checkComponentSets(outside, collector);
  checkComponentReactions(outsidePictures, collector);
  checkUnsupportedLayouts(outsidePictures, collector);
  checkTextInPictures(outside, collector);
  checkTextStyles(outsidePictures, collector);
  checkBlendModes(outside, collector);
  checkFlattenedInfo(outside, collector);
  checkLibraryComponents(outside, collector);
  return collector.result();
}

function pictureReasonOf(entry) {
  if (entry.flatten) return entry.flatten.reasons.join(', ');
  if (hasNineSlice(entry.settings)) return 'nine-slice';
  if (hasRenderWord(entry.name)) return '"render" in its name';
  return 'role image';
}

function rememberPictureInfo(analysis) {
  const info = new Map();
  for (const entry of analysis.entries) {
    if (entry.isPicture) info.set(entry.id, { kind: 'self', reason: pictureReasonOf(entry) });
    else if (entry.pictureAncestor) info.set(entry.id, { kind: 'inside', name: entry.pictureAncestor.cleanName });
  }
  state.pictureInfoById = info;
}

async function analyzeScope() {
  const scope = await resolveScope();
  const mainByInstanceId = await resolveMainComponents(scope.screens);
  const analysis = {
    scope,
    mainByInstanceId,
    entries: [],
    screenEntries: [],
    liveById: new Map(),
    ignoredIds: new Set(),
    walkedIds: new Set(),
    queuedDefinitionIds: new Set(),
    definitionQueue: [],
    definitionRoots: [],
    bridgeById: new Map(),
    rows: [],
    ignoredRows: [],
  };
  for (const screen of scope.screens) {
    const entry = walkTree(screen, null, OUTSIDE_CONTEXT, analysis, true);
    if (entry) analysis.screenEntries.push(entry);
  }
  while (analysis.definitionQueue.length > 0) {
    const root = analysis.definitionQueue.shift();
    if (analysis.walkedIds.has(root.id) || analysis.ignoredIds.has(root.id)) continue;
    const entry = walkTree(root, null, OUTSIDE_CONTEXT, analysis, false);
    if (entry) analysis.definitionRoots.push(root);
  }
  for (const entry of analysis.entries) {
    if (!entry.insideLocalInstance) entry.style = describeNode(entry.node);
  }
  decideFlattening(analysis.entries);
  for (const entry of analysis.entries) {
    const bridge = bridgeObjectFor(entry.settings, entry.isScreen ? null : entry.flatten);
    if (bridge) analysis.bridgeById.set(entry.id, bridge);
  }
  const checks = await collectChecks(analysis);
  analysis.rows = checks.rows;
  analysis.ignoredRows = checks.ignoredRows;
  rememberPictureInfo(analysis);
  return analysis;
}

function preflightResult(analysis) {
  const counts = { errors: 0, warnings: 0, info: 0, ignored: analysis.ignoredRows.length };
  for (const row of analysis.rows) {
    if (row.severity === 'error') counts.errors++;
    else if (row.severity === 'warning') counts.warnings++;
    else counts.info++;
  }
  return {
    scope: summarizeScope(analysis.scope),
    rows: analysis.rows,
    ignoredRows: analysis.ignoredRows,
    counts,
  };
}

function createCanvas(id, name, flowStartingPoints) {
  return { id, name, type: 'CANVAS', children: [], flowStartingPoints, backgroundColor: CANVAS_BACKGROUND };
}

function flowStartsFor(page, screens) {
  try {
    const screenIds = new Set(screens.map(screen => screen.id));
    return (page.flowStartingPoints || [])
      .filter(start => screenIds.has(start.nodeId))
      .map(start => ({ nodeId: start.nodeId, name: start.name }));
  } catch (error) {
    return [];
  }
}

function copyTransform(transform) {
  return [
    [transform[0][0], transform[0][1], transform[0][2]],
    [transform[1][0], transform[1][1], transform[1][2]],
  ];
}

function copyLiveGeometry(restNode, liveNode) {
  if (!restNode.relativeTransform && 'relativeTransform' in liveNode && liveNode.relativeTransform) {
    restNode.relativeTransform = copyTransform(liveNode.relativeTransform);
  }
  if (!restNode.size && 'width' in liveNode && 'height' in liveNode) {
    restNode.size = { x: liveNode.width, y: liveNode.height };
  }
}

function decorateRestNode(restNode, analysis, missingLive) {
  const liveNode = analysis.liveById.get(restNode.id);
  if (liveNode) copyLiveGeometry(restNode, liveNode);
  else missingLive.push(restNode);
  const bridge = analysis.bridgeById.get(restNode.id);
  if (bridge) restNode.figmaBridge = Object.assign({}, restNode.figmaBridge || {}, bridge);
  if (Array.isArray(restNode.children)) {
    restNode.children = restNode.children.filter(child => !analysis.ignoredIds.has(child.id));
    for (const child of restNode.children) decorateRestNode(child, analysis, missingLive);
  }
}

async function fillGeometryFromLookups(restNodes) {
  const liveNodes = await Promise.all(restNodes.map(restNode => figma.getNodeByIdAsync(restNode.id).catch(() => null)));
  restNodes.forEach((restNode, index) => {
    if (liveNodes[index]) copyLiveGeometry(restNode, liveNodes[index]);
  });
}

async function exportRestTree(liveRoot, file, analysis) {
  const result = await liveRoot.exportAsync({ format: 'JSON_REST_V1' });
  Object.assign(file.components, result.components || {});
  Object.assign(file.componentSets, result.componentSets || {});
  Object.assign(file.styles, result.styles || {});
  const missingLive = [];
  decorateRestNode(result.document, analysis, missingLive);
  if (missingLive.length > 0) await fillGeometryFromLookups(missingLive);
  return result.document;
}

function createVariableResolver(collections, variables) {
  const variableCache = new Map(variables.map(variable => [variable.id, variable]));
  const collectionCache = new Map(collections.map(collection => [collection.id, collection]));

  async function getVariable(id) {
    if (!variableCache.has(id)) {
      variableCache.set(id, await figma.variables.getVariableByIdAsync(id).catch(() => null));
    }
    return variableCache.get(id);
  }

  async function getCollection(id) {
    if (!collectionCache.has(id)) {
      collectionCache.set(id, await figma.variables.getVariableCollectionByIdAsync(id).catch(() => null));
    }
    return collectionCache.get(id);
  }

  async function resolve(value, modeName, depth) {
    if (!isVariableAlias(value)) return plainTokenValue(value);
    if (depth >= MAX_ALIAS_DEPTH) return null;
    const target = await getVariable(value.id);
    if (!target || !target.valuesByMode) return null;
    const collection = await getCollection(target.variableCollectionId);
    let modeId = null;
    if (collection) {
      const sameName = collection.modes.find(mode => mode.name === modeName);
      modeId = sameName ? sameName.modeId : collection.defaultModeId;
    }
    if (!modeId || !(modeId in target.valuesByMode)) modeId = Object.keys(target.valuesByMode)[0];
    if (!modeId) return null;
    return resolve(target.valuesByMode[modeId], modeName, depth + 1);
  }

  return { resolve };
}

async function collectVariableTokens() {
  if (!figma.variables) return [];
  const collections = await figma.variables.getLocalVariableCollectionsAsync();
  const variables = await figma.variables.getLocalVariablesAsync();
  const collectionById = new Map(collections.map(collection => [collection.id, collection]));
  const resolver = createVariableResolver(collections, variables);
  const tokens = [];
  for (const variable of variables) {
    const collection = collectionById.get(variable.variableCollectionId);
    if (!collection) continue;
    const valuesByMode = {};
    for (const mode of collection.modes) {
      valuesByMode[mode.name] = await resolver.resolve(variable.valuesByMode[mode.modeId], mode.name, 0);
    }
    tokens.push({ collection: collection.name, name: variable.name, type: variable.resolvedType, valuesByMode });
  }
  return tokens;
}

async function collectPaintStyleTokens() {
  const styles = await figma.getLocalPaintStylesAsync();
  const tokens = [];
  for (const style of styles) {
    const paints = style.paints || [];
    if (paints.length !== 1) continue;
    const paint = paints[0];
    if (paint.type !== 'SOLID' || paint.visible === false) continue;
    tokens.push({
      name: style.name,
      color: { r: paint.color.r, g: paint.color.g, b: paint.color.b, a: paint.opacity === undefined ? 1 : paint.opacity },
    });
  }
  return tokens;
}

async function collectTextStyleTokens() {
  const styles = await figma.getLocalTextStylesAsync();
  return styles.map(style => {
    const token = {
      name: style.name,
      fontFamily: style.fontName.family,
      fontStyle: style.fontName.style,
      fontSize: style.fontSize,
      letterSpacing: letterSpacingPx(style.letterSpacing, style.fontSize),
    };
    const lineHeight = lineHeightPx(style.lineHeight, style.fontSize);
    if (lineHeight !== null) token.lineHeightPx = lineHeight;
    return token;
  });
}

async function buildDesignTokens(warnings) {
  const tokens = { variables: [], paintStyles: [], textStyles: [] };
  const parts = [
    ['variables', collectVariableTokens, 'variables'],
    ['paintStyles', collectPaintStyleTokens, 'color styles'],
    ['textStyles', collectTextStyleTokens, 'text styles'],
  ];
  for (const part of parts) {
    try {
      tokens[part[0]] = await part[1]();
    } catch (error) {
      warnings.push({ message: 'Couldn\'t read the file\'s ' + part[2] + ': ' + errorText(error) });
    }
  }
  return tokens;
}

async function handleBuildExport(params) {
  const purpose = params.purpose === 'bundle' ? 'bundle' : 'unity';
  postProgress('Checking the design\u2026');
  const analysis = await analyzeScope();
  const preflight = preflightResult(analysis);
  postLayerInfo();
  if (preflight.counts.errors > 0) return { blocked: true, preflight };
  const scope = analysis.scope;
  if (scope.screens.length === 0) throw new Error('There are no top-level frames to export on this page.');

  const warnings = [];
  const file = {
    name: figma.root.name,
    schemaVersion: 0,
    pluginVersion: PLUGIN_VERSION,
    partialExport: scope.partialExport,
    components: {},
    componentSets: {},
    styles: {},
    designTokens: {},
    document: { id: '0:0', type: 'DOCUMENT', name: 'Document', children: [] },
  };
  const screenCanvas = createCanvas(scope.page.id, scope.page.name, flowStartsFor(scope.page, scope.screens));
  const componentCanvas = createCanvas(COMPONENTS_CANVAS_ID, 'Components', []);

  for (let index = 0; index < scope.screens.length; index++) {
    const screen = scope.screens[index];
    postProgress('Reading screen ' + (index + 1) + ' of ' + scope.screens.length + ': ' + screen.name);
    screenCanvas.children.push(await exportRestTree(screen, file, analysis));
  }
  for (let index = 0; index < analysis.definitionRoots.length; index++) {
    const root = analysis.definitionRoots[index];
    postProgress('Reading component ' + (index + 1) + ' of ' + analysis.definitionRoots.length + ': ' + root.name);
    await loadPageOf(root);
    componentCanvas.children.push(await exportRestTree(root, file, analysis));
  }
  postProgress('Reading colors and text styles\u2026');
  file.designTokens = await buildDesignTokens(warnings);
  file.document.children.push(screenCanvas, componentCanvas);

  const result = {
    blocked: false,
    preflight,
    json: JSON.stringify(file),
    pageName: scope.page.name,
    partialExport: scope.partialExport,
    screenNames: scope.screens.map(screen => screen.name),
    warnings,
  };
  if (purpose === 'bundle') {
    result.images = collectImageRefs(file.document);
    result.renders = collectBundleRenders(file.document);
  }
  return result;
}

async function handleInit() {
  const scope = await resolveScope();
  return {
    pluginVersion: PLUGIN_VERSION,
    fileName: figma.root.name,
    pages: listPages(),
    currentPageId: figma.currentPage.id,
    scope: summarizeScope(scope),
    selection: selectionMessage(false),
  };
}

async function handleSetExportPage(params) {
  const page = findPage(String(params.pageId || ''));
  if (!page) throw new Error('That page no longer exists.');
  await page.loadAsync();
  state.exportPageId = page.id;
  state.scopeFrameIds = page.id === figma.currentPage.id ? userSelectedScreenIds(page) : null;
  return summarizeScope(await resolveScope());
}

async function handlePreflight() {
  const analysis = await analyzeScope();
  postLayerInfo();
  return preflightResult(analysis);
}

function handleSetUnityInfo(params) {
  state.unityInfo = {
    fonts: Array.isArray(params.fonts) ? params.fonts.filter(font => font && font.family) : null,
    googleFonts: params.googleFonts === true,
    designResolution: params.designResolution && params.designResolution.width > 0 && params.designResolution.height > 0
      ? { width: Number(params.designResolution.width), height: Number(params.designResolution.height) }
      : null,
  };
  return null;
}

async function handleImageBytes(params) {
  let image = null;
  try {
    image = figma.getImageByHash(String(params.hash || ''));
  } catch (error) {
    image = null;
  }
  if (!image) return { bytes: null };
  return { bytes: await image.getBytesAsync() };
}

async function handleRenderBytes(params) {
  const node = await figma.getNodeByIdAsync(String(params.nodeId || ''));
  if (!node || !('exportAsync' in node)) throw new Error('This layer isn\'t in the file anymore.');
  await loadPageOf(node);
  const scale = Number(params.scale) > 0 ? Number(params.scale) : 1;
  const bytes = await node.exportAsync({ format: 'PNG', constraint: { type: 'SCALE', value: scale }, colorProfile: 'SRGB' });
  return { bytes };
}

async function handleSelectNodes(params) {
  const ids = Array.isArray(params.nodeIds) ? params.nodeIds.map(String) : [];
  const found = await Promise.all(ids.map(id => figma.getNodeByIdAsync(id).catch(() => null)));
  const nodes = found.filter(node => node && node.type !== 'PAGE' && node.type !== 'DOCUMENT');
  if (nodes.length === 0) {
    figma.notify('That layer isn\'t in this file anymore.');
    return { selected: 0 };
  }
  const page = pageOf(nodes[0]);
  if (!page) return { selected: 0 };
  const onPage = nodes.filter(node => {
    const nodePage = pageOf(node);
    return !!nodePage && nodePage.id === page.id;
  });
  if (figma.currentPage.id !== page.id) {
    state.navigationPage = { pageId: page.id, until: Date.now() + NAVIGATION_PAGE_WINDOW_MS };
    await figma.setCurrentPageAsync(page);
  }
  state.navigationSelectionKey = selectionKey(onPage);
  figma.currentPage.selection = onPage;
  figma.viewport.scrollAndZoomIntoView(onPage);
  return { selected: onPage.length };
}

async function handleSetIgnoredCheck(params) {
  const check = String(params.check || '');
  if (!check) throw new Error('No check was given.');
  const ids = Array.isArray(params.nodeIds) ? params.nodeIds.map(String) : [];
  const nodes = await Promise.all(ids.map(id => figma.getNodeByIdAsync(id).catch(() => null)));
  let failed = 0;
  for (const node of nodes) {
    if (!node) {
      failed++;
      continue;
    }
    const checks = readIgnoredChecks(node);
    if (params.ignored === false) checks.delete(check);
    else checks.add(check);
    try {
      node.setSharedPluginData(PLUGIN_DATA_NAMESPACE, IGNORED_CHECKS_KEY, checks.size > 0 ? JSON.stringify(Array.from(checks).sort()) : '');
    } catch (error) {
      failed++;
    }
  }
  if (failed > 0) figma.notify('Couldn\'t update ' + plural(failed, 'layer') + '. Library layers can\'t be changed here.', { error: true });
  return { failed };
}

async function handleSaveSettings(params) {
  const node = await figma.getNodeByIdAsync(String(params.nodeId || ''));
  if (!node || node.type === 'PAGE' || node.type === 'DOCUMENT') throw new Error('That layer isn\'t in this file anymore.');
  const settings = normalizeSettings(params.settings, node.type === 'TEXT');
  try {
    node.setSharedPluginData(PLUGIN_DATA_NAMESPACE, SETTINGS_KEY, settingsForStorage(settings));
  } catch (error) {
    throw new Error('This layer can\'t be changed here (' + errorText(error) + ').');
  }
  return describeLayer(node);
}

async function handleStorageGet(params) {
  const value = await figma.clientStorage.getAsync(String(params.key || ''));
  return value === undefined ? null : value;
}

async function handleStorageSet(params) {
  const key = String(params.key || '');
  if (!key) throw new Error('No storage key was given.');
  if (params.value === null || params.value === undefined) await figma.clientStorage.deleteAsync(key);
  else await figma.clientStorage.setAsync(key, params.value);
  return null;
}

function handleNotify(params) {
  const isError = params.error === true;
  figma.notify(String(params.message || ''), { error: isError, timeout: isError ? 6000 : 3000 });
  return null;
}

function handleResize(params) {
  const height = Math.round(Number(params.height) || UI_START_HEIGHT);
  figma.ui.resize(UI_WIDTH, Math.min(UI_MAX_HEIGHT, Math.max(UI_MIN_HEIGHT, height)));
  return null;
}

const REQUEST_HANDLERS = {
  init: handleInit,
  storageGet: handleStorageGet,
  storageSet: handleStorageSet,
  setUnityInfo: handleSetUnityInfo,
  setExportPage: handleSetExportPage,
  preflight: handlePreflight,
  buildExport: handleBuildExport,
  imageBytes: handleImageBytes,
  renderBytes: handleRenderBytes,
  selectNodes: handleSelectNodes,
  setIgnoredCheck: handleSetIgnoredCheck,
  saveSettings: handleSaveSettings,
  notify: handleNotify,
  resize: handleResize,
};

function handleUiMessage(message) {
  if (!message || message.type !== 'request') return;
  const handler = REQUEST_HANDLERS[message.method];
  const reply = (ok, payload) => figma.ui.postMessage(Object.assign({ type: 'reply', requestId: message.requestId, ok }, payload));
  Promise.resolve()
    .then(() => {
      if (!handler) throw new Error('Unknown request ' + message.method);
      return handler(message.params || {});
    })
    .then(result => reply(true, { result: result === undefined ? null : result }))
    .catch(error => reply(false, { error: errorText(error) }));
}

figma.showUI(__html__, { width: UI_WIDTH, height: UI_START_HEIGHT, themeColors: true });
state.exportPageId = figma.currentPage.id;
state.scopeFrameIds = userSelectedScreenIds(figma.currentPage);
figma.ui.onmessage = handleUiMessage;
figma.on('selectionchange', handleSelectionChange);
figma.on('currentpagechange', handlePageChange);
