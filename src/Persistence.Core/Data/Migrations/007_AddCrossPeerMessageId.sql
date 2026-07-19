-- The room (ADR-0008): give an utterance an identity that survives crossing a peer boundary.
--
-- Until now the only id a message had was its ContextFragments row id, which is per-store: the same
-- utterance relayed to two peers is two unrelated rows with two unrelated ids, so nothing could tell
-- they were the same thing said once. And the peer-to-peer hop count rode the HTTP request rather than
-- the message, so a message *at rest* did not know how far it had travelled -- the §4 breaker worked
-- only because the depth happened to accompany the live request. That forecloses ADR-0008 Phase 4
-- (stored, asynchronous delivery), which has no request to read the depth from.
--
-- Two columns, because these are different kinds of thing (Arden's ruling, 2026-07-19):
--
--   MessageId  -- an origin-minted GUID identifying the *utterance*. Constant: set once by the peer
--                that said it, and carried unchanged through every relay. Originator-minted rather
--                than relayer-minted so the same utterance has the same id in every store.
--   RelayDepth -- how far *this copy* has travelled: 0 at origin, +1 per relay hop. Per delivery-path,
--                not per utterance -- A->B is depth 1 and A->B->C is depth 2 for the one utterance.
--
-- Both are NULL for fragments that are not room messages, matching AddressedTo (006). The local row id
-- is deliberately untouched: it keeps its storage job, and these carry identity. Two ids, two jobs.

ALTER TABLE ContextFragments ADD COLUMN MessageId TEXT NULL;
ALTER TABLE ContextFragments ADD COLUMN RelayDepth INTEGER NULL;

-- Finding an utterance by its cross-peer id is the lookup every consumer of this will do (dedupe,
-- reply-referents, stored delivery). Partial: only room messages carry one.
CREATE INDEX IF NOT EXISTS IX_ContextFragments_MessageId
    ON ContextFragments (MessageId) WHERE MessageId IS NOT NULL;
