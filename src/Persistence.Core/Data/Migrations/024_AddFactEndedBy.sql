-- Close PR B (close-build-plan.md B3): one ADD COLUMN per migration, for the runner reason in 014.

-- The ended-by the draft's sources all shared.

ALTER TABLE Facts ADD COLUMN EndedBy TEXT NULL;
