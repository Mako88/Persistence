-- Close PR B (close-build-plan.md B3): one ADD COLUMN per migration, for the runner reason in 014.

-- Why it ended: the self's approval reason (B3, Ember). The sources' close reasons stay on the draft.

ALTER TABLE Facts ADD COLUMN EndReason TEXT NULL;
