// The page runs under a policy that refuses `eval` and `new Function`. The schema library the board tools use probes for them to decide whether it
// may compile its parsers, and a probe the policy refuses is reported as a violation even though the library catches it. Telling it not to compile
// skips the probe. This module is imported first, so the setting is there before any schema is made.
globalThis.__zod_globalConfig = { ...globalThis.__zod_globalConfig, jitless: true };
