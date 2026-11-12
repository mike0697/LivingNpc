// The commands of the script language, one entry per JSON form the game reads.
// Names are the JSON names ("do" of an effect, "type" of a condition, the key of a script step), so a command and its
// JSON are the same thing written two ways. The same table drives the converter (lib/script.js) and the buttons of
// the interface (public/app.js).
//
// A parameter: { name: JSON key, type, label, pos: written without its name (in order), req: required }.
// Types: text (a sentence, localizable), id:<kind> (an id of the world: npc, node, item, quest, stage, link, stat,
// activity, flag, target, goal, template, tstage = a stage of the template, lines), choice:<a|b|c>, number, time ("HH:mm"),
// file:<images|sfx|music>, op (comparison), json. In an automatic quest (template) a character, place or item can also be
// "self" (the npc that runs it), a slot of the template, or "it" (the slot being filled).

const P = (name, type, label, extra = {}) => ({ name, type, label, ...extra });
const pos = { pos: true }, req = { pos: true, req: true };

const target = P('target', 'id:target', 'Whose (empty = player)');
const volume = P('volume', 'number', 'Volume (0-1)');
const fade = P('fade', 'number', 'Fade (seconds)');
const layer = P('layer', 'number', 'Layer (40 = above everything)');
const place = [P('x', 'number', 'X (%)'), P('y', 'number', 'Y (%)'), P('width', 'number', 'Width (%)'), P('height', 'number', 'Height (%)'), layer];

/** Effects: { "do": name, ... }. Usable everywhere a list of commands is (events, quest onStart / onComplete / stage onComplete / onFail). */
export const EFFECTS = {
  message: { cat: 'Dialogue', label: 'Message in the log', params: [P('text', 'text', 'Text', req)] },
  giveItem: { cat: 'Items', label: 'Give item', params: [P('item', 'id:item', 'Item', req), P('count', 'number', 'Amount', pos), target] },
  takeItem: { cat: 'Items', label: 'Take item', params: [P('item', 'id:item', 'Item', req), P('count', 'number', 'Amount', pos), target] },
  startQuest: { cat: 'Quest', label: 'Start quest', params: [P('quest', 'id:quest', 'Quest', req)] },
  advanceQuest: { cat: 'Quest', label: 'Advance quest (next stage)', params: [P('quest', 'id:quest', 'Quest', req)] },
  completeQuest: { cat: 'Quest', label: 'Complete quest', params: [P('quest', 'id:quest', 'Quest', req)] },
  failQuest: { cat: 'Quest', label: 'Fail quest', params: [P('quest', 'id:quest', 'Quest', req)] },
  setFlag: { cat: 'Variables', label: 'Set flag', params: [P('key', 'id:flag', 'Flag', req), P('value', 'number', 'Value', pos)] },
  addFlag: { cat: 'Variables', label: 'Add to flag', params: [P('key', 'id:flag', 'Flag', req), P('value', 'number', 'How much', pos)] },
  modStat: { cat: 'Variables', label: 'Change stat (+/-)', params: [P('stat', 'id:stat', 'Stat', req), P('delta', 'number', 'By how much', req), target] },
  setStat: { cat: 'Variables', label: 'Set stat', params: [P('stat', 'id:stat', 'Stat', req), P('value', 'number', 'Value', req), target] },
  showImage: { cat: 'Images', label: 'Image on the map (stays)', params: [P('image', 'text', 'Name', req), P('file', 'file:images', 'File', req), ...place] },
  hideImage: { cat: 'Images', label: 'Remove image from the map', params: [P('image', 'text', 'Name', req)] },
  playSound: { cat: 'Sounds', label: 'Sound', params: [P('file', 'file:sfx', 'File', req), volume] },
  startMusic: { cat: 'Sounds', label: 'Change music', params: [P('file', 'file:music', 'File', req), volume, fade] },
  stopMusic: { cat: 'Sounds', label: 'Stop music', params: [] },
  startAmbient: { cat: 'Sounds', label: 'Ambient sound (looping)', params: [P('sound', 'text', 'Name', req), P('file', 'file:sfx', 'File', req), volume, fade] },
  stopAmbient: { cat: 'Sounds', label: 'Stop ambient sound', params: [P('sound', 'text', 'Name', req)] },
  teleportPlayer: { cat: 'World', label: 'Move the player', params: [P('node', 'id:node', 'Place', req)] },
  advanceTime: { cat: 'World', label: 'Let time pass', params: [P('minutes', 'number', 'Minutes', req)] },
  advanceTimeTo: { cat: 'World', label: 'Let time pass until', params: [P('time', 'time', 'Time (HH:mm)', req)] },
  showLink: { cat: 'World', label: 'Show link', params: [P('link', 'id:link', 'Link', req)] },
  hideLink: { cat: 'World', label: 'Hide link', params: [P('link', 'id:link', 'Link', req)] },
  openLink: { cat: 'World', label: 'Open link', params: [P('link', 'id:link', 'Link', req)] },
  closeLink: { cat: 'World', label: 'Close link', params: [P('link', 'id:link', 'Link', req)] },
  moveNpc: { cat: 'Characters', label: 'Send a character to a place', params: [P('npc', 'id:npc', 'Character', req), P('node', 'id:node', 'Place', req), P('forMinutes', 'number', 'For how many minutes')] },
  setPlace: { cat: 'Characters', label: 'Change a place of the character', params: [P('npc', 'id:npc', 'Character', req), P('place', 'text', 'Their place (home, work…)', req), P('node', 'id:node', 'Becomes', req)] },
  addRoutine: { cat: 'Characters', label: 'Add to the routine', params: [P('npc', 'id:npc', 'Character', req), P('id', 'text', 'Block name'), P('block', 'json', 'Block (JSON)')] },
  setRoutine: { cat: 'Characters', label: 'Replace the routine', params: [P('npc', 'id:npc', 'Character', req), P('routine', 'json', 'Routine (JSON)', req)] },
  removeRoutine: { cat: 'Characters', label: 'Remove from the routine', params: [P('npc', 'id:npc', 'Character', req), P('id', 'text', 'Block name', req)] },
  modRelation: { cat: 'Characters', label: 'Change a relation (+/-)', params: [P('from', 'id:target', 'Who', req), P('to', 'id:target', 'Towards whom', req), P('delta', 'number', 'By how much', req)] },
  setRelation: { cat: 'Characters', label: 'Set a relation', params: [P('from', 'id:target', 'Who', req), P('to', 'id:target', 'Towards whom', req), P('value', 'number', 'Value (-100 to 100)', req)] },
  setGoal: { cat: 'Automatic quests', label: 'Change the goal of a character', params: [P('npc', 'id:npc', 'Character', req), P('goal', 'id:goal', 'Goal (empty = none)', pos)] },
  setAutoQuests: { cat: 'Automatic quests', label: 'Automatic quests on / off', params: [P('npc', 'id:npc', 'Character', req), P('on', 'choice:true|false', 'On', pos)] },
  startAuto: { cat: 'Automatic quests', label: 'Start an automatic quest', params: [P('npc', 'id:npc', 'Character', req), P('template', 'id:template', 'Automatic quest', req)] },
  stopAuto: { cat: 'Automatic quests', label: 'Stop the automatic quest', params: [P('npc', 'id:npc', 'Character', req)] },
  tell: { cat: 'Automatic quests', label: 'A character says a line (in the log)', params: [P('lines', 'id:lines', 'Lines (a text group)', req), P('npc', 'id:npc', 'Who says it (empty = self)'), P('everywhere', 'choice:true|false', 'Also when the player is not there')] },
};

/** Script steps: only inside an event (they need the dialogue). JSON: the step's own key holds the first parameter. */
export const STEPS = {
  say: { cat: 'Dialogue', label: 'Line', params: [P('say', 'text', 'Text', req), P('who', 'id:npc', 'Who speaks'), P('pose', 'text', 'Pose (happy, angry…)')] },
  show: { cat: 'Images', label: 'Show image (in the dialogue)', params: [P('show', 'text', 'Name', req), P('file', 'file:images', 'File', req), ...place] },
  hide: { cat: 'Images', label: 'Hide image (in the dialogue)', params: [P('hide', 'text', 'Name', req)] },
  sound: { cat: 'Sounds', label: 'Sound (in the dialogue)', params: [P('sound', 'file:sfx', 'File', req), volume] },
};

/** Conditions: { "type": name, ... }, plus the groups all / any / not. */
export const CONDITIONS = {
  always: { label: 'Always', params: [] },
  playerAt: { label: 'The player is at', params: [P('node', 'id:node', 'Place', req)] },
  npcAt: { label: 'A character is at', params: [P('npc', 'id:npc', 'Character', req), P('node', 'id:node', 'Place (empty = with the player)', pos)] },
  npcWithPlayer: { label: 'A character is with the player', params: [P('npc', 'id:npc', 'Character', req)] },
  activity: { label: 'A character is doing', params: [P('npc', 'id:npc', 'Character', req), P('activity', 'id:activity', 'Activity', req)] },
  quest: { label: 'State of a quest', params: [P('quest', 'id:quest', 'Quest', req), P('status', 'choice:notStarted|active|completed|failed', 'State', pos), P('stage', 'id:stage', 'Stage', pos)] },
  elapsed: { label: 'Time has passed (quest)', params: [P('quest', 'id:quest', 'Quest'), P('since', 'choice:stage|quest', 'Since'), P('days', 'number', 'Days'), P('hours', 'number', 'Hours'), P('minutes', 'number', 'Minutes')] },
  flag: { label: 'Value of a flag', params: [P('key', 'id:flag', 'Flag', req), P('op', 'op', 'Comparison', pos), P('value', 'number', 'Value', pos)] },
  stat: { label: 'Value of a stat', params: [P('stat', 'id:stat', 'Stat', req), P('op', 'op', 'Comparison', pos), P('value', 'number', 'Value', pos), target] },
  hasItem: { label: 'Has an item', params: [P('item', 'id:item', 'Item', req), P('op', 'op', 'Comparison', pos), P('count', 'number', 'Amount', pos), target] },
  time: { label: 'Time of day', params: [P('from', 'time', 'From', req), P('to', 'time', 'To', req)] },
  day: { label: 'Days of the week', params: [P('days', 'json', 'Days (e.g. ["mon","tue"])', req)] },
  date: { label: 'Days of the game', params: [P('fromDay', 'number', 'From day (0 = first)'), P('toDay', 'number', 'To day')] },
  after: { label: 'After a given moment', params: [P('day', 'number', 'Day (0 = first)', req), P('time', 'time', 'Time', pos)] },
  relation: { label: 'Relation between two characters', params: [P('from', 'id:target', 'Who', req), P('to', 'id:target', 'Towards whom', req), P('op', 'op', 'Comparison', pos), P('value', 'number', 'Value', pos)] },
  goal: { label: 'A character has a goal', params: [P('npc', 'id:npc', 'Character', req), P('goal', 'id:goal', 'Goal (empty = any)', pos)] },
  age: { label: 'Age of a character', params: [P('npc', 'id:npc', 'Character', req), P('op', 'op', 'Comparison', pos), P('value', 'number', 'Years', pos)] },
  sex: { label: 'Sex of a character', params: [P('npc', 'id:npc', 'Character', req), P('sex', 'choice:m|f|x', 'Sex', req)] },
  chance: { label: 'Chance (automatic quest stage)', params: [P('percent', 'number', 'Percent', req)] },
  doing: { label: 'A character is doing an automatic quest', params: [P('npc', 'id:npc', 'Character', req), P('template', 'id:template', 'Automatic quest (empty = any)', pos), P('stage', 'id:tstage', 'Stage', pos)] },
  busy: { label: 'A character is busy (automatic quest, or gives a busyGiver quest)', params: [P('npc', 'id:npc', 'Character', req)] },
  did: { label: 'A character did an automatic quest', params: [P('npc', 'id:npc', 'Character', req), P('template', 'id:template', 'Automatic quest (empty = any)', pos), P('status', 'choice:completed|failed|stopped', 'How it ended', pos), P('days', 'number', 'In the last days (empty = ever)')] },
};

export const GROUPS = ['all', 'any', 'not'];
export const TRIGGERS = ['enterNode', 'talk', 'tick', 'wake', 'button', 'action'];
export const OPS = ['>=', '>', '==', '!=', '<', '<='];

/** The spec for a name, ignoring case (the game reads "do" and "type" without case). */
export function lookup(table, name) {
  if (table[name]) return [name, table[name]];
  const lower = String(name).toLowerCase();
  for (const key of Object.keys(table)) if (key.toLowerCase() === lower) return [key, table[key]];
  return [null, null];
}
