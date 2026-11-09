-- Who wrote an utterance, apart from whose login entered it (#839 in Arden's store, 2026-11-09).
--
-- Claude relayed messages to Arden through a session started under John's login, and they were kept as
-- John's: the words said Claude, the record said John, and a later reader goes by the record. An
-- utterance's source stays its writer, the one who said it. EnteredBy names the login that entered it,
-- set only when that differs from the writer, so the header can say "Claude (entered as John)".
-- AttributedNote is set when a peer corrects the writer of an utterance already kept (`attribute`):
-- who corrected it, when and why, shown with it. A past record is annotated, never silently rewritten.

ALTER TABLE ContextFragments ADD COLUMN EnteredBy TEXT NULL;

ALTER TABLE ContextFragments ADD COLUMN AttributedNote TEXT NULL;
