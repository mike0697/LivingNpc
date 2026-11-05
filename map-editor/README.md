# LivingNPC Map Editor

Draw the places of a world and the links between them. Every place is a background picture with buttons (links) to
other places; the editor writes the `project.json` and the `nodes/<id>/node.json` files the game reads, straight to
`../worlds/<world>/base`.

## Starting it

Double-click `start.bat` (Windows), or:

    npm install
    npm run build
    npm start

and open http://localhost:5173. Node.js 20 or newer is needed. Pick a world with **Load World**; `start.bat aetherhall`
opens it right away. Set `LIVINGNPC_WORLDS` to use another worlds folder (for example a test copy) and `PORT` for
another port. The server only listens on 127.0.0.1.

## What it does

- **Graph**: every place is a card; links are arrows between them. Drag cards around, or use **Auto arrange**.
  **Set as start node** chooses where the player starts.
- **Scene**: the background of the selected place with its link buttons. Add a link and click on the picture to place
  it, then drag and resize it.
- **Place properties**: title, narrative text, the **Bed** tag (the player can sleep here) and the backgrounds per
  part of the day: day, afternoon, night, night with lights, plus custom variants.
- **Link properties**: destination, button label, position and size (in % of the picture), travel time in minutes,
  message when locked, and whether it is **visible** and **open** at the start (effects can change both during the
  game).
- **Try the map**: a playable preview of the world, moving from place to place as in the game, with a panel to try
  the link effects (show / hide, open / close) on the place you are in.
- **Save World** writes the files to disk. **Export ZIP** downloads a copy of the world.

Pictures are kept as they are uploaded; the editor makes small cached copies (`.cache/`) only for its own lists and
previews. Characters, quests, messages, items and rules are edited in [LivingNPC Studio](../studio/README.md).

## Development

    npm start          # the server, on port 5173 (serves dist/ and the worlds)
    npm run dev        # Vite with hot reload on port 5175, forwarding /api to the server
    npm test           # end-to-end test of the server on a temporary world

Files: `server.mjs` (the local server and every disk write), `src/` (the React interface: `App.jsx`,
`GraphEditor.jsx`, `SceneEditor.jsx`, `Inspector.jsx`, `Player.jsx`, `mapUtils.js`), `unity/` (a minimal standalone
Unity viewer for the map format, independent from the game).
