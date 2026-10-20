# Mission frame isolation at the existing scheduling boundary

Four change-boundary obligations, before native acceptance:

1. GameStateManagerPatches already forces a background MapState frame for ordinary overlay menus on
   non-Listen clients. Listen preserves native scheduling. Readiness changes are reported by its
   existing postfix immediately; the one-second interval is only an unchanged-readiness heartbeat.
2. With a MissionState on the stack, the same client branch still starts additional map frames until
   the room's freeze handshake completes. Filtering that existing scheduling entrance solves the
   concrete conflict without modifying native Campaign/AI or adding another readiness manager.
3. Preserve native active-state ticking, ordinary map/menu behavior, local input, readiness reporting,
   final old-lease result publication, and original WaitAsyncTasks drain. Do not Invoke an AI task or
   freeze permissions before already-started work can finish.
4. Test actual map frame dispatch: both modes with Mission stack receive zero extra map calls, an
   active map receives exactly its original native frame. Recheck actual dual-process Mission
   enter/leave, all-away pause, world Tick/AI counters and task completion under the final build.

This closes the extra background-frame entrance, not all possible Mission callbacks that change
campaign resources. Battle result submission and local Mission transport have separate contracts;
this managed test does not constitute native CPU/frame-time or complete Mission acceptance.
