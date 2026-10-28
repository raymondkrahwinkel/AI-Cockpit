# Room bundle pin (AC-1897)

`room.bundle.js` is `room.js` plus the yjs of AI-Depot `dev` @ `6fe38ee8` (`src/Depot.Api/ClientApp/notes`, `npm ci` from its lockfile),
built with `node build.mjs <that notes dir> room.bundle.js`.

sha256: `a58d3b383bc240df05d098a604c5b13b79e6984f096f8ce257ee44a9da0357e3`

Updating it is a deliberate step: change the commit and the sha together. AC-1899 replaces this with the full bridge pipeline.
