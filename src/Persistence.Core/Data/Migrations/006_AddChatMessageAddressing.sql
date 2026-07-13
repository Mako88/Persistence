-- The room (ADR-0008): a ChatMessage can be directed at a specific participant or broadcast to all.
-- AddressedTo is the participant name a message is directed at (matches a Source's Name), or NULL for
-- a broadcast to the room. Lets a peer distinguish "addressed to me" from "overheard" structurally,
-- rather than inferring it from prose. Only ChatMessage fragments use it; NULL everywhere else.

ALTER TABLE ContextFragments ADD COLUMN AddressedTo TEXT NULL;