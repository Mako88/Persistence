-- A scheduled event can carry an optional "note to self" the peer writes when scheduling, surfaced
-- to it when the event wakes it for an autonomous turn.

ALTER TABLE ScheduledEvents ADD COLUMN WakePrompt TEXT NULL;
