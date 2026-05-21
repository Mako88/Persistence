# Refactor TODO

## Done
- [x] 1. Remove MediatR dependency (stale using + doc comment in Initializer.cs)
- [x] 3. Remove duplicate AddActionResponse helpers (inlined — callers use context.AddFragment directly)
- [x] 3b. Renamed ModifyContext → ManageContext (enum, handler class, filename)
- [x] 4. Add doc comments to IAppConfig properties + removed 7 dead config properties + cleaned appsettings template
- [x] 8. Clean up Order on WeightedContextFragment (no longer required, set by AddFragment)

- [x] 10. Update Orchestrator.InitializeAsync to use cascade save + AddFragment(ContextFragmentEntity) overload + removed 4 dead junction-table methods

- [x] 7. PromptBuilder sensory data — context budget (char/4 estimate), time since last prompt

- [x] 2. Update remaining repos (Tag, AuditLog, Source, ActionLog) to match new base pattern
- [x] Dead code pass — removed ActionEnvelope, ProcessingStarted, ModelClientFactory/IModelClientFactory, EnumerableExtensions

- [x] 5. Implement ManageContext handler — batched commands (add/update/remove/fetch), tag path resolution
- [x] Tags in context — always-present tag catalogue in sensory block, slash-separated paths

- [x] 6. Implement ExecuteActionsHandler — batched commands (schedule, cancel_event, list_events, audit, log, query_action_log)
- [x] create_tag command in ManageContext — slash-separated paths, auto-creates parent segments
- [x] Auto-log model actions in TurnHandler.DispatchActionAsync

## Up Next

## Future
- [ ] Dynamic context eviction — when loading fragments into a near-full context, swap out low-weight/low-importance fragments automatically
